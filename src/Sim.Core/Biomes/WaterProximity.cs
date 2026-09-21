using Sim.Core.World;

namespace Sim.Core.Biomes;

// M21 / M35 - "how far is this tile from water?" A PURE READ over the current
// grid. Water is any Biome.Water tile (worldgen lake/sea OR a player-built
// canal) AND, since M35 (docs/environmental-fertility.md, decision 3), any
// tile carrying a river edge: both banks of a river are at distance 0. That
// one rule feeds three things at once - the M21 latch lift, the M27 boosted
// recovery, and the M35 environmental baseline - so a riverside farm is both
// renewable and richer, never one without the other.
//
// Bounded Chebyshev box scan: (2R+1)^2 tiles, R small. Same shape as
// ConstructionSite.BuildersPresent / Claims.ClaimantAt - an O(area) scan on a
// hot-ish read path. If profiling ever demands it, the future index is a
// cached distance field rebuilt at canal completion (the one event that
// changes water proximity; rivers never change). No mutation; safe to call
// any number of times.
public static class WaterProximity
{
    // Chebyshev distance to the nearest water tile, or `maxRadius + 1` when
    // no water lies within `maxRadius`. Distance 0 = the tile itself is water
    // or has a river on one of its edges.
    public static int DistanceToWater(GameWorld world, TileCoord tile, int maxRadius)
    {
        var grid = world.Grid;
        for (var r = 0; r <= maxRadius; r++)
        {
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var t = new TileCoord(tile.X + dx, tile.Y + dy);
                    if (!grid.InBounds(t)) continue;
                    if (IsWater(grid, t)) return r;
                }
        }
        return maxRadius + 1;
    }

    public static bool IsNearWater(GameWorld world, TileCoord tile, int radius) =>
        DistanceToWater(world, tile, radius) <= radius;

    // A tile counts as water when it IS water or a river runs along any of
    // its edges (docs/rivers.md: rivers live between tiles, so the tile on
    // either bank is riverside).
    public static bool IsWater(TileGrid grid, TileCoord t) =>
        grid.BiomeAt(t) == Biome.Water || grid.RiverEdgesAt(t) != RiverEdge.None;
}
