namespace Sim.Core.Food;

// M13 — tuning knobs for the castle food sink.
//
// `FoodConsumptionPeriod` is the integer number of ticks per "meal":
// consumption happens in one-shot chunks at period boundaries, not
// smoothly. Chosen to be coarse enough that catch-up math is integer-
// exact (no remainder leakage) and fine enough that views see food
// updates regularly. Default ≈ 1 sim-hour (60 ticks if 1 tick ≈ 1 min);
// matches the cadence at which views naturally refresh.
//
// `FoodPerCitizenPerPeriod = 1` keeps the rate math trivial: one
// citizen eats one unit of food per period. Rate scales linearly with
// population.
//
// `StarvationStartDelay` and `StarvationDeathInterval` are Phase D
// (StarvationDeathEvent), defined here so the whole food-consumption
// timing surface lives in one file.
public static class FoodConsumptionConstants
{
    public const int FoodConsumptionPeriod = 6 * Time.Hour;
    public const int FoodPerCitizenPerPeriod = 1;

    // Phase D — first death occurs StarvationStartDelay ticks after the
    // famine started. Subsequent deaths follow at StarvationDeathInterval
    // ticks apart. 1 game-day grace, then 1 game-hour per death — a
    // reasonable first-pass tuning; revisit with playtesting.
    public const int StarvationStartDelay = Time.Day;
    public const int StarvationDeathInterval = Time.Hour;
}
