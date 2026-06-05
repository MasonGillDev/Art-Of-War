namespace Sim.Core.Biomes;

// M9 — world-level biome-degradation configuration. Set at genesis, immutable
// for the world's lifetime, serialized in the snapshot. Parallel to
// PopulationConfig / DiplomacyConfig / CombatConfig.
//
// THE FERTILITY LADDER (F/G/D only):
//   fertility >= ForestThreshold       → Forest
//   DesertThreshold <= fert < Forest…  → Grassland
//   fertility < DesertThreshold        → Desert (LATCHED — recovery off, permanent)
//
// Hills / Mountain / Water tiles are off the ladder: their biome is always
// their worldgen value regardless of stored fertility. They participate in
// neither degrade nor recovery in M9.
//
// Baselines per worldgen biome: the value a tile's fertility sits at when
// deviation = 0 (i.e. untouched). For ladder biomes, the baseline determines
// which band the biome defaults to; for off-ladder biomes, the baseline is
// stored for uniformity but doesn't affect biome.
//
// Recovery is fixed, slow, and applied only when no in-range extractor is
// producing AND deviation < 0 AND the implicit latch is NOT held (current
// fertility >= DesertThreshold). See docs/biome-degradation.md.
//
// Degrade rate AMOUNT per extractor type lives on StructureSpec
// (StructureSpec.DegradeAmount). Zero means "this extractor type does not
// degrade in M9" (Quarry, Mine — out of scope; the F/G/D ladder doesn't admit
// Mountain/Hills). The shared DegradePeriod here keeps "MAX over overlapping
// extractors" a simple integer max — same precedent as RoadConstants where
// DECAY_PERIOD is global and decay strength is the per-unit amount.
public readonly record struct BiomeDegradationConfig(
    int ForestBaseline,
    int GrasslandBaseline,
    int DesertBaseline,
    int HillsBaseline,
    int MountainBaseline,
    int WaterBaseline,
    int ForestThreshold,
    int DesertThreshold,
    int RecoveryAmount,
    long RecoveryPeriod,
    long DegradePeriod,
    int DegradeRadius)
{
    public BiomeDegradationConfig() : this(
        // F/G/D baselines drive band membership at deviation=0.
        // Forest 100 / Grassland 50 / Desert 10 puts the bands well clear of
        // the thresholds (Forest 75 / Desert 25) so a fresh-from-worldgen tile
        // sits squarely inside its band.
        ForestBaseline:    100,
        GrasslandBaseline:  50,
        DesertBaseline:     10,
        // H/M/W baselines stored for API uniformity; ignored by Band() because
        // those biomes are off-ladder (see BiomeDegradation.IsOnLadder).
        HillsBaseline:      30,
        MountainBaseline:   60,
        WaterBaseline:       0,
        // Forest ≥ 75 → Forest. Grassland 25 .. 74. Desert < 25 (LATCHED).
        // Generated-desert tiles sit at baseline 10 < 25 → implicit latch
        // from t=0.
        ForestThreshold:    75,
        DesertThreshold:    25,
        // Recovery is slower than degrade ("regrows takes longer" — design
        // doc). 1 fertility per 30 ticks; abandoned Grassland (deviation -50)
        // takes 1500 ticks to climb back to Forest baseline.
        RecoveryAmount:      1,
        RecoveryPeriod:     30,
        // Single period for all extractor-driven degrade. MAX-over-overlap
        // becomes a simple integer compare on StructureSpec.DegradeAmount.
        // 10 ticks aligns with ProductionPeriodTicks (LumberCamp=10, Farm=10)
        // so degrade and production tick at the same cadence.
        DegradePeriod:      10,
        // Chebyshev radius around an extractor. Radius 1 = 3×3 area (8
        // neighbours + own tile). Tuneable once play surfaces the right
        // pressure curve.
        DegradeRadius:       1)
    { }
}
