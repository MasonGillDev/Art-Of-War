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
    //
    // ARMS ARE INFERRED FROM THE FORGE (2026-09-19): a faction whose Smithy
    // the brain has SEEN (visible now, or remembered — structures are
    // physical land use, revealed by scouting) is priced as if every
    // soldier carries the catalog's best power item for its role. The
    // loadout itself stays private; the smithy is the tell. Without this
    // an armed defender reads as bare and the Rival's overmatch gate
    // launches campaigns it loses (docs/manned-towers.md names the same
    // failure for towers: "the Rival must learn X or it will suicide
    // into it").
    public static int EstimateVisiblePower(ThinkContext ctx, int ownerId, TileCoord near, int radius)
    {
        var armed = HasKnownSmithy(ctx, ownerId);
        var power = 0;
        foreach (var u in ctx.View.Units)
        {
            if (u.OwnerId != ownerId) continue;
            if (Math.Max(Math.Abs(u.X - near.X), Math.Abs(u.Y - near.Y)) > radius) continue;
            var role = (UnitRole)u.Role;
            power += Sim.Core.Combat.UnitCombatCatalog.Spec(role).BasePower;
            if (armed) power += AssumedArmsPower(role);
        }
        return power;
    }

    // M43 — what a force is worth at a chokepoint that lets only `slots` of it fight at once
    // (the castle gate): the strongest `slots` at full weight, the rest as a reserve at
    // ReserveWeightPercent. A force that fits is worth its plain sum.
    public static int AssaultPower(ThinkContext ctx, IEnumerable<int> powers, int slots)
    {
        var ordered = powers.OrderByDescending(p => p).ToList();
        var front = ordered.Take(slots).Sum();
        var reserve = ordered.Skip(slots).Sum();
        return front + reserve * ctx.Cfg.ReserveWeightPercent / 100;
    }

    public static bool HasKnownSmithy(ThinkContext ctx, int ownerId) =>
        ctx.View.Structures.Any(s => s.OwnerId == ownerId
            && (StructureKind)s.Kind == StructureKind.Smithy)
        || ctx.Mem.KnownEnemyStructures.Values.Any(k =>
            k.OwnerId == ownerId && (StructureKind)k.Kind == StructureKind.Smithy);

    // The strongest PowerModifier the equipment catalog offers this role
    // (0 for civilians). A rule-catalog read, like BasePower.
    private static int AssumedArmsPower(UnitRole role)
    {
        var best = 0;
        foreach (var item in new[] { Resource.Sword, Resource.Bow, Resource.Shield })
        {
            var spec = Sim.Core.Equipment.EquipmentCatalog.Spec(item);
            if (spec.AllowedRoles.Contains(role) && spec.PowerModifier > best)
                best = spec.PowerModifier;
        }
        return best;
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
