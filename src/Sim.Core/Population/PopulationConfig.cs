namespace Sim.Core.Population;

// M8 — world-level population configuration. Set at genesis, immutable
// for the world's lifetime, serialized in the snapshot. Parallel to
// DiplomacyConfig and CombatConfig.
//
// TicksPerYear         — the conversion from sim ticks to "years" (the
//                        unit used for age gates and lifespan). Default
//                        is Time.Year (1 tick = 1 game-minute, 360-day
//                        calendar). Tests scale this down to keep
//                        scenarios short — see PopulationDeathTests etc.
// MinTrainAge          — minimum age (years) to be assigned a worker
//                        role at an extractor or as a builder at a site.
// MinFertileAge        — lower bound (inclusive) of the breeding window.
// MaxFertileAge        — upper bound (inclusive) of the breeding window.
//                        Checked once, at breeding start; aging past
//                        this mid-gestation does not stop breeding.
// GestationTicks       — ticks between BeginBreedingIntent and BirthEvent.
//                        The only throttle on breeding tempo.
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
    public PopulationConfig() : this(
        TicksPerYear: Time.Year,
        MinTrainAge: 15,
        MinFertileAge: 18,
        MaxFertileAge: 45,
        GestationTicks: 9 * Time.Month,
        BirthFoodCost: 40,
        LifespanMinYears: 50,
        LifespanMaxYears: 80)
    { }
}
