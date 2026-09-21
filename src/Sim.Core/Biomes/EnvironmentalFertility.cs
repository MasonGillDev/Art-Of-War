using Sim.Core.World;

namespace Sim.Core.Biomes;

// M35 - the environmental fertility baseline (docs/environmental-fertility.md).
//
//   Baseline(tile) = BandBaseline(worldgenBiome) + Clamp(RawOffset(tile), band)
//   RawOffset(tile) = WaterBonus(distance-to-water) + DepthBonus(forest-depth)
//
// ALL PURE READS over the WORLDGEN grid (TileGrid.BiomeAt + river mask). The
// grid changes at exactly one sim event - canal completion - which is also
// the one place the lazy fertility field is caught up for a baseline change
// (BuildCompleteEvent.CompleteCanal -> BiomeDegradation.OnWaterProximityChanged).
// Reading the DERIVED biome here would be wrong: a neighbour crossing a band
// would move this tile's baseline with no catch-up anywhere.
//
// THE OFFSET IS CLAMPED PER BAND, not once. The baseline is load-bearing in
// four places beyond "the floor": Band() compares absolute fertility to fixed
// thresholds, the degrade step penalty snaps to the next band's baseline, the
// deviation clamp is [-baseline, 0], and the AI's rotation thresholds read the
// number. So the tile's own baseline is BaselineFor(worldgenBand, offset) and
// every snap target is BaselineFor(nextBand, offset) - each clamped so the
// value sits strictly inside that band. A rich riverside forest degrades to
// a rich riverside grassland, and raw desert near water never greens
// (docs/canals.md keeps that deferred; the clamp is the guard).
//
// STRENGTH 0 IS THE IDENTITY: with every bonus/penalty knob at 0 the offset
// is 0 and no scan runs, so M9/M21/M27 numbers reproduce byte-for-byte.
public static class EnvironmentalFertility
{
    // The unclamped environmental offset for a tile. Off-ladder tiles
    // (Hills/Mountain/Water) return 0: they carry no gradient in M35.
    public static int RawOffset(GameWorld world, TileCoord tile, BiomeDegradationConfig config)
    {
        var worldgen = world.Grid.BiomeAt(tile);
        if (!BiomeDegradation.IsOnLadder(worldgen)) return 0;
        var offset = 0;

        if (config.WaterFertilityBonus != 0 || config.DryEdgePenalty != 0)
        {
            var r = config.WaterFertilityRadius;
            var d = WaterProximity.DistanceToWater(world, tile, r);
            if (d <= r)
                // Integer taper: full bonus on the bank (d = 0), 1/(R+1) of it
                // at the radius, 0 beyond. Long math is overkill at these
                // magnitudes but costs nothing.
                offset += (int)((long)config.WaterFertilityBonus * (r + 1 - d) / (r + 1));
            else
                offset -= config.DryEdgePenalty;
        }

        if (config.ForestDepthBonusPerRing != 0 && worldgen == Biome.Forest)
            offset += ForestDepth.At(world, tile, config.ForestDepthRings) * config.ForestDepthBonusPerRing;

        return offset;
    }

    // The baseline a tile has IN A GIVEN BAND, given its raw offset. Used for
    // the tile's own baseline (band = worldgen biome) and for the step-penalty
    // snap targets (band = the band being entered). Clamped so the result is
    // strictly inside the band:
    //   Forest    >= ForestThreshold            (no ceiling - the top band)
    //   Grassland in [DesertThreshold, ForestThreshold - 1]
    //   Desert    in [0, DesertThreshold - 1]   (stays latched; never negative)
    // Off-ladder bands ignore the offset entirely.
    public static int BaselineFor(Biome band, int rawOffset, BiomeDegradationConfig config)
    {
        var bandBaseline = BiomeDegradation.BaselineFertility(band, config);
        if (rawOffset == 0 || !BiomeDegradation.IsOnLadder(band)) return bandBaseline;
        var (lo, hi) = band switch
        {
            Biome.Forest    => (config.ForestThreshold, int.MaxValue),
            Biome.Grassland => (config.DesertThreshold, config.ForestThreshold - 1),
            Biome.Desert    => (0, config.DesertThreshold - 1),
            _ => throw new InvalidOperationException($"{band} is not a ladder band"),
        };
        var v = (long)bandBaseline + rawOffset;
        if (v < lo) v = lo;
        if (v > hi) v = hi;
        return (int)v;
    }

    // Convenience: the tile's own baseline (its worldgen band + clamped offset).
    public static int Baseline(GameWorld world, TileCoord tile, BiomeDegradationConfig config) =>
        BaselineFor(world.Grid.BiomeAt(tile), RawOffset(world, tile, config), config);
}
