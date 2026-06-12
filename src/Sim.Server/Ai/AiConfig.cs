using Sim.Core;

namespace Sim.Server.Ai;

// M17 — the AI player's knobs. The thresholds here ARE the arbitration
// (docs/m17-ai-players-spec.md, "Arbitration is its own work item"): a
// strict priority ladder has no weights, so when behavior reads as dumb
// the dial that misfired is one of these named numbers, found by
// replaying the seed and reading the decision trace at the bad tick.
public sealed record AiConfig
{
    public bool Enabled { get; init; } = true;

    // One brain evaluation per game-hour — same cadence reasoning as the
    // bandit driver: reacts within a fraction of any march, keeps the
    // intent log lean.
    public long ThinkPeriodTicks { get; init; } = Time.Hour;

    // THE "Eat preempts everything" rule: below this projected runway the
    // Eat rung claims the think. 2 game-days ≈ one farm-bootstrap cycle —
    // the same coupling the famine grace period uses.
    public long FoodRunwayFloorTicks { get; init; } = 2 * Time.Day;

    // THE "Grow yields to Eat" rule: breeding (and its 20-food birth cost
    // hauled out of the castle) only happens above this castle stock.
    public int GrowthFoodFloor { get; init; } = 350;

    // Haul an extractor's buffer home once it holds at least this much —
    // a full hauler trip's worth by default, so trips aren't wasteful.
    public int HaulBufferThreshold { get; init; } = 15;

    // How far from the castle the brain considers tiles for new sites,
    // and how long a scouting leg is.
    public int SiteSearchRange { get; init; } = 12;
    public int ScoutRange { get; init; } = 12;

    // Demographic gates the brain needs but the view doesn't carry
    // (they're world config, not state). MUST match the world's
    // PopulationConfig; server worlds use the defaults. If the user
    // retunes PopulationConfig these follow — flagged by the
    // config-derived tests.
    public int MinAdultAgeYears { get; init; } = 13;
    public int MinFertileAgeYears { get; init; } = 18;
    public int MaxFertileAgeYears { get; init; } = 45;
    public int BirthFoodCost { get; init; } = 20;

    // Print each decision to the console (--ai-trace 1).
    public bool TracePrint { get; init; } = false;
}
