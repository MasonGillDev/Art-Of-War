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
    int WaterRecoveryRadius = 4,
    // M27 — IRRIGATION: recovery amount for degraded land within
    // WaterRecoveryRadius of water — the M21 "canals visibly rejuvenate
    // faster than rainfall" knob, deferred there and cashed in here so
    // canal-side fields rest in half the time (2 vs 1 per RecoveryPeriod).
    // Set equal to RecoveryAmount to restore exact M21 behavior. Same
    // rate-invariance argument as the latch lift: water proximity only
    // changes at canal completion, which runs the OnWaterProximityChanged
    // catch-up. See docs/canals.md update.
    int WaterRecoveryAmount = 4,
    // M35 - ENVIRONMENTAL BASELINE (docs/environmental-fertility.md). A
    // ladder tile's baseline is BandBaseline + EnvOffset(tile), the offset a
    // pure box-bounded read of distance-to-water and forest depth, clamped
    // so a tile at deviation 0 never leaves its worldgen band. ALL FIVE
    // DEFAULT TO STRENGTH 0: bonus/penalty knobs at 0 make the offset 0
    // everywhere and skip the scans, reproducing M9/M21/M27 byte-for-byte.
    // The gradient is dialled in from the Phase F lab sweep, never here.
    //
    // Water: a tile at Chebyshev distance d <= WaterFertilityRadius from
    // water (lake/sea/canal/river bank) gets
    //   WaterFertilityBonus * (Radius + 1 - d) / (Radius + 1)
    // (full bonus on the bank, tapering to 1/(R+1) at the radius). Beyond
    // the radius the tile is "dry" and takes -DryEdgePenalty instead.
    int WaterFertilityRadius = 3,
    int WaterFertilityBonus = 0,
    int DryEdgePenalty = 0,
    // Forest depth (Forest worldgen tiles only): ForestDepth.At(tile,
    // ForestDepthRings) * ForestDepthBonusPerRing. The forest heart has
    // more fertility to burn than its edge.
    int ForestDepthRings = 3,
    int ForestDepthBonusPerRing = 0)
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
        WaterRecoveryRadius: 4,
        // M27 — irrigated recovery at DOUBLE the rainfall rate: canal-side
        // fields rest in half the time, which is what makes digging one a
        // real farm investment rather than latch insurance.
        WaterRecoveryAmount: 4,
        // M35 — environmental baseline knobs. Radii are the scan bounds;
        // strengths are 0 here ON PURPOSE: this constructor is the identity
        // every hand-built test world and the M9/M21/M27 pins rely on. The
        // played gradient is chosen at launch (`--fertility mild|strong`,
        // ServerOptions.FertilityGradient → WorldFactory.FertilityFor), and
        // the 2026-09-20 sweep found "mild" as a GLOBAL default puts the
        // riverside lab faction into famine by day 160 — the live-fertility
        // taper plus the dry-edge penalty is a real food-curve change, still
        // being balanced. See docs/environmental-fertility.md.
        WaterFertilityRadius:     3,
        WaterFertilityBonus:      0,
        DryEdgePenalty:           0,
        ForestDepthRings:         3,
        ForestDepthBonusPerRing:  0)
    { }
}
