using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Scavenging dead kingdoms (2026-07-13) — when a faction falls (castle
// razed, or its last soul dead), PlayerDefeatedEvent marks it Enemy with
// everyone: its ruins are anyone's. This rung runs the expedition that
// collects: a bounded party (ScavengePartySize, ledger #9) of SURPLUS
// soldiers — above the peacetime quota, after the campaign and raid
// parties took theirs (ledger #5) — marches to the fallen kingdom's
// known structures, razes them by PRESENCE (the M24 auto-siege: standing
// on a hostile structure's tile grinds it down; no orders needed while
// combat works), and hauls the spilled vaults home. RazeStructure spills
// holdings to the tile, so the razer is already standing on the loot
// when it lands.
//
// THE FREIGHT PHASE: a soldier lugs 5, a castle vault is hundreds. Once
// nothing is left to raze, spare Haulers (above HaulerFloor — the
// logistics backbone stays home) join the party for the 25-capacity
// freight loop over the remembered spill piles (Mem.ScavengePiles — the
// wire only vouches for piles in current sight, so the loop needs its
// own map of where the loot lies; entries clear when re-observed empty).
//
// WHAT IT WON'T DO: walk into a LIVING kingdom's yard — remembered piles
// inside PursuitLeashTiles of a known living foe's castle are never
// recorded (ground piles are unowned and looting them is legal, but
// skimming a live rival's battlefields is how wars start); near-home
// piles (inside the salvage leash) are left to SalvageRung's civilians;
// walls and gates are never march targets (they block movement — the
// move would just reject; FortSiege grinds any wall the party happens to
// stand beside for free).
public sealed class ScavengeRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        if (ctx.Cfg.ScavengePartySize <= 0) return null;

        // Roster hygiene: the dead leave the party (roles are mixed by
        // design — soldiers raze, haulers freight).
        ctx.Mem.ScavengeParty.RemoveWhere(id => ctx.OwnUnits.All(u => u.Id != id));

        // PERCEIVE the loot field: remember visible spill piles inside
        // the expedition range but beyond the civilian salvage leash,
        // never in a living foe's yard...
        foreach (var p in ctx.View.Piles)
        {
            var d = Cheb(p.X, p.Y, ctx.CastleTile.X, ctx.CastleTile.Y);
            if (d > ctx.Cfg.ScavengeRangeTiles || d <= ctx.Cfg.SalvageLeashTiles) continue;
            if (ctx.Mem.KnownEnemyCastles.Any(kv => !ctx.IsDefeated(kv.Key)
                    && Cheb(p.X, p.Y, kv.Value.Tile.X, kv.Value.Tile.Y)
                        <= ctx.Cfg.PursuitLeashTiles))
                continue;
            ctx.Mem.ScavengePiles.Add((p.X, p.Y));
        }
        // ...and forget the ones re-observed empty (current sight only —
        // the wire vouches for nothing it can't see).
        ctx.Mem.ScavengePiles.RemoveWhere(t =>
            ctx.VisibleTiles.Contains(t) && ctx.PileAt(t.X, t.Y) is null);

        // Ruins: known structures of DEFEATED factions inside range.
        var ruins = ctx.Mem.KnownEnemyStructures
            .Where(kv => ctx.IsDefeated(kv.Value.OwnerId)
                && (StructureKind)kv.Value.Kind is not (StructureKind.Wall or StructureKind.Gate)
                && Cheb(kv.Key.X, kv.Key.Y, ctx.CastleTile.X, ctx.CastleTile.Y)
                    <= ctx.Cfg.ScavengeRangeTiles)
            .Select(kv => kv.Key)
            .OrderBy(t => Cheb(t.X, t.Y, ctx.CastleTile.X, ctx.CastleTile.Y))
            .ThenBy(t => t.Y).ThenBy(t => t.X)
            .ToList();
        var piles = ctx.Mem.ScavengePiles
            .OrderBy(t => Cheb(t.X, t.Y, ctx.CastleTile.X, ctx.CastleTile.Y))
            .ThenBy(t => t.Y).ThenBy(t => t.X)
            .ToList();
        if (ruins.Count == 0 && piles.Count == 0) return StandDown(ctx);

        // Muster: surplus soldiers above the peacetime quota, minus every
        // prior claim (the garrison never leaves; the campaign and raid
        // parties reserved first).
        var peacetime = Math.Min(
            ctx.Cfg.SoldierQuotaFloor
                + ctx.Own.Count / Math.Max(1, ctx.Cfg.SoldiersPerStructures),
            ctx.View.Population / Math.Max(1, ctx.Cfg.PopulationPerSoldier));
        var soldiers = ctx.OwnUnits.Count(u => (UnitRole)u.Role == UnitRole.Soldier);
        var soldierTarget = Math.Min(ctx.Cfg.ScavengePartySize, Math.Max(0,
            soldiers - peacetime - ctx.Mem.CampaignSoldiers.Count - ctx.Mem.RaidParty.Count));
        var partySoldiers = ctx.OwnUnits.Count(u =>
            ctx.Mem.ScavengeParty.Contains(u.Id) && (UnitRole)u.Role == UnitRole.Soldier);
        foreach (var u in ctx.OwnUnits
                     .Where(u => (UnitRole)u.Role == UnitRole.Soldier
                         && ctx.IsIdleStill(u) && ctx.IsFree(u))
                     .OrderBy(u => u.Id))
        {
            if (partySoldiers >= soldierTarget) break;
            ctx.Mem.ScavengeParty.Add(u.Id);
            partySoldiers++;
        }
        // Freight phase: nothing left to raze — spare Haulers join.
        if (ruins.Count == 0)
        {
            var spareHaulers = Math.Max(0,
                ctx.OwnUnits.Count(u => (UnitRole)u.Role == UnitRole.Hauler)
                    - ctx.Cfg.HaulerFloor);
            foreach (var u in ctx.OwnUnits
                         .Where(u => (UnitRole)u.Role == UnitRole.Hauler
                             && ctx.IsIdleStill(u) && ctx.IsFree(u) && u.CargoAmount == 0
                             && u.Age >= ctx.Cfg.MinAdultAgeYears)
                         .OrderBy(u => u.Id))
            {
                if (spareHaulers <= 0
                    || ctx.Mem.ScavengeParty.Count >= ctx.Cfg.ScavengePartySize) break;
                ctx.Mem.ScavengeParty.Add(u.Id);
                spareHaulers--;
            }
        }
        if (ctx.Mem.ScavengeParty.Count == 0) return null;

        var intents = new List<Intent>();
        foreach (var u in ctx.OwnUnits.Where(u => ctx.Mem.ScavengeParty.Contains(u.Id))
                     .OrderBy(u => u.Id))
        {
            if (!ctx.IsIdleStill(u)) continue;   // mid-march / mid-fight — orders on arrival

            if (u.CargoAmount > 0)
            {
                // Loot home, then hand it over (the raid leg, verbatim).
                if (u.X == ctx.CastleTile.X && u.Y == ctx.CastleTile.Y)
                    intents.Add(new UnloadCargoIntent(ctx.Reserve(u).Id) { PlayerId = ctx.PlayerId });
                else
                    intents.Add(new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile) { PlayerId = ctx.PlayerId });
                continue;
            }

            // Standing on loot: load the richest row (and remember the
            // tile — freight trips come back until it's dry).
            if (ctx.PileAt(u.X, u.Y) is { } pileHere)
            {
                var pick = pileHere.Holdings.Where(h => h.Amount > 0)
                    .OrderByDescending(h => h.Amount).ThenBy(h => h.Resource)
                    .FirstOrDefault();
                if (pick is not null)
                {
                    ctx.Mem.ScavengePiles.Add((u.X, u.Y));
                    intents.Add(new LoadCargoIntent(ctx.Reserve(u).Id, (Resource)pick.Resource)
                        { PlayerId = ctx.PlayerId });
                    continue;
                }
            }

            // Standing on a ruin that still stands: HOLD — presence is
            // the siege, and the vault spills right here when it falls.
            if (ruins.Any(r => r.X == u.X && r.Y == u.Y)) continue;

            // March: soldiers to the nearest standing ruin, everyone else
            // (and soldiers in the freight phase) to the nearest pile.
            var isSoldier = (UnitRole)u.Role == UnitRole.Soldier;
            var dest = isSoldier && ruins.Count > 0 ? NearestTo(u, ruins)
                : piles.Count > 0 ? NearestTo(u, piles)
                : ruins.Count > 0 ? NearestTo(u, ruins)
                : ((int X, int Y)?)null;
            if (dest is { } t)
                intents.Add(new MoveIntent(ctx.Reserve(u).Id, new TileCoord(t.X, t.Y))
                    { PlayerId = ctx.PlayerId });
        }

        return intents.Count == 0 ? null
            : new Decision("scavenge",
                $"{ctx.Mem.ScavengeParty.Count} scavenging the fallen " +
                $"({ruins.Count} ruin(s), {piles.Count} pile(s))", intents);
    }

    // Nothing known to raze or loot: carriers walk their cargo home and
    // unload; the empty-handed are released where they stand.
    private static Decision? StandDown(ThinkContext ctx)
    {
        if (ctx.Mem.ScavengeParty.Count == 0) return null;
        var intents = new List<Intent>();
        foreach (var u in ctx.OwnUnits.Where(u => ctx.Mem.ScavengeParty.Contains(u.Id))
                     .OrderBy(u => u.Id).ToList())
        {
            if (u.CargoAmount == 0) { ctx.Mem.ScavengeParty.Remove(u.Id); continue; }
            if (!ctx.IsIdleStill(u)) continue;
            if (u.X == ctx.CastleTile.X && u.Y == ctx.CastleTile.Y)
                intents.Add(new UnloadCargoIntent(ctx.Reserve(u).Id) { PlayerId = ctx.PlayerId });
            else
                intents.Add(new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile) { PlayerId = ctx.PlayerId });
        }
        return intents.Count == 0 ? null
            : new Decision("scavenge", "field stripped — expedition returns", intents);
    }

    private static (int X, int Y) NearestTo(Sim.Server.Wire.UnitDto u, List<(int X, int Y)> tiles) =>
        tiles.OrderBy(t => Cheb(t.X, t.Y, u.X, u.Y)).ThenBy(t => t.Y).ThenBy(t => t.X).First();

    private static int Cheb(int ax, int ay, int bx, int by) =>
        Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));
}
