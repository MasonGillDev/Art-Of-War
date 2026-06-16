namespace Sim.Core.Canals;

// M21 — a tile is "canal-reserved" while an in-flight canal ConstructionSite
// lists it in its path. Mirrors Claims.ClaimantAt exactly: reservation is
// DERIVED by scanning the in-progress sites, never stored in a separate world
// collection — so it round-trips through the snapshot for free (the site
// carries its CanalPath) and can never drift out of sync. The exclusion is
// consulted by PlaceCanalIntent, PlaceSiteIntent, and Claims.ValidateOne so
// nothing can build on, claim, or re-canal a tile already promised to a canal
// under construction.
//
// O(structures × path length), same scaling shape as Claims.ClaimantAt; the
// future optimization (if structure counts ever demand it) is a per-tile
// reservation index. PURE READ — never mutates.
public static class CanalReservation
{
    public static bool IsReserved(GameWorld world, TileCoord tile)
    {
        foreach (var s in world.Structures.Values)
            if (s is ConstructionSite { TargetKind: StructureKind.Canal } c
                && c.CanalPath.Contains(tile))
                return true;
        return false;
    }
}
