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
    int DegradeRadius,
    // M21 — Chebyshev radius within which a Water tile (worldgen lake/sea OR a
    // player-built canal) lifts the otherwise-permanent desert latch on
    // degraded ladder land, letting it recover toward its original biome.
    // Defaulted so existing positional/named construction stays source-
    // compatible. See WaterProximity + docs/canals.md.
    int WaterRecoveryRadius = 2)
{
    // SCALE NOTE: the fertility space is ×100 the original M9 scale
    // (10000/5000/1000 instead of 100/50/10). The point space is fine-
    // grained ON PURPOSE: catch-up drops the partial-period carry at every
    // production transition (the M9 anchor discipline), so the degrade
    // period must stay much shorter than an extractor's arm/dormant duty
    // cycle (a few hundred ticks) or duty-cycling would shed all
    // degradation and reopen "extract forever." Long land lifetimes
    // therefore come from a BIG point budget at a SHORT period — never
    // from a long period. Tests pin the math on an explicit small-scale
    // config; only these defaults carry the gameplay pacing.
    public BiomeDegradationConfig() : this(
        // F/G/D baselines drive band membership at deviation=0, placed well
        // clear of the thresholds (7500 / 2500) so a fresh-from-worldgen
        // tile sits squarely inside its band.
        ForestBaseline:    10000,
        GrasslandBaseline:  5000,
        DesertBaseline:     1000,
        // H/M/W baselines stored for API uniformity; ignored by Band() because
        // those biomes are off-ladder (see BiomeDegradation.IsOnLadder).
        HillsBaseline:      3000,
        MountainBaseline:   6000,
        WaterBaseline:         0,
        // Forest ≥ 7500. Grassland 2500..7499. Desert < 2500 (LATCHED).
        // Generated-desert tiles sit at baseline 1000 < 2500 → implicit
        // latch from t=0.
        ForestThreshold:    7500,
        DesertThreshold:    2500,
        // Recovery is slower than degrade ("regrowing takes longer" — design
        // doc): half the degrade tempo. A logged-out tile snapped to
        // GrasslandBaseline 5000 climbs the 2500 points back to Forest in
        // 5000 game-hours ≈ 208 days (~7 game-months) of rest. Land-use
        // decisions play out on the calendar, not the hour hand.
        RecoveryAmount:      1,
        RecoveryPeriod:      2 * Time.Hour,
        // Single period for all extractor-driven degrade. MAX-over-overlap
        // becomes a simple integer compare on StructureSpec.DegradeAmount.
        // 1 point per game-hour puts land exhaustion on a real-life-ish
        // scale: a Farm (amount 1) crosses Grassland→Desert after ~2500
        // hours ≈ 104 days (~3.5 game-months) of CONTINUOUS production; a
        // LumberCamp (amount 2) crosses Forest→Grassland after ~1250 hours
        // ≈ 52 days (~7 weeks). Degradation only accrues while producing,
        // so calendar time is longer in practice.
        DegradePeriod:       1 * Time.Hour,
        // Chebyshev radius around an extractor. Radius 1 = 3×3 area (8
        // neighbours + own tile). Tuneable once play surfaces the right
        // pressure curve.
        DegradeRadius:       2,
        // M21 — same Chebyshev scale as the degrade footprint: land within 2
        // tiles of water (lake/sea/canal) escapes the permanent latch and
        // recovers. Lakeside/canal-side fields are renewable; inland land
        // still has a hard desert floor.
        WaterRecoveryRadius: 2)
    { }
}
