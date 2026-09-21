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
    // M35 — baselines are no longer a flat 5000 (docs/environmental-
    // fertility.md): a dry-edge farm whose worst tile CAN'T climb back to
    // ResumeSoilAbove would rest forever. So a resting farm re-enters at
    //   max(RestSoilBelow, min(ResumeSoilAbove, worstBaseline - ResumeSoilWornWithin))
    // i.e. "within 500 of what this land can be", capped by the absolute
    // bar. At a flat 5000 baseline that is exactly 4500 — the lab-pinned
    // default — so strength 0 is unchanged. RestSoilBelow stays ABSOLUTE:
    // it guards the permanent desert latch, which is an absolute line.
    public int ResumeSoilWornWithin { get; init; } = 500;

    // M25 — how many of the AI factions run the Rival (conquest) ladder
    // instead of the Homesteader (docs/m25-rival-spec.md). The host gives
    // the HIGHEST AI faction ids to Rivals, so faction 1 — the balance
    // lab's baseline Homesteader in every pre-M25 test — keeps its golden
    // curve. 0 = a fully peaceful field (the pre-M25 world, and the
    // default: aggression is opt-in via --rivals).
    public int RivalCount { get; init; } = 0;

    // M25 — ENEMY INTEL. Castle intel older than this is not a war plan
    // (the target may have grown a garrison since); WarRung refuses it
    // and ProbeRung re-earns it. 6 game-days ≈ two probe round-trips at
    // enemy-castle distances.
    public long IntelStaleTicks { get; init; } = 6 * Time.Day;
    // Probe legs before the Rival's scouting pressure rests (every
    // standing job has a bound — ledger #9). The budget REFRESHES when
    // the castle map changes (EnemyIntel resets ProbeLeg on a castle
    // discovered or observed razed), so it bounds continuous blind
    // sweeping, not lifetime exploration. 24 = 1.5 spiral revolutions
    // at growing reach.
    public int ProbeLegBudget { get; init; } = 24;

    // M25 — WAR POLICY (WarRung). Young colonies homestead: below the
    // population floor the Rival's early curve is EXACTLY the
    // Homesteader's (the pre-M25 golden signature, by construction).
    public int CampaignPopulationFloor { get; init; } = 30;
    // Declare only from strength: own military power ×100 must clear the
    // enemy estimate × this. Wars are picked, not stumbled into.
    public int WarAdvantageRatioPercent { get; init; } = 150;
    // The GO gate at campaign launch (ConquerRung) — declaration is
    // strategic, marching is operational; the enemy may have mustered
    // during the telegraph window, so the ratio is re-checked with
    // fresher eyes before anyone walks.
    public int AttackOvermatchPercent { get; init; } = 150;
    // Below this percent of the enemy estimate the campaign withdraws
    // and the war sues for peace — parity lost is campaign over, not a
    // death spiral. 100 = retreat the moment we're no longer ahead.
    public int RetreatBelowPercent { get; init; } = 100;
    // The OFFENSE budget: one campaign soldier per this many mouths, ON
    // TOP of the peacetime garrison, clamped by the wartime ceiling
    // (WarPopulationPerSoldier) — the budget scales with the society
    // that pays it (ledger #11), and the garrison never marches.
    public int OffensePopulationPerSoldier { get; init; } = 6;
    // Raid party size (RaidRung; ledger #9 — a bounded job, not a
    // horde). 0 disables raiding entirely — the doctrine A/B switch
    // (raid-first vs siege-rush) the Phase-5 lab measures.
    public int RaidPartySize { get; init; } = 3;
    // Never plan against zero: fog hides garrisons, so the enemy
    // estimate is floored here (≈4 bare soldiers) no matter how empty
    // their fields look.
    public int AssumedGarrisonPower { get; init; } = 12;

    // SCAVENGING DEAD KINGDOMS (ScavengeRung, 2026-07-13) — a DEFEATED
    // faction is hostile-to-all (PlayerDefeatedEvent marks the rows), so
    // its ruins are anyone's: a bounded expedition (ledger #9) of SURPLUS
    // soldiers (above the peacetime quota — the garrison never leaves)
    // marches to known structures of the fallen, razes them by presence
    // (the M24 auto-siege), and hauls the spilled vaults home. Once
    // nothing is left to raze, spare Haulers (above HaulerFloor) join the
    // freight loop — a soldier lugs 5, a hauler 25, and a castle vault is
    // hundreds. 0 disables the rung entirely.
    public int ScavengePartySize { get; init; } = 4;
    // Expedition reach, Chebyshev from the castle. Wider than every other
    // leash — the prize is a kingdom's whole treasury — but still bounded:
    // a dead empire across the map belongs to whoever lives next to it.
    public int ScavengeRangeTiles { get; init; } = 80;

    // BATTLEFIELD SALVAGE (SalvageRung, 2026-07-13) — the dead drop their
    // cargo and equipment as a ground pile under a grave marker; an idle
    // civilian crew walks out, loads the pile, and hauls it home to the
    // castle. Crew size is the job's budget (ledger #9 — a bounded detail,
    // not a procession), additionally capped at one hand per known grave.
    // 0 disables the rung entirely.
    public int SalvageCrewSize { get; init; } = 2;
    // Only graves within this Chebyshev reach of the castle are worth a
    // civilian's walk. Matches PursuitLeashTiles by design: the ground the
    // colony is willing to fight over is the ground it's willing to loot —
    // anything farther is someone else's battlefield.
    public int SalvageLeashTiles { get; init; } = 24;

    // M26 — FORTIFY (walls & gates, docs/walls-and-gates.md). The colony
    // rings its castle once it's a town with stone to spare. The radius
    // is Chebyshev — a square ring around the keep; 0 disables the rung.
    // 6 sits just outside the castle's vision disc (5): far enough that
    // the inner farms/houses keep their ground, close enough that ring
    // tiles enter the remembered map from ordinary near-castle traffic.
    public int FortifyRadius { get; init; } = 6;
    // Placement waits behind this castle STONE stock. Walls are
    // stone-heavy (per-tile cost × a ~48-tile ring is a war-chest-sized
    // project) and Fortify must never outbid the Barracks bootstrap or
    // any other stone use — the floor is the fortification's own
    // savings account, comfortably under ResourceStockTarget so the
    // haul cap can actually fill it.
    public int FortifyStoneFloor { get; init; } = 120;
    // ...and behind this population: a camp doesn't build city walls.
    // Below the floor the rung is entirely inert — the pre-M26 curve.
    public int FortifyPopulationFloor { get; init; } = 25;
    // Longest wall LINE per think (one PlaceWallIntent). Short segments
    // localize server-side rejections — the whole line fails clean on
    // one bad tile, and BrainCore's bisect feedback hunts it down.
    public int FortifySegmentMax { get; init; } = 6;
    // Outstanding fort construction sites cap. Every open site claims a
    // background delivery carrier per think (LogisticsLayer), so an
    // unbounded ring of sites would starve the food hauls — the cap IS
    // the fortification's logistics budget.
    public int FortifyMaxOpenSites { get; init; } = 6;
    // Quarry staffing target while the stone floor is unmet. Two hands
    // ≈ 8 stone/game-day with the Quarryman bonus — a wall segment
    // every day or two without bleeding the farm ledger.
    public int FortifyQuarryWorkers { get; init; } = 2;

    // M27 — IRRIGATE (canals for farms, docs/canals.md update). The colony
    // digs a canal from known water into its farm belt once enough farm
    // claims sit beyond the water-recovery radius: irrigated fields rest
    // at WaterRecoveryAmount (double rainfall by default), so the same
    // land sustains more mouths. Longest dig per project; 0 disables the
    // rung entirely.
    public int IrrigateMaxCanalTiles { get; init; } = 6;
    // A camp doesn't dig canals — same reasoning as the Fortify floor.
    public int IrrigatePopulationFloor { get; init; } = 25;
    // Dig only when the canal's END tile would put at least this many DRY
    // farm-claim tiles inside the recovery radius (and don't bother
    // planning below this many dry tiles total). The dig must water a
    // real field, not a hedgerow.
    public int IrrigateMinDryClaimTiles { get; init; } = 6;
    // Start the dig only above this castle stone stock. The floor means
    // the quarry income exists and a short canal is fundable soon, not
    // that the full price is banked (logistics delivers stone to the
    // site over the build). Retuned 200 -> 80 alongside the canal cost
    // cut (150 -> 50 stone/tile): 200 was unreachable on a single-quarry
    // economy AND wasn't even enough for one old-priced tile, so canals
    // were never dug (observed: 8 rivals, 0 canals in 150 days).
    public int IrrigateStoneFloor { get; init; } = 80;
    // While a canal is WANTED but stone is short, staff the quarry to this
    // many workers (the Quarry WorkerCap is 3) to accelerate the stone
    // income specifically for the dig — canals compete with walls for
    // stone, so a dig-in-waiting earns the extra pick.
    public int IrrigateQuarryWorkers { get; init; } = 3;
    // Mirror of the world's BiomeDegradationConfig.WaterRecoveryRadius
    // (config, not on the wire — the demographic-mirror convention;
    // update if the world knob is retuned). Tracks the world default (4).
    public int IrrigateWaterRadius { get; init; } = 4;

    // ARMING (ForgeRung + ArmRung, 2026-09-19, docs/refining-structures.md
    // "SmeltRung" deferral cashed in). The colony raises a Smithy and
    // forges SHIELDS for its garrison from wood + stone alone, then — once
    // it knows Hills — a Mine and a Smelter, and forges SWORDS from the
    // iron (Ore + fuel -> Iron -> Sword: the second hop every human can
    // automate and the AI must be taught). False disables both rungs and
    // the feed lines — the pre-arming curves, byte for byte.
    public bool Arm { get; init; } = true;
    // A camp doesn't run a smithy. Higher than the Fortify floor (25): the
    // haul-belt lab showed a marginal colony that armed at 25 collapsing
    // from 57 pop to 7 while its unarmed twin lived — three sites and
    // two crews are a town's spend, not a village's. Below it both rungs
    // are entirely inert.
    public int ArmPopulationFloor { get; init; } = 40;
    // Workers staffed onto the Mine and the Smelter (WorkerCap 3 and 2).
    // One hand at each ≈ 1 ore and 1 iron a day (generalists) — a sword
    // every three days, a garrison of eight armed in a month: an armoury,
    // not a second economy. The lab priced two-and-two at ~15% of the
    // population by day 200; the labor ledger's slack gate keeps even
    // this from bleeding the farms.
    public int ArmMineWorkers { get; init; } = 1;
    public int ArmSmelterWorkers { get; init; } = 1;
    // The Smithy's working stock of wood / stone / iron (each), kept
    // topped up from the castle (and the Smelter) by the feed lines in
    // LogisticsLayer. A shield is 5+5, a sword 2 wood + 3 iron: twenty of
    // each is a few items' worth, not a second warehouse.
    public int ArmSmithyStockTarget { get; init; } = 20;
    // Refeed a Smelter input when it drops below this (InputCap is 60).
    // Twenty ore is ten batches — a couple of days of smelting between
    // hauls, so the feed line never dominates the carrier pool.
    public int ArmFeedFloor { get; init; } = 20;
    // The castle keeps at least this much wood / stone before the feed
    // lines draw on it: construction and the Barracks bootstrap outrank
    // fuel and hafts (a smithy that starves the third farm of its 10 wood
    // is the arbitration #10 wedge in a new coat).
    public int ArmCastleReserve { get; init; } = 40;

    // THE HAUL BELT (2026-09-19, the food-wall post-mortem): the baseline
    // lab trained 1 Hauler against 23 Farmers in 170 days, then spent
    // ~4.5 food hauls per think carrying 5 turnips at a time from ten
    // far farms — the "food wall" was people on the road, not yield.
    // The hauler floor now RISES with the belt: one Hauler per this many
    // own extractors (farms, camps, quarry, mine), never below
    // HaulerFloor. A carted Hauler moves 50 a trip against a citizen's
    // 5, so each one retires several field hands from the road.
    public int ExtractorsPerHauler { get; init; } = 3;

    // CARTS (CartRung, docs/cart.md + docs/refining-structures.md): raise
    // a Workshop and forge a Cart for every Hauler — +25 carry at +50%
    // hop time, the logistics multiplier a human buys first. Fires above
    // the population floor and outside famine; the site waits for the
    // castle to hold its cost (lesson #10). False disables the rung and
    // the workshop feed line.
    public bool Carts { get; init; } = true;
    // Lower than the Fortify/Arm floor on purpose: the haul belt is what
    // the colony grows on, and the wall it fixes arrives at 60-90 pop.
    public int CartPopulationFloor { get; init; } = 20;

    // Print each decision to the console (--ai-trace 1).
    public bool TracePrint { get; init; } = false;

    // DecisionTrace ring size. 256 ≈ 10 game-days of hourly thinks —
    // plenty for a live server; the balance lab cranks it to keep a whole
    // match's decisions for post-mortem.
    public int TraceCapacity { get; init; } = 256;
}
