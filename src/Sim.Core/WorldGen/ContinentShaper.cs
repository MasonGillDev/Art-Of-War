namespace Sim.Core.WorldGen;

// Shapes the raw Perlin elevation field into an ISLAND CONTINENT: land in the
// middle, guaranteed ocean at every map edge, coastline warped into bays and
// peninsulas. Generator-only float math, off the replay path (same contract
// as NoiseField).
//
// Why shape ELEVATION instead of post-carving biomes: the elevation field is
// the single source of truth — BiomeClassifier derives Water from it AND the
// client rebuilds its heightmap/waterline from the same quantized field
// (ViewProjector). Shaping the field keeps sim water, rendered sea, and boat
// pathing in agreement by construction; a biome post-pass would desync them.
//
// The mask has three parts (all knobs on GenerationConfig):
//   1. Radial distance d via the "square bump" 1-(1-nx²)(1-ny²): 0 at center,
//      exactly 1 along the whole border. Unlike Euclidean distance it lets
//      land reach toward the corners of a rectangular map.
//   2. Domain warp: d += CoastWarpAmplitude * (warpNoise - 0.5). A third
//      independent noise field pushes the whole falloff band in/out at
//      continent scale — the anti-circle. Fractal shoreline detail on top of
//      that comes free from the base elevation noise crossing the waterline.
//   3. A smoothstep edge ramp over EdgeOceanTiles forcing elevation to 0 at
//      the outermost ring — the HARD guarantee that the border is ocean, so
//      the surrounding sea is one connected component touching every edge
//      (pinned by WorldGenTests).
//   4. A ContinentDome added toward the (warped) interior: coastal lowlands,
//      interior highlands. Keeps Mountain tiles existing on seeds where the
//      falloff would otherwise erase them all — StartPicker requires one.
public static class ContinentShaper
{
    // The shaped continuous elevation field in [0,1]. Both MapGenerator (biome
    // classification) and WorldFactory (client heightmap) MUST source elevation
    // from here — two call sites, one field, or the client renders land where
    // the sim says sea.
    public static double[,] BuildElevation(GenerationConfig cfg)
    {
        var elevation = NoiseField.Generate(cfg.Seed + cfg.ElevationSeedOffset, cfg);
        if (!cfg.OceanBorder) return elevation;

        var warp = NoiseField.Generate(
            cfg.Seed + cfg.CoastSeedOffset,
            cfg with { Frequency = cfg.CoastWarpFrequency });
        // Octave-summed Perlin mapped by (raw+1)/2 is strongly mid-heavy —
        // most values sit in ~[0.4, 0.6], which would mute the warp to a
        // fraction of its amplitude (the preview harness measured coastlines
        // that stayed near-circular at any amplitude). Min-max stretch the
        // field to a true [0, 1] so CoastWarpAmplitude means what it says.
        Normalize(warp, cfg.Width, cfg.Height);

        for (var y = 0; y < cfg.Height; y++)
        {
            for (var x = 0; x < cfg.Width; x++)
            {
                var nx = 2.0 * x / (cfg.Width - 1) - 1.0;   // [-1, 1]
                var ny = 2.0 * y / (cfg.Height - 1) - 1.0;
                var d = 1.0 - (1.0 - nx * nx) * (1.0 - ny * ny);

                d += cfg.CoastWarpAmplitude * (warp[x, y] - 0.5);

                var mask = 1.0 - Smooth01((d - cfg.CoastInner) / (cfg.CoastOuter - cfg.CoastInner));

                var edge = Math.Min(Math.Min(x, y), Math.Min(cfg.Width - 1 - x, cfg.Height - 1 - y));
                var ramp = Smooth01(edge / (double)cfg.EdgeOceanTiles);

                // Interior dome (uses the WARPED distance so highlands follow
                // the continent's actual shape, not the map's geometry).
                var dome = cfg.ContinentDome * (1.0 - Smooth01(d / cfg.CoastInner));

                elevation[x, y] = Math.Min(1.0, elevation[x, y] + dome) * mask * ramp;
            }
        }
        return elevation;
    }

    // Clamped smoothstep: 0 for t<=0, 1 for t>=1, 3t²-2t³ between.
    private static double Smooth01(double t)
    {
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        return t * t * (3.0 - 2.0 * t);
    }

    // In-place min-max stretch to [0, 1]. A constant field stays constant.
    private static void Normalize(double[,] field, int width, int height)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (field[x, y] < min) min = field[x, y];
                if (field[x, y] > max) max = field[x, y];
            }
        var range = max - min;
        if (range <= 0.0) return;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                field[x, y] = (field[x, y] - min) / range;
    }
}
