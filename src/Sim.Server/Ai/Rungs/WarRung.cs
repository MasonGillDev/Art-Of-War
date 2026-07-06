using Sim.Core.Diplomacy;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// M25 — War: the Rival's diplomacy policy (docs/m25-rival-spec.md).
// ONE WAR AT A TIME (a constant, not a knob): if any war involving us is
// telegraphed or effective, this rung MANAGES it (sue for peace when
// losing, stand ready otherwise — the marching is ConquerRung's job);
// only a Rival at peace considers declaring.
//
// DECLARE only when every gate holds — wars are picked, not stumbled
// into:
//   * the colony can afford it: population >= CampaignPopulationFloor,
//     no famine, food runway above the Eat rung's floor;
//   * the garrison is already at its peacetime quota (the war chest
//     doesn't raid the home guard);
//   * FRESH castle intel on a living foe (<= IntelStaleTicks — a
//     week-old sighting is not a war plan);
//   * ADVANTAGE: own military power ×100 >= the enemy estimate ×
//     WarAdvantageRatioPercent. The estimate prices their VISIBLE units
//     near their castle via the combat catalog (equipment is fogged —
//     the ratio absorbs the underestimate), floored by
//     AssumedGarrisonPower (never plan against zero).
//
// TARGET = the NEAREST fresh castle — march time is the real cost;
// prosperity-envy is someone else's doctrine. The declaration starts the
// telegraph (DiplomacyConfig.Delay): the victim SEES it coming — that's
// the fairness contract — and the Rival mobilizes inside the same window
// (MusterRung's offense budget keys off AtWarOrMobilizing).
//
// PEACE: target defeated → the hint clears and the next think starts
// fresh (full conquest loops until GameOverEvent). LOSING (own military
// power below RetreatBelowPercent of the estimate) → propose Neutral on
// a cooldown (own outgoing offers aren't wire-visible — the throttle is
// memory, not observation) and ACCEPT any Neutral offer from the foe.
// While winning, incoming offers are ignored — lazy expiry buries them.
public sealed class WarRung : IRung
{
    // Re-proposal cadence while losing. A rule constant, not a knob:
    // once a game-day is polite persistence, not spam.
    private const long PeaceCooldownTicks = Sim.Core.Time.Day;

    public Decision? TryClaim(ThinkContext ctx)
    {
        // The hint is droppable; the VIEW is authority. Clear a target
        // that fell or made peace; adopt a war we're already in (a
        // restart, or a war declared ON us).
        if (ctx.Mem.WarTarget is { } t
            && (ctx.IsDefeated(t) || (!ctx.AtWarWith(t) && !ctx.PendingWarWith(t))))
            ctx.Mem.WarTarget = null;
        if (ctx.Mem.WarTarget is null)
            ctx.Mem.WarTarget = ctx.View.Factions
                .Where(f => f.Id != ctx.PlayerId && !f.Defeated
                    && (ctx.AtWarWith(f.Id) || ctx.PendingWarWith(f.Id)))
                .OrderBy(f => f.Id)
                .Select(f => (int?)f.Id).FirstOrDefault();

        return ctx.Mem.WarTarget is { } target
            ? ManageWar(ctx, target)
            : ConsiderDeclaring(ctx);
    }

    private static Decision? ManageWar(ThinkContext ctx, int target)
    {
        var own = OwnMilitaryPower(ctx);
        var theirs = EnemyIntel.EstimateFactionPower(ctx, target);
        var losing = own * 100 < theirs * ctx.Cfg.RetreatBelowPercent;
        if (!losing) return null;   // steady on — Conquer/Raid carry the war

        var intents = new List<Intent>();
        // Take any open Neutral offer from the foe…
        foreach (var p in ctx.View.IncomingProposals)
            if (p.ProposerId == target
                && p.DesiredState == (int)RelationshipState.Neutral)
                intents.Add(new RespondToProposalIntent(ctx.PlayerId, p.Id, accept: true)
                    { PlayerId = ctx.PlayerId });
        // …and extend our own, on the cooldown.
        if (ctx.Mem.LastPeaceProposedTick is not { } last
            || ctx.Now - last >= PeaceCooldownTicks)
        {
            ctx.Mem.LastPeaceProposedTick = ctx.Now;
            intents.Add(new ProposeRelationshipIntent(ctx.PlayerId, target,
                RelationshipState.Neutral) { PlayerId = ctx.PlayerId });
        }
        if (intents.Count == 0) return null;
        return new Decision("war",
            $"losing ({own}pw vs ~{theirs}pw) — suing {target} for peace", intents);
    }

    private static Decision? ConsiderDeclaring(ThinkContext ctx)
    {
        // Affordability first (all cheap reads — bail early).
        if (ctx.View.Population < ctx.Cfg.CampaignPopulationFloor) return null;
        if (ctx.View.InFamine) return null;
        if (ctx.View.FoodRunwayTicks >= 0
            && ctx.View.FoodRunwayTicks < ctx.Cfg.FoodRunwayFloorTicks) return null;

        // The home guard stands before the war chest opens — same
        // peacetime formula as MusterRung.
        var peacetime = Math.Min(
            ctx.Cfg.SoldierQuotaFloor
                + ctx.Own.Count / Math.Max(1, ctx.Cfg.SoldiersPerStructures),
            ctx.View.Population / Math.Max(1, ctx.Cfg.PopulationPerSoldier));
        var soldiers = ctx.OwnUnits.Count(u => (UnitRole)u.Role == UnitRole.Soldier);
        if (soldiers < peacetime) return null;

        // Nearest living foe with FRESH castle intel.
        var known = ctx.Mem.KnownEnemyCastles
            .Where(kv => !ctx.IsDefeated(kv.Key)
                && ctx.Now - kv.Value.Tick <= ctx.Cfg.IntelStaleTicks)
            .OrderBy(kv => Math.Max(Math.Abs(kv.Value.Tile.X - ctx.CastleTile.X),
                Math.Abs(kv.Value.Tile.Y - ctx.CastleTile.Y)))
            .ThenBy(kv => kv.Key)
            .ToList();
        if (known.Count == 0) return null;
        var (target, castle) = (known[0].Key, known[0].Value);

        // ADVANTAGE is judged on MOBILIZABLE power, not the standing army.
        // The peacetime quota deliberately caps soldiers at pop/8, and the
        // offense budget only unlocks once a war is telegraphed — gating the
        // declaration on CURRENT power was a chicken-and-egg that kept
        // organic Rivals at peace forever (the first live playtest found
        // it; the integration tests had masked it by injecting soldiers).
        // The declaration is strategic — "we can RAISE an army that wins;
        // the telegraph window is our mobilization time" — priced at bare
        // soldiers (equipment isn't plannable). ConquerRung's GO gate
        // (AttackOvermatchPercent) still re-checks the REAL assembled army
        // before anyone marches: declaration strategic, GO operational.
        var mobilizable = Math.Min(
            peacetime + ctx.View.Population / Math.Max(1, ctx.Cfg.OffensePopulationPerSoldier),
            ctx.View.Population / Math.Max(1, ctx.Cfg.WarPopulationPerSoldier));
        var potential = Math.Max(OwnMilitaryPower(ctx),
            mobilizable * Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower);
        var theirs = EnemyIntel.EstimateFactionPower(ctx, target, castle.Tile);
        if (potential * 100 < theirs * ctx.Cfg.WarAdvantageRatioPercent) return null;

        ctx.Mem.WarTarget = target;
        return new Decision("war",
            $"declaring on {target} (~{potential}pw mobilizable vs ~{theirs}pw at {castle.Tile.X},{castle.Tile.Y})",
            new List<Intent> { new DeclareWarIntent(ctx.PlayerId, target)
                { PlayerId = ctx.PlayerId } });
    }

    // Own soldiers + archers at their REAL power (own units cross the
    // wire with buffs applied).
    internal static int OwnMilitaryPower(ThinkContext ctx) =>
        ctx.OwnUnits
            .Where(u => (UnitRole)u.Role is UnitRole.Soldier or UnitRole.Archer)
            .Sum(u => u.Power >= 0 ? u.Power
                : Sim.Core.Combat.UnitCombatCatalog.Spec((UnitRole)u.Role).BasePower);
}
