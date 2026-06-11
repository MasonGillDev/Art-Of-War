namespace Sim.Core.Population;

// M8 — world-level population configuration. Set at genesis, immutable
// for the world's lifetime, serialized in the snapshot. Parallel to
// DiplomacyConfig and CombatConfig.
//
// TicksPerYear         — the conversion from sim ticks to "years" (the
//                        unit used for age gates and lifespan). THE
//                        DEMOGRAPHIC CLOCK: deliberately compressed off
//                        the literal calendar (a citizen ages one "year"
//                        per 4 game-days) so demography lands in the
//                        same days-to-a-season band as land degradation,
//                        docks, and war — instead of 30–500× past it.
//                        At the literal Time.Year a child took 13 game-
//                        years (~94 wall-hours at 20 tps) to become
//                        trainable; populations were effectively static
//                        on every other system's timescale, while
//                        starvation killed hourly — a one-way death
//                        spiral. Every age gate below is denominated in
//                        age-years, so this ONE knob rescales all of
//                        demography while preserving its internal
//                        proportions. Tests scale it down further to
//                        keep scenarios short — see PopulationDeathTests.
// MinTrainAge          — minimum age (years) to be assigned a worker
//                        role at an extractor or as a builder at a site.
// MinFertileAge        — lower bound (inclusive) of the breeding window.
// MaxFertileAge        — upper bound (inclusive) of the breeding window.
//                        Checked once, at breeding start; aging past
//                        this mid-gestation does not stop breeding.
// GestationTicks       — ticks between BeginBreedingIntent and BirthEvent.
//                        The only throttle on breeding tempo. Keep it at
//                        (9/12) × TicksPerYear — "nine months" on the
//                        demographic clock — so it scales with the ages.
// BirthFoodCost        — food drawn from the House at breeding start.
// LifespanMinYears     — uniform-distribution minimum for the seeded
// LifespanMaxYears     — lifespan roll. Variable so cohorts don't die
//                        in synchronized cliffs.
public readonly record struct PopulationConfig(
    long TicksPerYear,
    int MinTrainAge,
    int MinFertileAge,
    int MaxFertileAge,
    long GestationTicks,
    int BirthFoodCost,
    int LifespanMinYears,
    int LifespanMaxYears)
{
    // Resulting demographic timeline (4 game-days per age-year):
    //   gestation 3 days · trainable at 52 days · fertile 72–180 days ·
    //   lifespan 200–320 days (avg ~260 ≈ one forest regrowth). A
    //   generation ≈ a season; tps stays a pure pace dial.
    public PopulationConfig() : this(
        TicksPerYear: 4 * Time.Day,
        MinTrainAge: 13,
        MinFertileAge: 18,
        MaxFertileAge: 45,
        GestationTicks: 3 * Time.Day,   // (9/12) × TicksPerYear
        BirthFoodCost: 20,
        LifespanMinYears: 50,
        LifespanMaxYears: 80)
    { }
}
