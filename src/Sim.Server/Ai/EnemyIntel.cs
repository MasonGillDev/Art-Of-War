using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M25 — the Rival's perception pre-pass (docs/m25-rival-spec.md). Runs on
// BrainCore's `perceive` hook, BEFORE the ladder, so the enemy map updates
// every think no matter which rung claims — the same reasoning that put
// threat-memory updates at the top of DefendRung.
//
// FAIRNESS: reads only the ThinkContext (view digest + memory). Foreign
// structures reach the view exactly when a human would see them
// (pos/kind/owner — enrichment stays own-only), and pricing uses
// UnitCombatCatalog, a rule catalog any player reads off the UI.
public static class EnemyIntel
{
    public static void Perceive(ThinkContext ctx)
    {
        // Refresh on sight: every visible foreign structure lands in the
        // memory. Rubble is not an asset (it's the tombstone of one) and
        // sentinel owners (bandit camps someday, caches -2) aren't foes.
        foreach (var s in ctx.View.Structures)
        {
            if (s.OwnerId == ctx.PlayerId || s.OwnerId < 0) continue;
            if ((StructureKind)s.Kind == StructureKind.Rubble) continue;
            ctx.Mem.KnownEnemyStructures[(s.X, s.Y)] = (ctx.Now, s.Kind, s.OwnerId);
            if ((StructureKind)s.Kind == StructureKind.Castle
                && !ctx.Mem.KnownEnemyCastles.ContainsKey(s.OwnerId))
                ctx.Mem.ProbeLeg = 0;   // NEW castle on the map — fresh probe budget
            if ((StructureKind)s.Kind == StructureKind.Castle)
                ctx.Mem.KnownEnemyCastles[s.OwnerId] = ((s.X, s.Y), ctx.Now);
        }

        // Clear on re-observed-gone: a tile in LIVE sight whose remembered
        // structure is no longer there (razed to Rubble, or the site never
        // completed) drops out. One lookup table per think — structures
        // are one-per-tile, so the map is total.
        var byTile = new Dictionary<(int X, int Y), StructDto>();
        foreach (var s in ctx.View.Structures) byTile[(s.X, s.Y)] = s;

        foreach (var key in ctx.Mem.KnownEnemyStructures.Keys.ToList())
        {
            if (!ctx.VisibleTiles.Contains(key)) continue;
            var (_, kind, owner) = ctx.Mem.KnownEnemyStructures[key];
            if (byTile.TryGetValue(key, out var live)
                && live.Kind == kind && live.OwnerId == owner) continue;
            ctx.Mem.KnownEnemyStructures.Remove(key);
        }

        foreach (var (owner, known) in ctx.Mem.KnownEnemyCastles.ToList())
        {
            if (!ctx.VisibleTiles.Contains(known.Tile)) continue;
            if (byTile.TryGetValue(known.Tile, out var live)
                && (StructureKind)live.Kind == StructureKind.Castle
                && live.OwnerId == owner) continue;
            ctx.Mem.KnownEnemyCastles.Remove(owner);
            ctx.Mem.ProbeLeg = 0;   // a castle FELL — the map changed, re-earn eyes
        }
    }

    // Price a faction's VISIBLE presence near a tile via the combat
    // catalog. Foreign units cross the wire with Power hidden (-1), so
    // each is priced at its ROLE's base power — equipment stays invisible,
    // which is the fog doing its job (the caller's overmatch ratio and
    // AssumedGarrisonPower floor absorb the underestimate). Civilians
    // count too: in M7 combat every unit on the tile fights at its
    // catalog power.
    public static int EstimateVisiblePower(ThinkContext ctx, int ownerId, TileCoord near, int radius)
    {
        var power = 0;
        foreach (var u in ctx.View.Units)
        {
            if (u.OwnerId != ownerId) continue;
            if (Math.Max(Math.Abs(u.X - near.X), Math.Abs(u.Y - near.Y)) > radius) continue;
            power += Sim.Core.Combat.UnitCombatCatalog.Spec((UnitRole)u.Role).BasePower;
        }
        return power;
    }

    // The war ledger's enemy estimate (shared by WarRung's gates and
    // ConquerRung's GO/retreat): visible strength near their castle when
    // we know it (the pursuit leash doubles as "their home ground"), all
    // their visible units otherwise — floored by AssumedGarrisonPower,
    // because fog hides garrisons and nobody plans against zero.
    public static int EstimateFactionPower(ThinkContext ctx, int target,
        (int X, int Y)? castleTile = null)
    {
        var tile = castleTile;
        if (tile is null && ctx.Mem.KnownEnemyCastles.TryGetValue(target, out var k))
            tile = k.Tile;
        var seen = tile is { } home
            ? EstimateVisiblePower(ctx, target,
                new TileCoord(home.X, home.Y), ctx.Cfg.PursuitLeashTiles)
            : EstimateVisiblePower(ctx, target, ctx.CastleTile, int.MaxValue);
        return Math.Max(seen, ctx.Cfg.AssumedGarrisonPower);
    }
}
