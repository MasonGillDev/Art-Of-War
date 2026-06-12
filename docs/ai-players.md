# AI Players — fair, fogged, full factions

## The decision

AI players are **ordinary factions driven by a view-only brain**. Each
AI faction is a normal `FactionStartSpec` with the player's exact
starting loadout (`--ai N`, default 1; the old token "neutral scout"
faction is retired). Its driver (`Sim.Server/Ai/AiPlayerDriver`) follows
the M16 bandit-driver architecture — reads in, ordinary durable intents
out, ephemeral state — with one stricter rule: **the brain's only input
is the projected `ViewDto`**, the same fog-filtered payload a human
client renders. Fairness is structural (the brain's signature cannot
receive `GameWorld`/`Simulation`; pinned by
`AiPlayerTests.Brain_TouchesOnlyTheView`), not a policy.

The first brain is the **Homesteader** — a peaceful economy player:
bootstrap a farm inside the 200-food runway, build a lumber camp, keep
buffers hauled, breed when the larder allows, scout with Scout-role
units. Strict-priority ladder + background logistics; every think is
recorded in a `DecisionTrace` ring buffer (`--ai-trace 1` prints live).

## Why

- **Completely fair (user decision, 2026-06-11).** No information
  cheats, no discounts — ever. Difficulty, if wanted later, will be
  handicaps, never wallhacks. Fairness is what makes the balance lab
  trustworthy and the AI a genuine new-player simulator. The brain MAY
  read Sim.Core catalogs/enums — game rules a human knows from the UI —
  but no world state.
- **The balance lab is the point.** AI-vs-AI headless matches turn
  balance questions into test runs:
  `Homesteader_Survives100GameDays_NoStarvationDeath` IS the "is the
  opening winnable?" question, frozen as CI. Retune food constants and
  the answer is a 3-second test, not an evening of play.
- **Second proof of the automation architecture.** Replay-from-intent-log
  hash equality holds for a full 15-game-day economy run
  (`Ai_ReplayFromIntentLog_HashesMatch`) — the driver's decisions live
  in the durable log, the brain is disposable. The player standing-orders
  layer inherits a twice-proven shape.

## The arbitration ledger (the real cost, paid and documented)

The spec predicted arbitration — not behaviors — would be where the work
lives. It was right five times in one milestone. Each was found in
minutes via the decision trace + deterministic replay; each is the kind
of bug that's an afternoon of mystery in a non-deterministic engine:

1. **Priority greed** — Eat claimed the think for routine food hauls;
   Build/Grow starved. Fix: hauling is BACKGROUND (logistics layer runs
   every think), only real decisions arbitrate.
2. **Double-tasking** — two layers tasked the same unit in one think;
   move-on-busy silently cancelled the first order (food hauls yanked
   off carriers by scout moves). Fix: a per-think unit reservation
   shared by every selector.
3. **Job creep** — Scout conscripted any idle adult, marching the whole
   village on wander legs; the carrier pool died. Fix: scouting is for
   Scout-role units only.
4. **Capacity blindness** — hauls went out on capacity-5 villagers while
   the two 25-capacity Haulers farmed; the farm sat dormant-at-cap for
   days and food flatlined at the famine line. Fix: swarm buffers with
   enough carriers to cover them (capacity is catalog knowledge);
   Haulers excluded from non-hauling duties.
5. **Reservation order** — logistics ran before the strategic ladder and
   reserved every idle unit, so staffing never got a candidate (the camp
   sat unstaffed for 29 days). Fix: strategic decisions reserve first,
   hauls take whoever's left. Priority isn't just rung order — it's who
   reserves people first.

Plus one **systemic find** (game knowledge, not AI knowledge): a haul
that over-picks for a small need strands the leftover ON the carrier —
`HaulIntent` has no amount parameter, so a 25-capacity pickup against a
10-wood site need leaves 15 wood stuck on a unit that every selector
then ignores. The brain works around it (laden idle units walk home and
`UnloadCargo`; one delivery in flight per site) — but a human player
hits the same trap, which makes "partial-amount hauls or auto-unload"
a UX candidate for the core game.

## Update 2026-06-11 — the farm retune campaign (lessons 6–9)

The first real balance-lab CAMPAIGN: Farm output cut 4× (2/1h → 1/2h;
18 mouths per farm, a farmer feeds 6) to make food a continuing project
instead of a solved one. Roughly a dozen lab iterations followed — the
parameter held; the BRAIN needed four more capabilities to play the
economy the parameter created, and the arbitration ledger grew:

6. **Cross-think plans need OWNERSHIP, not reservations** — Grow freed
   a farmhand to breed; Eat re-staffed them one think later; thrash
   forever (one birth in 130 days). Fix: designated parents persist in
   memory until breeding starts; every other selector skips them.
   Corollary bug: the carrying-capacity gate counted designated parents
   as missing labor and deadlocked shut — ledgers must count people, not
   availability.
7. **Mouths are easy to plan; ADULTS are the scarce resource** — a child
   eats for 13 game-years before it can work. Unthrottled breeding outran
   the workforce into extinction twice. Fix: the labor ledger + the
   carrying-capacity gate (breed only when the adult pool clears demand
   with margin). Population now grows in honest generational waves.
8. **Price labor honestly** — only Farmer-role hands earn the 2:1 bonus;
   the first ledger valued everyone as a farmer, overstating supply ~40%
   and riding the colony off a knife-edge. Role scarcity is REAL under
   the new economy: training farmers at a School is the designed relief
   valve (next brain phase).
9. **Jobs need budgets** — perpetual scouting cost two adults forever;
   the urgent farm-backstop once built six farms in ten days; staffing
   to cap conscripted the village. Every standing job now has a bound
   (ScoutLegBudget, +1 insurance farm, worker budgets).
10. **Don't break ground you can't cover** (M17 Phase 2, the Muster
   rung's first lab run) — placing a site is free, but deliveries run
   in (y,x) TILE order, not rung order, and an ASSIGNED builder is
   locked (Activity=Building, invisible to every selector) on a site
   whose delivery queue it doesn't control. A 100-wood Barracks placed
   against a day-5 economy wedged the whole queue: the third farm (10
   wood, last in tile order) starved with both builders locked on it,
   the fully-provisioned camp never got a builder, wood income died,
   five sites sat for 20 days. TWO fixes, both the player's own rules:
   Muster waits until the castle HOLDS the full build cost, and
   EnsureBuilders assigns only once every material is on site (march
   early — a walking builder is re-targetable; a hammering one isn't).
   The second fix HEALED the long-standing faction-1 bootstrap
   collapse (`Survives100GameDays` red since the demographic campaign
   — same wedge, smaller demands).
11. **Sparta starves** — the first standing-army quota (flat floor of
   4) was a ~30% defense budget against a 14-person genesis: the labor
   pool shrank, the carrying-capacity brake (correctly) blocked
   breeding, the colony froze at pop 17 and died with the founders
   (control without an army: pop 151). Same lesson as the
   proportional growth brake, re-learned for the military: THE BUDGET
   MUST SCALE WITH THE SOCIETY THAT PAYS IT. Quota is now capped at
   one soldier per PopulationPerSoldier mouths (~12%); the threat
   curve agrees — bandit parties scale with structures, so a colony
   too small to afford a garrison is also too small to draw a raid.
   With the cap: pop 98 at day 220 carrying its garrison the whole
   way.

Systemic game finds along the way (the new-player simulator earning
its keep): **storage pressure is real** (an AI hoarded 4,600 wood and
choked food intake against the castle's 5,000 cap — players will too);
**the synchronized fertility cliff** (uniform genesis ages = all
founders infertile the same day; fixed in WorldFactory with staggered
ages 18–40, which benefits humans equally); **viable starts need a
meadow, not a tile** (FindAiStart now demands ≥40 grassland within
ring 6 — a proto fair-start rule).

**The 160-day curve that shipped the retune** (faction 0, seed 7):
pop 14 → carrying-capacity plateau at ~19–21 while the granary banks to
~2,100 → all three founding farms exhaust their claims around day
104–130 and the colony eats its savings down to 320 → death-detection
releases the crews, FIVE replacement farms go up by day 140 → food and
population climbing again by day 160 (pop 25–27, second generation
breeding). Stability → land crisis → rotation sprawl → recovery: the
loop the claims system was designed to force, now demonstrably forced.

## Update 2026-06-11 (later) — the demographic campaign: pop 21 → 138

A second lab campaign, driven by the user's TicksPerYear/lifespan
experiments, gave the Homesteader the rest of its economy toolkit. Each
capability was demanded by a measured failure; peak stable population
roughly doubled per tool:

- **Demand-driven exploration + the land bank** — fog made
  SiteSearchRange meaningless (a colony starved at pop 67 with the
  continent unexplored); crisis-triggered scouting rescued ~6 days too
  late, so scouts now go out when known claimable land drops below a
  floor, BEFORE anything is wrong, spiraling wider per sweep.
- **Farm mortality accounting** — panic-built farm cohorts died in
  synchronized cliffs (~104-day claim life); farms within ReplaceAhead
  of their working lifetime stop counting as supply so replacements
  pre-build. Observation (12 thinks of zero buffer) stays ground truth;
  the husks and their claims remain locked (DemolishIntent deferral).
- **The Train rung** — the single biggest jump (pop 69 → 159 peak).
  School + TrainUnitIntent had NEVER been used by anyone, human or AI;
  natives are born Role=None, perfect apprentices; each graduated
  Farmer doubles a hand. Designation ownership reused for the
  apprentice's walk to school.
- **Camp rotation** — training's boom exposed that only farms had
  death-detection: the lone lumber camp died at ~day 52, wood hit zero
  by day 200, and 159 people starved unable to afford a 10-wood farm.
  Exhaustion detection generalized to all extractors; one LIVE camp is
  now an invariant; a forest-starved Build rung re-opens scouting.

**Where the Homesteader's story ends (for now):** at the user's
fast-clock settings it sustains a ~135-population colony in a 50-day
steady state — then collapses against the SINGLE-CASTLE FOOD SINK:
every crumb walks home to one castle while farms march outward over
burned land. That wall is not brain-fixable; it is the per-House food
consumption milestone (deferred in docs/food-consumption.md) plus
DemolishIntent, now both carrying measured price tags. The balance lab
at this scale runs ~2 minutes per 300-day match (pop ~140, two brains).

## Future expansion

- **Defender** (phase 2) — SHIPPED 2026-06-12
  (docs/m17-defender-spec.md): the brain decomposed file-per-rung,
  Muster (standing army, population-capped quota), Defend (threat
  memory, leash-bounded pursuit, recall doctrine — pinned ON by the
  A/B after OFF got a faction wiped), gate test un-skipped and green.
  Open: the SIEGE POVERTY TRAP (bandits arrive before the army is
  affordable; a parked ambusher freezes the recall-paused economy at
  pop 10 — war-footing quota / pressure retune / equipment are the
  priced candidates), loot recovery (cargo piles aren't on the wire
  for anyone — core/wire work item), group sorties.
- **Rival** (phase 3) — scouting pressure, diplomacy through the normal
  intents, supply-line raids. Conquest waits on win conditions.
- **Training** — the Homesteader lives with its genesis roster; a School
  + TrainUnitIntent rung (more Haulers!) is the highest-value economy
  upgrade and the natural fix for arbitration find #4.
- **Multi-house breeding, second farms, claim rotation** as the
  demographic curve demands.

## References

- `docs/m17-ai-players-spec.md` — the spec (fairness contract,
  arbitration section, ladder design).
- `docs/bandits.md` — the driver architecture inherited (and its
  pitfalls: Idle-while-moving, replay interleaving, ephemeral memory).
- `docs/food-consumption.md` — the famine-debt opening the Eat rung
  beats; `docs/extraction-claims.md` — auto-select placement the brain
  leans on.
