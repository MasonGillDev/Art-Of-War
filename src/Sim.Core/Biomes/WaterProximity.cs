using Sim.Core.World;

namespace Sim.Core.Biomes;

// M21 — "is this tile within R tiles of any Water?" A PURE READ over the
// current grid. Worldgen lakes/seas and player-built canals are the same
// Biome.Water, so a canal dug toward your fields irrigates them automatically
// (docs/canals.md). Used by BiomeDegradation.DeriveRate to lift the otherwise-
// permanent desert latch on degraded ladder land that sits near water — the
// "where you farm matters" softening of M9 desertification.
//
// Bounded Chebyshev box scan: (2R+1)^2 tiles, R small (default 2 → 25 tiles).
// Same shape as ConstructionSite.BuildersPresent / Claims.ClaimantAt — an
// O(area) scan on a hot-ish read path. If profiling ever demands it, the
// future index is a cached near-water set rebuilt at canal completion (the one
// event that changes water proximity). No mutation; safe to call any number
// of times.
public static class WaterProximity
{
    public static bool IsNearWater(GameWorld world, TileCoord tile, int radius)
    {
        for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                var t = new TileCoord(tile.X + dx, tile.Y + dy);
                if (!world.Grid.InBounds(t)) continue;
                if (world.Grid.BiomeAt(t) == Biome.Water) return true;
            }
        return false;
    }
}
