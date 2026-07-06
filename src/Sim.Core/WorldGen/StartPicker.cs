namespace Sim.Core.WorldGen;

// Picks a single sensible starting tile for the Castle. Requirements:
//   - Grassland (Castle needs a buildable, cheap-to-move-on tile)
//   - Forest, Hills, and Mountain ALL present within Chebyshev radius
//     `radius` (so every extractor type — LumberCamp, Mine, Quarry —
//     can eventually be built within reach).
//
// Scans CENTER-OUTWARD in the codebase's standard deterministic
// (dist, y, x) ring order and returns the first matching tile. Interior
// bias matters since the ocean border landed (ContinentShaper): the old
// top-left (y, x) scan systematically parked the castle on the northern
// COAST — beach at its back, no meadow, and the balance lab watched
// bandit pressure wipe such colonies. Center of the map ≈ heart of the
// continent. Returns null if no candidate qualifies — caller decides
// (retry with different seed, relax thresholds, etc.).
//
// Fair multi-player start placement is OUT of scope here (single-player
// start only; multi-player concern lands with combat).
public static class StartPicker
{
    public static TileCoord? Pick(Biome[,] grid, int radius)
    {
        var width = grid.GetLength(0);
        var height = grid.GetLength(1);
        int cx = width / 2, cy = height / 2;

        bool Qualifies(int x, int y) =>
            x >= 0 && x < width && y >= 0 && y < height
            && grid[x, y] == Biome.Grassland
            && HasNearby(grid, x, y, radius, Biome.Forest)
            && HasNearby(grid, x, y, radius, Biome.Hills)
            && HasNearby(grid, x, y, radius, Biome.Mountain);

        if (Qualifies(cx, cy)) return new TileCoord(cx, cy);
        var maxR = Math.Max(width, height);
        for (var r = 1; r <= maxR; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // perimeter only
            if (Qualifies(cx + dx, cy + dy)) return new TileCoord(cx + dx, cy + dy);
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
