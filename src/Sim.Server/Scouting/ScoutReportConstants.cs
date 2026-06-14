namespace Sim.Server.Scouting;

// M20 Phase 2 — the claims compiler's balance knobs. Presentation-only: none
// of this is sim state, none is hashed. Tests derive their expectations from
// these values (the standing convention), so a retune is a one-file change.
public sealed class ScoutReportConstants
{
    // Ride pace: ticks to cross one tile on open ground (grassland march
    // pace = Biomes.MoveCost(Grassland) = 30; docs/movement-cost.md, "1 tile
    // ≈ 1 km"). The lexical pass turns a crow-flies tile distance into a
    // traveler's ride-time with this — the conversion that stops the LLM
    // doing its own (wrong) tile→time arithmetic.
    public int RideTicksPerTile { get; init; } = 30;

    // Count banding. A force seen at vision quality >= this percent is
    // counted EXACTLY; below it the count becomes a band whose half-width
    // grows as the sighting worsens. The TRUE count is never carried into a
    // claim once banded — that is how "true values never reach the LLM" is
    // enforced in one place.
    public int ExactCountQualityPct { get; init; } = 67; // inner third → exact

    // Band half-width = max(1, TrueCount * (100 - quality) / this). Larger =
    // tighter bands. 200 ⇒ at 50% quality a half-width of a quarter of the count.
    public int BandWidthDivisor { get; init; } = 200;

    // Foreign units within this Chebyshev distance of one another read as one
    // body of men (one Force claim), not a scatter of individuals.
    public int ForceClusterRadius { get; init; } = 3;

    // A heading counts as diagonal (north-east, ...) rather than cardinal
    // when min(|dx|,|dy|) * Denominator >= max(|dx|,|dy|) * Numerator
    // (≈ tan 22.5° ≈ 0.414). Pure-integer 8-wind compass.
    public int DiagonalNumerator { get; init; } = 5;
    public int DiagonalDenominator { get; init; } = 12;
}
