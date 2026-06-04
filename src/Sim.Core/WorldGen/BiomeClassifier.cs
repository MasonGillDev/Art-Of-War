namespace Sim.Core.WorldGen;

// Maps (elevation, moisture) → Biome per the Whittaker-style table in
// GenerationConfig. Pure function; floats in, integer Biome out.
//
// Order matters: water test first (gates the low-elevation band),
// then mountain (highest), then hills (mid-high). For the remaining
// low-elevation band, very dry tiles become Desert; otherwise we fall
// through to the moisture split between Forest and Grassland. Desert
// only appears in low-elevation dry zones — high ground stays Hills /
// Mountain regardless of moisture.
public static class BiomeClassifier
{
    public static Biome Classify(double elevation, double moisture, GenerationConfig cfg)
    {
        if (elevation < cfg.WaterMax)         return Biome.Water;
        if (elevation > cfg.MountainMin)      return Biome.Mountain;
        if (elevation > cfg.HillsMin)         return Biome.Hills;
        if (moisture < cfg.DesertMoistureMax) return Biome.Desert;
        return moisture > cfg.MoistureSplit ? Biome.Forest : Biome.Grassland;
    }
}
