namespace Sim.Core.WorldGen;

// The frozen result of one generation run. Grid is a concrete Biome[,] —
// integer-typed, deterministic to the sim. The Seed field is for
// PROVENANCE ONLY (logging, debugging). The sim and replay anchor on
// the grid, never on the seed — re-running the generator is never on
// the replay path.
//
// Rivers is the per-tile edge mask RiverCarver froze alongside the biomes
// (docs/rivers.md) — terrain, like Grid, and shipped the same way.
public sealed record GeneratedMap(Biome[,] Grid, RiverEdge[,] Rivers, TileCoord Start, int Width, int Height, int Seed);

// Top-level entry. Runs the noise → classify → start-pick pipeline once.
public static class MapGenerator
{
    public static GeneratedMap Build(GenerationConfig cfg)
    {
        // ContinentShaper = raw noise + ocean-border mask (island continent).
        // WorldFactory rebuilds the same field for the client heightmap — the
        // two MUST stay one code path or sea and rendered terrain desync.
        var elevation = ContinentShaper.BuildElevation(cfg);
        var moisture  = NoiseField.Generate(cfg.Seed + cfg.MoistureSeedOffset,  cfg);

        var grid = new Biome[cfg.Width, cfg.Height];
        for (var y = 0; y < cfg.Height; y++)
            for (var x = 0; x < cfg.Width; x++)
                grid[x, y] = BiomeClassifier.Classify(elevation[x, y], moisture[x, y], cfg);

        // Rivers follow the SHAPED elevation down to the sea, carved after
        // classification so they know where Water is and stop at its shore.
        var rivers = RiverCarver.Carve(elevation, grid, cfg);

        var start = StartPicker.Pick(grid, cfg.StartSearchRadius)
            ?? throw new InvalidOperationException(
                $"No valid start found in {cfg.Width}x{cfg.Height} map with seed {cfg.Seed}. " +
                "Try a different seed or relax thresholds.");

        return new GeneratedMap(grid, rivers, start, cfg.Width, cfg.Height, cfg.Seed);
    }

    // Convenience: pack a GeneratedMap into the dict form GenesisSpec.Biomes
    // already accepts. Replaces every tile (no implicit defaulting to Grassland).
    // The sim sees only the frozen Biome values — it cannot tell the map came
    // from generation vs hand-authoring.
    public static IReadOnlyDictionary<TileCoord, Biome> ToBiomeOverrides(GeneratedMap map)
    {
        var dict = new Dictionary<TileCoord, Biome>(capacity: map.Width * map.Height);
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                dict[new TileCoord(x, y)] = map.Grid[x, y];
        return dict;
    }

    // Same for rivers: the sparse form GenesisSpec.Rivers accepts. Only river
    // tiles get an entry.
    public static IReadOnlyDictionary<TileCoord, RiverEdge> ToRiverOverrides(GeneratedMap map)
    {
        var dict = new Dictionary<TileCoord, RiverEdge>();
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                if (map.Rivers[x, y] != RiverEdge.None)
                    dict[new TileCoord(x, y)] = map.Rivers[x, y];
        return dict;
    }
}
