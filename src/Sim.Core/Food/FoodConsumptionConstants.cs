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
    // ticks apart. 3 game-days grace ≈ one farm-bootstrap cycle (site +
    // hauls + 10h build + first food haul home), so a famine gives the
    // player roughly one full "fix it" window before the first body.
    // The grace isn't free: famine DEBT (Castle.FoodDebt) accrues at the
    // full population rate the whole time and must be repaid in full to
    // stop the deaths — see docs/food-consumption.md (Update 2026-06-11).
    public const int StarvationStartDelay = 3 * Time.Day;
    public const int StarvationDeathInterval = 12 * Time.Hour;

    // M19 — home auto-assignment reach (docs/m19-per-house-food-spec.md):
    // how far (Chebyshev) the three assignment triggers look for a house
    // with a free bed — around the workplace (home-follows-work), around
    // the new house (completion move-in), and around the birth house
    // (newborn overflow). Beyond it, the castle is home. A house serves
    // the work cluster around it, not the whole map.
    public const int HomeAssignRadius = 8;
}
