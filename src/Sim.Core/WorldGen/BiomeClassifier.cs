namespace Sim.Core.WorldGen;

// Maps (elevation, moisture) → Biome per the Whittaker-style table in
// GenerationConfig. Pure function; floats in, integer Biome out.
//
// Order matters: water test first (gates the low-elevation band),
// then mountain (highest), then hills (mid-high), then moisture-split
// for the low–mid band.
public static class BiomeClassifier
{
    public static Biome Classify(double elevation, double moisture, GenerationConfig cfg)
    {
        if (elevation < cfg.WaterMax)    return Biome.Water;
        if (elevation > cfg.MountainMin) return Biome.Mountain;
        if (elevation > cfg.HillsMin)    return Biome.Hills;
        return moisture > cfg.MoistureSplit ? Biome.Forest : Biome.Grassland;
    }
}
