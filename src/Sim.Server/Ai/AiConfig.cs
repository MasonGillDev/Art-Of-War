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

    // M25 — the Rival's posture (docs/m25-rival-spec.md). Default Homesteader
    // is the M17 peaceful brain, UNCHANGED: every existing scenario and test
    // that constructs `new AiConfig()` keeps today's behavior, and the balance
    // lab (which builds drivers directly) stays peaceful. The host assigns
    // Opportunist/Warlord per faction via RivalDoctrine.AssignPersonality; tests
    // set this directly to pin a posture.
    public AiPersonality Personality { get; init; } = AiPersonality.Homesteader;

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

    // WAR FOOTING (the siege-trap fix, docs/m17-defender-spec.md):
    // while the threat memory counts hostiles inside the leash, the
    // quota rises to known headcount + 1 — capped by a WARTIME
    // population share (1 per 4 mouths vs peacetime's 1 per 8; a levy
    // is allowed to hurt, not to starve). When the memory goes fully
    // cold, veterans demobilize back to the fields one at a time
    // (Soldier → Farmer at the School), so the levy is a war tax, not
    // a permanent Sparta.
    public int WarPopulationPerSoldier { get; init; } = 4;

    // ROLE FLOORS (the builder-extinction fix): the bandit lab watched
    // a raid kill both Builders and both Haulers — and the colony
    // freeze forever, because only Builders may raise a site (engine
    // rule) and the Train rung only knew how to make Farmers. The
    // Train rung now restores critical organs FIRST: Builders (the
    // hands), Haulers (the 25-capacity logistics backbone), Scouts
    // (the eyes), then Farmer coverage as before. Floors match the
    // genesis roster's shape.
    public int BuilderFloor { get; init; } = 2;
    public int HaulerFloor { get; init; } = 1;
    public int ScoutFloor { get; init; } = 1;

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

    // CROP ROTATION (soil-aware farming): with own-claim fertility on
    // the wire (StructDto.ClaimFertility) the brain can rest a tiring
    // farm BEFORE the permanent desert latch and return it when the
    // soil recovers. The catalog's rates encode a three-field system —
    // recovery runs at HALF the degrade rate, so ~2 fields rest per 1
    // worked. The thresholds are the rotation's hysteresis: a STAFFED
    // farm works until its worst claim tile falls below RestSoilBelow;
    // an UNSTAFFED farm only (re)enters service above ResumeSoilAbove
    // (staffing state is the memory, so the boundary can't oscillate).
    // RestSoilBelow sits ~700 over the 2500 latch — a month of margin
    // at the farm's 1/hour burn. Default pinned by the lab A/B
    // (CropRotation_VsSlashAndBurn_LabReport).
    public bool RotateFarms { get; init; } = true;
    public int RestSoilBelow { get; init; } = 3200;
    public int ResumeSoilAbove { get; init; } = 4500;

    // ===== M25 — THE RIVAL (offensive war; docs/m25-rival-spec.md) =====
    // These gate the war-capable postures (Opportunist/Warlord); a Homesteader
    // never reads them. Like every knob above, the thresholds ARE the
    // arbitration — a strict casus-belli check has no weights.

    // ENCROACHMENT: a rival STRUCTURE seen within this Chebyshev range of my
    // castle is a trespass — close enough to contest my land bank / claims.
    // Sized just inside SiteSearchRange so it fires on the rivals I'd actually
    // be expanding into.
    public int EncroachmentRadius { get; init; } = 28;

    // OPPORTUNISM: strike a rival objective only when my committable army power
    // is at least this PERCENT of the rival force defending it (visible units
    // near the target). 200 = "twice their guard" — a predator picks fights it
    // wins, not fair ones.
    public int OpportunismPowerPercent { get; init; } = 200;

    // The offensive CAMPAIGN army size (Phase 4): the soldier quota a colony
    // musters once it has a campaign, and the force-parity floor before the
    // column marches. Larger than the peacetime garrison floor — a siege needs
    // a real army, not the home guard.
    public int CampaignArmySize { get; init; } = 8;

    // How far a campaign army will march from home to reach a target — the
    // offensive cousin of the pursuit leash. Beyond it, a rival objective is
    // too far to project force onto and isn't considered.
    public int CampaignReachTiles { get; init; } = 120;

    // Print each decision to the console (--ai-trace 1).
    public bool TracePrint { get; init; } = false;

    // DecisionTrace ring size. 256 ≈ 10 game-days of hourly thinks —
    // plenty for a live server; the balance lab cranks it to keep a whole
    // match's decisions for post-mortem.
    public int TraceCapacity { get; init; } = 256;
}
