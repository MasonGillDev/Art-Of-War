namespace Sim.Core.WorldGen;

// The frozen result of one generation run. Grid is a concrete Biome[,] —
// integer-typed, deterministic to the sim. The Seed field is for
// PROVENANCE ONLY (logging, debugging). The sim and replay anchor on
// the grid, never on the seed — re-running the generator is never on
// the replay path.
public sealed record GeneratedMap(Biome[,] Grid, TileCoord Start, int Width, int Height, int Seed);

// Top-level entry. Runs the noise → classify → start-pick pipeline once.
public static class MapGenerator
{
    public static GeneratedMap Build(GenerationConfig cfg)
    {
        var elevation = NoiseField.Generate(cfg.Seed + cfg.ElevationSeedOffset, cfg);
        var moisture  = NoiseField.Generate(cfg.Seed + cfg.MoistureSeedOffset,  cfg);

        var grid = new Biome[cfg.Width, cfg.Height];
        for (var y = 0; y < cfg.Height; y++)
            for (var x = 0; x < cfg.Width; x++)
                grid[x, y] = BiomeClassifier.Classify(elevation[x, y], moisture[x, y], cfg);

        var start = StartPicker.Pick(grid, cfg.StartSearchRadius)
            ?? throw new InvalidOperationException(
                $"No valid start found in {cfg.Width}x{cfg.Height} map with seed {cfg.Seed}. " +
                "Try a different seed or relax thresholds.");

        return new GeneratedMap(grid, start, cfg.Width, cfg.Height, cfg.Seed);
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
}
