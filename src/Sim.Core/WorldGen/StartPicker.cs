namespace Sim.Core.WorldGen;

// Picks a single sensible starting tile for the Castle. Requirements:
//   - Grassland (Castle needs a buildable, cheap-to-move-on tile)
//   - Forest, Hills, and Mountain ALL present within Chebyshev radius
//     `radius` (so every extractor type — LumberCamp, Mine, Quarry —
//     can eventually be built within reach).
//
// Scans the grid in (y, x) order and returns the first matching tile.
// Deterministic for a given (grid, radius). Returns null if no
// candidate qualifies — caller decides (retry with different seed,
// relax thresholds, etc.).
//
// Fair multi-player start placement is OUT of scope here (single-player
// start only; multi-player concern lands with combat).
public static class StartPicker
{
    public static TileCoord? Pick(Biome[,] grid, int radius)
    {
        var width = grid.GetLength(0);
        var height = grid.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (grid[x, y] != Biome.Grassland) continue;
                if (HasNearby(grid, x, y, radius, Biome.Forest)
                 && HasNearby(grid, x, y, radius, Biome.Hills)
                 && HasNearby(grid, x, y, radius, Biome.Mountain))
                    return new TileCoord(x, y);
            }
        }
        return null;
    }

    // Chebyshev-radius box scan (matches what Sight.Reveal uses for the
    // outer bound). Returns true on first match — bounded by `radius`,
    // not "iterate all tiles."
    private static bool HasNearby(Biome[,] grid, int cx, int cy, int radius, Biome target)
    {
        var width = grid.GetLength(0);
        var height = grid.GetLength(1);
        var xLo = Math.Max(0, cx - radius);
        var xHi = Math.Min(width - 1, cx + radius);
        var yLo = Math.Max(0, cy - radius);
        var yHi = Math.Min(height - 1, cy + radius);
        for (var y = yLo; y <= yHi; y++)
        {
            for (var x = xLo; x <= xHi; x++)
            {
                if (grid[x, y] == target) return true;
            }
        }
        return false;
    }
}
