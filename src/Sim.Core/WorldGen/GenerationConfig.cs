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
    //   elevation < WaterMax                 → Water
    //   elevation > MountainMin              → Mountain
    //   elevation > HillsMin                 → Hills
    //   moisture  < DesertMoistureMax        → Desert (low-elevation only)
    //   else                                 → Forest (moisture > MoistureSplit) or Grassland
    public double WaterMax { get; init; } = 0.30;
    public double HillsMin { get; init; } = 0.65;
    // Tried 0.90 (fewer, more landmark-like mountains) and reverted: combined with the
    // dome no longer clipping at 1.0 (ContinentShaper), the highest ground stopped
    // reaching 0.90 on some seeds, StartPicker could not find a Grassland start with
    // Mountain in range, and generation threw. Mountains are a START REQUIREMENT, not
    // just scenery, so this threshold cannot move without re-tuning ContinentDome and
    // sweeping seeds. The cliff-like LOOK was the render curve, not this number, and is
    // fixed client-side in WorldGeometry.
    // The client's WorldGeometry.MountainBand must match this.
    public double MountainMin { get; init; } = 0.85;
    public double MoistureSplit { get; init; } = 0.50;
    // Below this moisture, low-elevation (non-water, non-hills, non-mountain)
    // tiles become Desert. 0.20 carves the driest ~20% of moisture values in
    // the low-elevation band → roughly 5–10% of total map area. "Not a lot of
    // desert" is the design intent; tune this down to push it further.
    public double DesertMoistureMax { get; init; } = 0.20;

    // Start picker scans for a Grassland tile within this Chebyshev radius
    // of Forest + Hills + Mountain (so every extractor can eventually be built).
    public int StartSearchRadius { get; init; } = 15;

    // ---- Ocean border (island continent) — see ContinentShaper ----
    // When on, the elevation field is multiplied by a radial falloff mask so
    // the land forms one continent surrounded by ocean, and every map edge is
    // guaranteed sea. The falloff distance is domain-warped by a third noise
    // field so the coastline grows bays and peninsulas instead of tracing a
    // circle. Off = the legacy unbounded-noise world.
    public bool OceanBorder { get; init; } = true;

    // Seed offset for the coast-warp noise field (sibling of Elevation/Moisture).
    public int CoastSeedOffset { get; init; } = 2000;

    // Frequency of the coast-warp noise. Lower than terrain Frequency →
    // continent-scale lobes (whole bays and peninsulas), not per-tile jitter.
    // Fixed like Frequency: a bigger map gets proportionally more coastline.
    public double CoastWarpFrequency { get; init; } = 0.015;

    // How far (in normalized radial-distance units: 0 = map center, 1 = map
    // border) the warp noise can push the coastline in or out. The main
    // "interesting coastline" dial — 0 degrades to a wobbly-edged disc.
    public double CoastWarpAmplitude { get; init; } = 0.50;

    // Radial falloff band: mask = 1 inside CoastInner (interior noise passes
    // through, plus the ContinentDome below), smoothstepping to 0 at
    // CoastOuter. Outer may exceed 1.0 — the edge ramp below still guarantees
    // border ocean; a larger Outer just moves the average coast further out.
    public double CoastInner { get; init; } = 0.50;
    public double CoastOuter { get; init; } = 1.05;

    // Smooth ramp (in tiles from the nearest map edge) forcing elevation to 0
    // at the very border regardless of noise — the hard guarantee that the
    // outermost ring is ocean, hence one connected sea touching all edges.
    public int EdgeOceanTiles { get; init; } = 8;

    // Gentle elevation dome added toward the continent interior (peaks at the
    // warped center, gone by CoastInner). Continents keep highlands: without
    // it, masking can erase every Mountain tile on an unlucky seed (seed 1151
    // — the server default — did exactly that) and StartPicker finds no start.
    // Also shapes geography the right way round: coastal lowlands, interior
    // ranges. 0 = off.
    public double ContinentDome { get; init; } = 0.15;
}
