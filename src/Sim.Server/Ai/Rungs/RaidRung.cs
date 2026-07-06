using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// M25 — Raid: supply-line harassment (docs/m25-rival-spec.md). While the
// war is EFFECTIVE and the campaign hasn't launched, a small bounded
// party (RaidPartySize — ledger #9; 0 = the siege-rush doctrine) loops:
// march to the war target's nearest known EXTRACTOR, LoadCargoIntent the
// buffer (the bandit verb — source ownership unchecked by design), walk
// it home, unload, repeat.
//
// EXTRACTORS ONLY: LoadCargo must NAME the resource, and enemy holdings
// are fogged — but an extractor's KIND names its output through
// StructureCatalog, which is rule knowledge (a human reads the same off
// the UI). Storages/castles stay un-raidable until loot is observable.
// DRY STRIKES are learned the honest way: a raider standing on the
// target empty-handed for consecutive thinks means the buffer is empty —
// the tile is struck from this war's list (the ZeroBufferThinks
// discipline, pointed outward).
//
// LADDER NOTE: Raid sits ABOVE Conquer — deliberately inverted from the
// spec sketch — because Conquer's PREP sweeps every free surplus soldier
// into the campaign roster; the party must reserve its hands first
// (ledger #5: priority is who reserves first, not rung order). At
// LAUNCH the party stands down and the garrison absorbs it.
public sealed class RaidRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        // Roster hygiene, same as the campaign.
        ctx.Mem.RaidParty.RemoveWhere(id =>
            ctx.OwnUnits.FirstOrDefault(u => u.Id == id) is not { } u
            || (UnitRole)u.Role != UnitRole.Soldier);

        var active = ctx.Cfg.RaidPartySize > 0
            && ctx.Mem.WarTarget is { } t && !ctx.IsDefeated(t)
            && ctx.AtWarWith(t)          // no looting inside the telegraph
            && !ctx.Mem.CampaignLaunched;
        if (!active) return StandDown(ctx);
        var target = ctx.Mem.WarTarget!.Value;

        // Nearest known live extractor of the foe, dry strikes excluded.
        var raidable = ctx.Mem.KnownEnemyStructures
            .Where(kv => kv.Value.OwnerId == target
                && !ctx.Mem.DryRaidTargets.Contains(kv.Key)
                && (StructureKind)kv.Value.Kind is StructureKind.Farm
                    or StructureKind.LumberCamp or StructureKind.Quarry or StructureKind.Mine)
            .OrderBy(kv => Cheb(kv.Key, (ctx.CastleTile.X, ctx.CastleTile.Y)))
            .ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .ToList();

        // Top up the party from soldiers the garrison and campaign don't
        // hold (available may be zero — the party waits for muster).
        var soldierCount = ctx.OwnUnits.Count(u => (UnitRole)u.Role == UnitRole.Soldier);
        var peacetime = Math.Min(
            ctx.Cfg.SoldierQuotaFloor
                + ctx.Own.Count / Math.Max(1, ctx.Cfg.SoldiersPerStructures),
            ctx.View.Population / Math.Max(1, ctx.Cfg.PopulationPerSoldier));
        var partyTarget = Math.Min(ctx.Cfg.RaidPartySize,
            Math.Max(0, soldierCount - peacetime - ctx.Mem.CampaignSoldiers.Count));
        foreach (var u in ctx.OwnUnits
                     .Where(u => (UnitRole)u.Role == UnitRole.Soldier
                         && ctx.IsIdleStill(u) && ctx.IsFree(u))
                     .OrderBy(u => u.Id))
        {
            if (ctx.Mem.RaidParty.Count >= partyTarget) break;
            ctx.Mem.RaidParty.Add(u.Id);
        }
        if (ctx.Mem.RaidParty.Count == 0) return null;

        var intents = new List<Intent>();
        var bumped = new HashSet<(int X, int Y)>();   // one dry-strike per tile per THINK
        foreach (var u in ctx.OwnUnits.Where(u => ctx.Mem.RaidParty.Contains(u.Id))
                     .OrderBy(u => u.Id))
        {
            if (!ctx.IsIdleStill(u)) continue;   // mid-march / pinned — orders on arrival

            if (u.CargoAmount > 0)
            {
                // Loot home, then hand it over.
                if (u.X == ctx.CastleTile.X && u.Y == ctx.CastleTile.Y)
                    intents.Add(new UnloadCargoIntent(ctx.Reserve(u).Id) { PlayerId = ctx.PlayerId });
                else
                    intents.Add(new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile) { PlayerId = ctx.PlayerId });
                continue;
            }

            if (raidable.Count == 0) continue;   // no live intel — the party holds
            var (tile, info) = (raidable[0].Key, raidable[0].Value);
            if (ctx.Mem.DryRaidTargets.Contains(tile)) continue;   // struck dry mid-think
            if (u.X == tile.X && u.Y == tile.Y)
            {
                // Standing on it empty-handed: strike once, and learn.
                // Think k: counter 1, emit the load (resolves by k+1).
                // Think k+1 still empty-handed: counter 2 → the buffer is
                // dry — struck from this war's list. One bump per tile
                // per think, or two raiders arriving together would
                // condemn a full farm unloaded.
                var strikes = ctx.Mem.RaidDryThinks.GetValueOrDefault(tile)
                    + (bumped.Add(tile) ? 1 : 0);
                ctx.Mem.RaidDryThinks[tile] = strikes;
                if (strikes >= 2)
                {
                    ctx.Mem.DryRaidTargets.Add(tile);
                    ctx.Mem.RaidDryThinks.Remove(tile);
                    continue;   // next think retargets the survivor list
                }
                var loot = StructureCatalog.Spec((StructureKind)info.Kind).OutputResource;
                intents.Add(new LoadCargoIntent(ctx.Reserve(u).Id, loot) { PlayerId = ctx.PlayerId });
            }
            else
            {
                intents.Add(new MoveIntent(ctx.Reserve(u).Id, new TileCoord(tile.X, tile.Y))
                    { PlayerId = ctx.PlayerId });
            }
        }

        return intents.Count == 0 ? null
            : new Decision("raid", $"{ctx.Mem.RaidParty.Count} raiding {target}'s supply lines", intents);
    }

    // War over, doctrine off, or the campaign launched: the party walks
    // home and the garrison absorbs it. Dry-strike memory goes with it —
    // buffers refill; the next war re-learns.
    private static Decision? StandDown(ThinkContext ctx)
    {
        ctx.Mem.DryRaidTargets.Clear();
        ctx.Mem.RaidDryThinks.Clear();
        if (ctx.Mem.RaidParty.Count == 0) return null;
        var moves = ctx.OwnUnits
            .Where(u => ctx.Mem.RaidParty.Contains(u.Id) && ctx.IsIdleStill(u)
                && (u.X != ctx.CastleTile.X || u.Y != ctx.CastleTile.Y))
            .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile)
                { PlayerId = ctx.PlayerId })
            .ToList();
        ctx.Mem.RaidParty.Clear();
        return moves.Count == 0 ? null
            : new Decision("raid", "party stands down", moves);
    }

    private static int Cheb((int X, int Y) a, (int X, int Y) b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
