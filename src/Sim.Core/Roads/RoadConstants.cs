namespace Sim.Core.Roads;

// Tuning constants for the road system. Numbers are placeholders — tune
// during balance, lock the *shape* now (diminishing-returns gain ×
// constant-rate decay × smooth-linear cost reduction).
public static class RoadConstants
{
    // Condition is an integer in [0, CONDITION_MAX]. 0 = no road, MAX = stone.
    // Room left for diminishing-returns math without integer precision loss.
    public const int CONDITION_MAX = 1000;

    // Movement cost on a road tile is biome cost minus reduction; floored
    // here so movement is never free and remains a positive integer.
    public const int MIN_COST = 1;

    // Maximum reduction from a maxed-out road, as a PERCENTAGE of the
    // tile's biome cost. Proportional (not flat-absolute) so the road is
    // comparably useful across all biomes — a flat reduction lopsidedly
    // helps cheap terrain (10 - 8 = 5x speedup on grassland) and barely
    // touches expensive terrain (45 - 8 = 1.22x on mountain).
    //
    // With MAX_REDUCTION_PERCENT = 66, every biome gets ~3x speedup at
    // max condition (still floored by MIN_COST, still differentiated by
    // absolute biome cost):
    //   Grassland (cost  30) at max → cost 11
    //   Hills     (cost  75) at max → cost 26
    //   Forest    (cost  90) at max → cost 31
    //   Desert    (cost 120) at max → cost 41
    //   Mountain  (cost 135) at max → cost 46
    //
    // Roads only apply to Foot traversal — Water uses BoatMovementCost,
    // so this percentage never touches water tile costs.
    public const int MAX_REDUCTION_PERCENT = 66;

    // Per-traversal gain when condition is 0. Drops linearly toward GAIN_FLOOR
    // as condition approaches CONDITION_MAX (diminishing returns). With
    // BASE_GAIN = 50 and CONDITION_MAX = 1000, ~20 traversals to first
    // meaningful road; sustained traffic builds smoothly.
    public const int BASE_GAIN = 50;

    // Minimum gain per traversal. Ensures the same-tick contention test stays
    // meaningful at cap, and avoids the integer-math degenerate case where
    // BASE_GAIN * (MAX - condition) / MAX rounds to zero near the top.
    public const int GAIN_FLOOR = 1;

    // Constant-rate decay (DECAY_PER_PERIOD condition units per DECAY_PERIOD
    // game-minutes). With (1, 100): an abandoned maxed road takes
    // 100,000 minutes (~70 game-days) to vanish — durable infrastructure,
    // asynchronous-game friendly. NOT condition-dependent: that's the
    // coupled-interval trap (the same one production tick math avoided in
    // Phase D of M1).
    public const int DECAY_PER_PERIOD = 1;
    public const int DECAY_PERIOD = 2 * Time.Hour;
}
