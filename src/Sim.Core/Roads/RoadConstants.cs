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

    // Maximum reduction from a maxed-out road. With MAX_REDUCTION = 8:
    //   Grassland (cost 10) at max → cost 2
    //   Forest    (cost 30) at max → cost 22
    //   Mountain  (cost 45) at max → cost 37
    public const int MAX_REDUCTION = 8;

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
    // ticks). With (1, 100): an abandoned maxed road takes 100,000 ticks to
    // vanish — durable infrastructure, asynchronous-game friendly. NOT
    // condition-dependent: that's the coupled-interval trap (the same one
    // production tick math avoided in Phase D of M1).
    public const int DECAY_PER_PERIOD = 1;
    public const int DECAY_PERIOD = 100;
}
