using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// M25 — Conquer: the campaign (docs/m25-rival-spec.md). WarRung picks the
// fight; this rung walks it to the enemy castle and lets M24 do the rest —
// combat auto-triggers on hostile contact, defenders shield their castle
// (so kill-defenders-first is automatic), an undefended castle bleeds HP
// per round, razing defeats the owner, GameOverEvent ends the game.
//
// An OBSERVATION-DRIVEN state machine; the only remembered bit is
// CampaignLaunched (assemble vs march — dropping it on restart walks the
// army home to reassemble; everything else re-derives from the view):
//
//   PREP     — designate the soldiers ABOVE the peacetime garrison
//              (cross-think ownership, ledger #6: the garrison never
//              marches, Defend/demob/staffing skip campaigners for free).
//   ASSEMBLE — converge the roster on the rally (Barracks, else castle):
//              multi-round combat punishes trickle-in, so nobody marches
//              until everybody stands together.
//   LAUNCH   — war EFFECTIVE (no shot before AreHostile — the telegraph
//              pin) AND assembled AND campaign power ×100 clears the
//              estimate × AttackOvermatchPercent (declaration was
//              strategic; GO is operational — they may have mustered
//              during the telegraph).
//   MARCH    — everyone walks to the castle tile. Orders go only to
//              idle-STILL units (DestX >= 0 = busy — the Idle-while-
//              moving replay pin, docs/bandits.md); a unit pinned by an
//              intercept fights or re-marches next think.
//   SIEGE    — standing on the castle IS the siege; nothing to emit.
//   WITHDRAW — home famine, or campaign power below RetreatBelowPercent
//              of the estimate: parity lost is campaign over, not a
//              death spiral. Everyone walks home, the roster clears
//              (WarRung sues for peace on its own losing test).
//
// Victory needs no branch here: the razed castle vanishes from intel
// (EnemyIntel observes Rubble), the owner's Defeated flag crosses the
// wire, WarRung drops the target, and the no-war branch below stands the
// survivors down — full conquest loops when WarRung picks the next foe.
public sealed class ConquerRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        // Roster hygiene: the fallen and the retrained leave the campaign.
        ctx.Mem.CampaignSoldiers.RemoveWhere(id =>
            ctx.OwnUnits.FirstOrDefault(u => u.Id == id) is not { } u
            || (UnitRole)u.Role != UnitRole.Soldier);

        // No war → stand down whatever remains.
        if (ctx.Mem.WarTarget is not { } target || ctx.IsDefeated(target))
            return StandDown(ctx, "war over");

        // No castle on the intel map (razed and observed, or dropped on a
        // restart): the campaign has no destination — stand down and let
        // Probe re-earn the eyes.
        if (!ctx.Mem.KnownEnemyCastles.TryGetValue(target, out var castle))
            return StandDown(ctx, "no target castle known");
        var objective = new TileCoord(castle.Tile.X, castle.Tile.Y);

        var roster = ctx.OwnUnits
            .Where(u => ctx.Mem.CampaignSoldiers.Contains(u.Id))
            .OrderBy(u => u.Id).ToList();
        var campaignPower = AssaultPower(ctx, roster);
        var estimate = EnemyIntel.EstimateFactionPower(ctx, target, castle.Tile);

        // WITHDRAW — the Sparta abort (famine at home) or parity lost.
        if (ctx.Mem.CampaignLaunched
            && (ctx.View.InFamine
                || campaignPower * 100 < estimate * ctx.Cfg.RetreatBelowPercent))
            return StandDown(ctx,
                $"withdrawing ({campaignPower}pw vs ~{estimate}pw{(ctx.View.InFamine ? ", famine at home" : "")})");

        if (ctx.Mem.CampaignLaunched)
        {
            // MARCH / SIEGE: re-order any idle-still campaigner not on the
            // objective. Standing on the castle needs no further orders —
            // the combat rounds are the siege.
            var moves = roster
                .Where(u => ctx.IsIdleStill(u) && (u.X != objective.X || u.Y != objective.Y))
                .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, objective)
                    { PlayerId = ctx.PlayerId })
                .ToList();
            return moves.Count == 0 ? null
                : new Decision("conquer", $"marching on {target}'s castle at {objective.X},{objective.Y}", moves);
        }

        // PREP — claim every soldier above the peacetime garrison (same
        // formula as MusterRung; the offense budget upstream is what grew
        // the roster past it).
        var peacetime = Math.Min(
            ctx.Cfg.SoldierQuotaFloor
                + ctx.Own.Count / Math.Max(1, ctx.Cfg.SoldiersPerStructures),
            ctx.View.Population / Math.Max(1, ctx.Cfg.PopulationPerSoldier));
        var soldiers = ctx.OwnUnits.Where(u => (UnitRole)u.Role == UnitRole.Soldier)
            .OrderBy(u => u.Id).ToList();
        var want = Math.Max(0, soldiers.Count - peacetime);
        foreach (var u in soldiers)
        {
            if (ctx.Mem.CampaignSoldiers.Count >= want) break;
            if (ctx.Mem.CampaignSoldiers.Contains(u.Id)) continue;
            if (!ctx.IsFree(u)) continue;   // never poach another job's unit
            ctx.Mem.CampaignSoldiers.Add(u.Id);
            roster.Add(u);
        }
        if (roster.Count == 0) return null;

        // ASSEMBLE at the rally.
        var rally = ctx.OwnStructure(StructureKind.Barracks) is { } b
            ? ThinkContext.TileOf(b) : ctx.CastleTile;
        var toRally = roster
            .Where(u => ctx.IsIdleStill(u) && (u.X != rally.X || u.Y != rally.Y)
                // M43: a full rally spills its extras onto the tile beside; they count as
                // assembled there instead of being marched back every think.
                && !(Math.Max(Math.Abs(u.X - rally.X), Math.Abs(u.Y - rally.Y)) <= 1
                     && !ctx.HasRoomAt(rally)))
            .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, rally)
                { PlayerId = ctx.PlayerId })
            .ToList();
        if (toRally.Count > 0)
            return new Decision("conquer", $"assembling {roster.Count} at the rally", toRally);

        // LAUNCH — everyone stands at the rally; the war must BE a war
        // (the telegraph pin lives here) and the odds must clear the GO
        // gate with fresh eyes.
        if (!ctx.AtWarWith(target)) return null;   // telegraph still running
        campaignPower = AssaultPower(ctx, roster);
        if (campaignPower * 100 < estimate * ctx.Cfg.AttackOvermatchPercent) return null;

        ctx.Mem.CampaignLaunched = true;
        var march = roster
            .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, objective)
                { PlayerId = ctx.PlayerId })
            .ToList();
        return new Decision("conquer",
            $"LAUNCH: {roster.Count} soldiers ({campaignPower}pw vs ~{estimate}pw) on {target}",
            march);
    }

    // The roster's worth at the castle gate: only CastleAttackerSlots of it fight at once
    // (the board's per-side cap), the rest wait as a reserve.
    private static int AssaultPower(ThinkContext ctx, List<UnitDto> roster) =>
        EnemyIntel.AssaultPower(ctx, roster.Select(u => u.Power >= 0 ? u.Power
            : Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower),
            ctx.Cfg.CastleAttackerSlots);

    // Clear the roster and walk the survivors home. Emitting and clearing
    // in the same think is safe: this think's context already carries the
    // designations, and next think the veterans are ordinary soldiers
    // again (Defend's stand-down or Muster's demob takes them from there).
    private static Decision? StandDown(ThinkContext ctx, string why)
    {
        if (ctx.Mem.CampaignSoldiers.Count == 0)
        {
            ctx.Mem.CampaignLaunched = false;
            return null;
        }
        var moves = ctx.OwnUnits
            .Where(u => ctx.Mem.CampaignSoldiers.Contains(u.Id) && ctx.IsIdleStill(u)
                && (u.X != ctx.CastleTile.X || u.Y != ctx.CastleTile.Y))
            .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile)
                { PlayerId = ctx.PlayerId })
            .ToList();
        ctx.Mem.CampaignSoldiers.Clear();
        ctx.Mem.CampaignLaunched = false;
        return moves.Count == 0 ? null
            : new Decision("conquer", $"{why} — campaign stands down", moves);
    }
}
