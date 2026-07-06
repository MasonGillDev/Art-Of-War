namespace Sim.Core.Fortifications;

// M26 — fortification passability (docs/walls-and-gates.md). The two
// blocking predicates are PURE READS (architecture §2.2): A* calls
// BlocksPlan many times per query via MovementCost.PlanCost, and the hop
// scheduler / arrival events call BlocksMover per hop. Both are a single
// dictionary lookup + spec read — no scans.
//
// The fog split mirrors MovementCost's crowding contract:
//
//   BlocksPlan  — PLAYER-PERSPECTIVE. A blocking structure bends the
//     planner's route only if the planning player OWNS it (you know your
//     own walls) or can currently SEE its tile. An unseen wall — enemy or
//     allied — is learned by bonking into it: the ground-truth checks stop
//     the mover at its face. (Ignorance has consequences; enemy wall
//     layouts must not leak through pathfinding.)
//
//   BlocksMover — GROUND TRUTH. What actually stops a mover entering the
//     tile, regardless of fog. Checked when a hop is scheduled and again
//     when the arrival fires (a wall can complete, or a gate turn hostile,
//     during the hop's travel time).
//
// Blocking is ENTRY-ONLY: both predicates answer "may this mover enter?"
// A unit already standing on a fortification tile (the builder who just
// finished it) can always walk off — costs are paid on entered tiles only.
public static class Fortification
{
    // Ground truth: does the structure on `tile` (if any) block `moverOwnerId`
    // from entering? Gates (AlliedPassage) open for the owner and Ally
    // factions, evaluated live against current diplomacy.
    public static bool BlocksMover(GameWorld world, TileCoord tile, int moverOwnerId)
    {
        if (!world.Structures.TryGetValue(tile, out var s)) return false;
        if (!StructureCatalog.TryGetSpec(s.Kind, out var spec)) return false;
        if (!spec.BlocksMovement) return false;
        if (spec.AlliedPassage && IsOwnOrAllied(world, s.OwnerId, moverOwnerId)) return false;
        return true;
    }

    // Player-perspective: does the planner (playerId, seeing visibleTiles)
    // KNOW this tile is blocked for them? Own blockers always count;
    // everyone else's only when the tile is currently visible.
    public static bool BlocksPlan(
        GameWorld world, TileCoord tile, int playerId, HashSet<TileCoord> visibleTiles)
    {
        if (!world.Structures.TryGetValue(tile, out var s)) return false;
        if (!StructureCatalog.TryGetSpec(s.Kind, out var spec)) return false;
        if (!spec.BlocksMovement) return false;
        if (s.OwnerId != playerId && !visibleTiles.Contains(tile)) return false;
        if (spec.AlliedPassage && IsOwnOrAllied(world, s.OwnerId, playerId)) return false;
        return true;
    }

    // A standing fortification: a blocking kind that is still a live siege
    // target. Health > 0 excludes nothing today (Wall/Gate specs both carry
    // BaseHealth > 0) but keeps the predicate honest if an indestructible
    // blocking kind ever lands.
    public static bool IsStandingFortification(Structure s) =>
        StructureCatalog.TryGetSpec(s.Kind, out var spec)
        && spec.BlocksMovement
        && s.Health > 0;

    // Same rule as EmbarkIntent.IsOwnOrAllied (allied docks). Self or Ally.
    internal static bool IsOwnOrAllied(GameWorld world, int ownerA, int ownerB) =>
        ownerA == ownerB
        || world.Diplomacy.RelationshipBetween(ownerA, ownerB)
            == Sim.Core.Diplomacy.RelationshipState.Ally;
}
