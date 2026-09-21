using Sim.Core.World;

namespace Sim.Core.Biomes;

// M35 - how deep inside a forest mass is this tile? (docs/environmental-
// fertility.md). PURE READ over the WORLDGEN grid (TileGrid.BiomeAt), never
// the derived BiomeAt: the lazy fertility field only admits baseline changes
// at events, and the one event that changes the worldgen grid is canal
// completion. A depth read from the derived biome would move a tile's
// baseline whenever a neighbour crossed a band, with no catch-up anywhere.
//
// Depth 0 = an edge tile (some 8-neighbour is not Forest, or the map ends).
// Depth k = every tile within Chebyshev distance k is Forest, but not k+1.
// Capped at `maxRings`, so the scan is a bounded (2R+3)^2 box - the Grand
// World halo shape, same as WaterProximity. Non-Forest tiles return 0.
public static class ForestDepth
{
    public static int At(GameWorld world, TileCoord tile, int maxRings)
    {
        var grid = world.Grid;
        if (!grid.InBounds(tile) || grid.BiomeAt(tile) != Biome.Forest) return 0;
        // Ring r is "all Forest" iff every tile at Chebyshev distance exactly
        // r is in-bounds Forest. Depth = number of consecutive all-Forest
        // rings starting at r = 1, capped.
        for (var r = 1; r <= maxRings; r++)
        {
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var t = new TileCoord(tile.X + dx, tile.Y + dy);
                    if (!grid.InBounds(t) || grid.BiomeAt(t) != Biome.Forest)
                        return r - 1;
                }
        }
        return maxRings;
    }
}
