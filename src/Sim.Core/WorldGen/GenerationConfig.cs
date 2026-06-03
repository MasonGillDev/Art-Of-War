namespace Sim.Core.WorldGen;

// Configuration for procedural map generation. Inputs are floats and may
// freely use floating-point noise — the generator runs ONCE and freezes a
// concrete integer Biome[,] grid before the sim ever sees the world.
// Replay never re-executes generation (see this folder's README rule).
//
// Defaults pin the *shape*: 64x64 continent, 4 octaves of Perlin, the
// Whittaker-style thresholds from the M3 sub-task spec. Retune visually.
public sealed record GenerationConfig
{
    // Provenance seed. Same seed + same config = same map (D1 reproducibility).
    public int Seed { get; init; } = 42;

    public int Width { get; init; } = 64;
    public int Height { get; init; } = 64;

    // Noise shaping (passed straight to SharpNoise.Modules.Perlin).
    public int OctaveCount { get; init; } = 4;
    public double Persistence { get; init; } = 0.5;
    public double Lacunarity { get; init; } = 2.0;
    // Smaller frequency = larger features. 0.04 over a 64-tile map gives
    // a few coherent regions per axis rather than per-tile speckle.
    public double Frequency { get; init; } = 0.04;

    // Independent fields share the base Seed plus per-field offsets.
    public int ElevationSeedOffset { get; init; } = 0;
    public int MoistureSeedOffset { get; init; } = 1000;

    // Whittaker thresholds, on normalized noise output in [0, 1].
    //   elevation < WaterMax        → Water
    //   elevation > MountainMin     → Mountain
    //   elevation > HillsMin        → Hills
    //   else                        → Forest (moisture > MoistureSplit) or Grassland
    public double WaterMax { get; init; } = 0.30;
    public double HillsMin { get; init; } = 0.65;
    public double MountainMin { get; init; } = 0.85;
    public double MoistureSplit { get; init; } = 0.50;

    // Start picker scans for a Grassland tile within this Chebyshev radius
    // of Forest + Hills + Mountain (so every extractor can eventually be built).
    public int StartSearchRadius { get; init; } = 15;
}
