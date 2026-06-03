namespace Sim.Core.WorldGen;

// Octave-summed 2D Perlin field, normalized to [0, 1]. Floats are fine
// here — generator-only code, off the replay path.
//
// Uses SharpNoise.Modules.Perlin, which sums octaves internally according
// to (OctaveCount, Persistence, Lacunarity, Frequency).
public static class NoiseField
{
    // Generates a [Width × Height] grid in [0, 1] using the given seed.
    // Two independent fields (elevation, moisture) come from calling this
    // with different seeds (Seed + ElevationSeedOffset vs MoistureSeedOffset).
    public static double[,] Generate(int seed, GenerationConfig cfg)
    {
        var perlin = new SharpNoise.Modules.Perlin
        {
            Seed = seed,
            OctaveCount = cfg.OctaveCount,
            Persistence = cfg.Persistence,
            Lacunarity = cfg.Lacunarity,
            Frequency = cfg.Frequency,
        };

        var grid = new double[cfg.Width, cfg.Height];
        for (var y = 0; y < cfg.Height; y++)
        {
            for (var x = 0; x < cfg.Width; x++)
            {
                // SharpNoise Perlin returns roughly in [-1, 1]. We clamp and
                // remap to [0, 1] so downstream thresholds are unambiguous.
                var raw = perlin.GetValue(x, y, 0);
                var v = (raw + 1.0) * 0.5;
                if (v < 0.0) v = 0.0;
                if (v > 1.0) v = 1.0;
                grid[x, y] = v;
            }
        }
        return grid;
    }
}
