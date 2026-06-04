namespace Sim.Core.Population;

// M8 — world-level population configuration. Set at genesis, immutable
// for the world's lifetime, serialized in the snapshot. Parallel to
// DiplomacyConfig and CombatConfig.
//
// TicksPerYear         — the conversion from sim ticks to "years" (the
//                        unit used for age gates and lifespan).
//                        Default 100 keeps tests tractable while making
//                        age non-trivial: a 60-year lifespan = 6000
//                        ticks.
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
        TicksPerYear: 100,
        MinTrainAge: 15,
        MinFertileAge: 18,
        MaxFertileAge: 40,
        GestationTicks: 300,
        BirthFoodCost: 20,
        LifespanMinYears: 50,
        LifespanMaxYears: 80)
    { }
}
