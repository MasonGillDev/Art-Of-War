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
    // Eat rung claims the think. 3 game-days ≈ one farm-bootstrap cycle
    // PLUS the march/staffing lag — at 2 days the panic farm comes online
    // after the cascade starts (the lab watched faction 1 lose that race).
    public long FoodRunwayFloorTicks { get; init; } = 3 * Time.Day;

    // Farm capacity headroom, percent. Demand × this ÷ 100 is the supply
    // the planner builds toward. 150 starts the next farm well BEFORE the
    // demographic surge crosses the curve — at 125 both lab factions hit
    // the crunch with food under 60 and one fell into the cascade.
    public int FarmHeadroomPercent { get; init; } = 150;

    // THE "Grow yields to Eat" rule: breeding (and its 20-food birth cost
    // hauled out of the castle) only happens above this castle stock.
    // ~4.5 days of pop-14 consumption — low enough that the first house
    // goes up while the founders are still fertile (the balance lab's
    // fertility-cliff finding), high enough to never gamble the larder.
    public int GrowthFoodFloor { get; init; } = 250;

    // Haul an extractor's buffer home once it holds at least this much —
    // a full hauler trip's worth by default, so trips aren't wasteful.
    public int HaulBufferThreshold { get; init; } = 15;

    // Breed only while the adult labor pool covers farm demand with this
    // much slack, in PERCENT (pool ≥ demanded × this ÷ 100). The brake on
    // demographic surges. Proportional, not a fixed count: at the fast
    // demographic clock a flat 3 let a surge famine the colony and a flat
    // 5 stopped breeding until the founders died of old age with a full
    // granary — the slack must scale with the society it protects.
    public int GrowthLaborSlackPercent { get; init; } = 135;

    // One house per this many fertile adults — houses are pregnancy
    // SLOTS, and a single house caps the whole colony at one birth per
    // gestation cycle regardless of every demographic knob (the lab's
    // TicksPerYear experiments were reading the house ceiling, not the
    // clock). Pregnancies overlap across houses; pair setup is serial.
    public int FertileAdultsPerHouse { get; init; } = 6;

    // Stop hauling a non-food resource home once the castle holds this
    // much of it. Without the cap the AI hoarded ~4,600 wood, filled the
    // castle's 5,000 capacity, and FOOD deposits started bouncing — the
    // economy choked on its own warehouse. Side effect of the cap: the
    // camp idles buffer-full, production halts, and its claims stop
    // degrading — demand-driven logging that spares the forest.
    public int ResourceStockTarget { get; init; } = 300;

    // How far from the castle the brain considers tiles for new sites,
    // and how long a scouting leg is. The range is the colony's land
    // bank: dead extractors lock their claims (no DemolishIntent yet),
    // so a long-lived, fast-rotating economy eventually eats outward.
    public int SiteSearchRange { get; init; } = 90;
    public int ScoutRange { get; init; } = 12;

    // Exploration budget: total scout legs before the Scout rung retires
    // and scout-role units join the general labor pool. Two scouts × one
    // full compass sweep each — enough to find farmland and neighbors;
    // perpetual scouting cost two adults forever and broke the founder-
    // die-off bridge in the lab. The LAND BANK re-opens scouting on
    // demand (below).
    public int ScoutLegBudget { get; init; } = 16;

    // Keep at least this many known farm POCKETS (tiles the brain's own
    // map says can host a farm's full claim) in inventory; below it,
    // scouts go back out BEFORE anything is wrong. Counting raw tiles
    // was the rugged-map killer: 40 scattered slope-grass tiles is zero
    // farms.
    public int LandBankFloorPockets { get; init; } = 12;

    // Farm mortality accounting. A farm's claims exhaust after a KNOWN
    // working lifetime (Grassland fertility headroom 2500 ÷ 1/hour burn ≈
    // 104 days — must match BiomeDegradationConfig, same contract as the
    // demographic mirrors below). Farms within ReplaceAhead of that age
    // stop counting as supply, so replacements go up BEFORE the cohort
    // cliff — the lab watched whole panic-built generations of farms die
    // the same week, twice, at pop 65+. Dormant spells pause the burn, so
    // age-based death is conservative; observation (zero-buffer) remains
    // the ground truth.
    public long FarmLifetimeTicks { get; init; } = 2500 * Time.Hour;
    public long FarmReplaceAheadTicks { get; init; } = 8 * Time.Day;

    // Demographic gates the brain needs but the view doesn't carry
    // (they're world config, not state). MUST match the world's
    // PopulationConfig; server worlds use the defaults. If the user
    // retunes PopulationConfig these follow — flagged by the
    // config-derived tests.
    public int MinAdultAgeYears { get; init; } = 13;
    public int MinFertileAgeYears { get; init; } = 18;
    public int MaxFertileAgeYears { get; init; } = 45;
    public int BirthFoodCost { get; init; } = 20;

    // M17 Phase 2 (docs/m17-defender-spec.md) — the STANDING ARMY.
    // Quota = min(floor + ownStructures / perStructures,
    //             population / populationPerSoldier).
    // The floor is sized so one bare squad beats the biggest default
    // bandit party on health (4 Soldiers: 12pw/120hp vs 4 Bandits:
    // 12pw/100hp); the structures term mirrors the SHAPE of bandit
    // pressure (one party per N structures) without reading
    // BanditConfig — the AI learns the wolf the way a player does.
    // The POPULATION CAP is the lab's lesson re-learned (the slack
    // must scale with the society it protects): a flat floor of 4
    // against a 14-person genesis was a ~30% defense budget — Sparta
    // starved at pop 17 while the unarmed control grew to 151. One
    // soldier per 8 mouths caps the budget at ~12%, and the threat
    // curve agrees: a young colony is too small to draw a party at
    // all. Soldiers are a separate labor class (no hauling, farming,
    // or breeding) and eat without producing — the quota IS the
    // defense budget, tuned against the famine line.
    public int SoldierQuotaFloor { get; init; } = 4;
    public int SoldiersPerStructures { get; init; } = 8;
    public int PopulationPerSoldier { get; init; } = 8;

    // M17 Phase 2 — the Defend rung (top of the ladder; fires only
    // while the threat memory is hot).
    // How long a bandit sighting stays actionable: bandits move at
    // march pace, so half a day of staleness is already a cold trail.
    public long ThreatMemoryTicks { get; init; } = 12 * Time.Hour;
    // Pursuit leash, Chebyshev from the castle (user-locked: PURSUE —
    // but every open-ended job needs a budget, lesson #9; an unleashed
    // chase is scout-creep with swords).
    public int PursuitLeashTiles { get; init; } = 24;
    // CIVILIAN DOCTRINE under raid — the knob the balance lab A/Bs
    // (user-locked: measure both, let the data pin the default).
    // ON: workers within the danger radius of a live sighting are
    // recalled and staffing/building inside it pauses until the threat
    // cools (Eat re-staffs automatically — recall costs production).
    // OFF: civilians work through the raid and the militia handles it
    // (the famine clock doesn't pause, but neither do bandit blades).
    public bool RecallCiviliansUnderRaid { get; init; } = true;
    public int CivilianDangerRadius { get; init; } = 5;

    // Print each decision to the console (--ai-trace 1).
    public bool TracePrint { get; init; } = false;

    // DecisionTrace ring size. 256 ≈ 10 game-days of hourly thinks —
    // plenty for a live server; the balance lab cranks it to keep a whole
    // match's decisions for post-mortem.
    public int TraceCapacity { get; init; } = 256;
}
