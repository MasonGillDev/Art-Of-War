# M19 Spec — Per-House Food (localized feeding)

The single-castle food sink is the measured wall: every balance-lab
colony dies at pop ~100–150 because every crumb walks home to one
castle while farms march outward over burned land
(docs/ai-players.md, the Malthus endings). This milestone moves the
food sink to where people LIVE: citizens eat from their HOME's larder
— a house on the frontier feeds the frontier — and the supply network
reorganizes from hub-and-spoke into specialized pockets that trade.
It is the upgrade path `docs/food-consumption.md` designed on day one
("Per-House consumption… the harder system; the more interesting
gameplay") and the milestone M18's standing orders were waiting for:
"keep this house stocked" is automation's killer app.

This is a CORE milestone (a world rule), followed by a brain phase so
the Homesteader can play it — the same order as claims, famine, and
bandits before it.

## Locked decisions (user, 2026-06-12)

- **Auto-assignment.** No new player chores: homes assign themselves
  through three deterministic, event-driven triggers (below). An
  explicit `AssignHomeIntent` can layer on later; none ships now.
- **Harsh local famine.** A dry house runs the FULL famine-debt
  machinery — debt, grace, death cadence among its own residents —
  even if the castle larder is full. The soft alternative (fall back
  to the castle sink) teleports demand across the map and breaks the
  "nothing teleports" spine. A region without a stocked granary
  starves; that's the gameplay.
- **`ResidentCap = 5` per house** (catalog knob). The castle is
  UNCAPPED — deep larder, mess hall for the mobile class, loss
  condition. Capacity pressure must never block breeding (below).
- **Breeding is never housing-gated.** A full birth-house overflows
  the newborn (nearest free bed, castle fallback) instead of
  rejecting the birth — a hard cap would reintroduce the one-house
  population ceiling the lab already found, as a rule. A housing
  shortage makes feeding EXPENSIVE (castle-homed mouths eat off the
  long road), it never freezes the population.
- **Eating is bookkeeping, not commuting.** A citizen's home is their
  demand point — meals deduct from the home's stock wherever the
  citizen stands (exactly today's castle rule). The PHYSICALITY lives
  on the food's side: grain is grown, hauled tile-by-tile into the
  house cache, and robbable in transit. No walk-to-lunch events.
- **Home follows work; haulers stay castle-homed.** Workers re-home
  near their workplace (triggers below). Haulers/scouts are itinerant
  — "near" is wherever they stand mid-trip — so the mobile class
  keeps the castle as its mess hall.

## The model

### Home

`Unit.Home : TileCoord?` — the tile of the unit's home HOUSE; `null`
= homed at the owner's castle (the default, and the universal
fallback). Houses gain `ResidentCount` maintained with the
single-mutation discipline (`Population.OnHomeChanged` is the only
writer — same pattern as `Player.PopulationCount`). Castle residents
are derived: `PopulationCount − Σ house.ResidentCount`.

### Auto-assignment — three triggers, all discrete events

1. **Birth** (`BirthEvent`): the child homes at the birth house if a
   bed is free; else the nearest house with a free bed (Chebyshev
   ring scan from the birth house in (dist, y, x) order); else the
   castle (`null`).
2. **Assignment** (`AssignWorkersIntent` / `AssignBuildersIntent`,
   after the per-id assignment succeeds): the unit re-homes to the
   nearest house with a free bed within `HomeAssignRadius` of the
   workplace tile, ties (dist, y, x). No bed in radius → home stays.
   Same house → no-op.
3. **House completion** (`BuildCompleteEvent` → House): the new house
   scans own citizens currently WORKING/BUILDING within
   `HomeAssignRadius` whose home sits farther from their workplace
   (their own tile — working units stand on their post) than this
   house does; re-homes them nearest-first (then unit id) until the
   beds fill. This closes the obvious gap: frontier farms are staffed
   BEFORE their house exists, and assignments are sticky for months.

Cleanup paths: resident dies → bed frees (no rebalance; the next
birth/assignment uses it). Home house changes owner or is destroyed
→ residents re-home to castle (`null`). Embarked units keep their
home (they drain it from the sea, exactly as they drain the castle
today).

### The sink split

The famine-debt machinery — `LastFoodConsumedTick`, `FoodDebt`,
`FamineStartTick`, `NextFamineCheckTick/Seq`,
`NextStarvationDeathTick/Seq`, the lazy catch-up, the
self-rescheduling `FamineCheckEvent`/`StarvationDeathEvent` with
their fences — GENERALIZES from "the Castle" to "any food home"
(Castle + House; hoisted into a shared shape so `FoodConsumption.
CatchUp` takes the home, not the Castle class). Per home:

- **Rate** = that home's resident count × `FoodPerCitizenPerPeriod`.
  Rate-changing events grow by exactly one kind: HOME CHANGE
  (re-assignment catches up BOTH sinks at the same `now` — the same
  two-sided pattern capture uses on castles today).
- **Draw** = the home's own stock: `Castle.Holdings[Food]` or the
  house's existing food cache (the same physical cache births spend —
  one stock, competing uses, deliberately).
- **Famine** = per home, harsh: debt accrues at the home's full
  resident rate; deposits to THAT home pay ITS debt first; the death
  cadence picks the OLDEST RESIDENT of that home (BornTick, then id).
  The castle's machinery is untouched in behavior — it is simply one
  more home whose residents happen to be the mobile class.

A world with no houses (or houses with no residents) is bit-for-bit
the old model — every existing food test runs unchanged on the
castle-homed default.

### Knobs

- `StructureSpec.ResidentCap` — House 5, Castle 0 (= uncapped).
- `FoodConsumptionConstants.HomeAssignRadius` — default 8 (a house
  serves the work cluster around it, not the whole map). Both are
  balance knobs; tests derive from them per the standing convention.

## What gets built (phases, each ends green)

1. **Home plumbing** — `Unit.Home`, `House.ResidentCount`,
   `Population.SetHome` (the single writer), the three triggers +
   death-frees-the-bed, snapshot FormatVersion 13. (Migration follows
   the standing snapshot-on-deploy policy — old versions are rejected,
   not converted.) Consumption math untouched this phase.
2. **The sink split** — generalize CatchUp/events/deposit paths to
   food-homes; per-home anchors snapshot-carried; RegenerateQueue
   regenerates per-home events; `ViewProjector` exposes own-only
   per-house effective food (signed, like `CastleFood`) so a player —
   and therefore the brain — can see a hungry house.
3. **Brain conformity** — LogisticsLayer stocks houses against
   CONSUMPTION (residents × a config-derived buffer), not just
   births; Grow places houses near the work cluster they will feed
   (nearest farm cluster, not nearest-to-castle); the balance lab
   measures the wall moving (the whole point — the 300-day curve's
   pop ceiling is the acceptance metric).
4. **Client** (Unity repo, after the server pushes) — house food on
   the HUD, local-famine toast.

## Headline tests

- **`Food_TwinRun_HashesMatch_AcrossHomeChurn`** — births with
  overflow, re-homings, a local famine with deaths, deposits to
  multiple homes: twin runs hash-equal (the M-level contract).
- **`LocalFamine_StarvesTheDistrict_NotTheRealm`** — a dry frontier
  house kills its own residents oldest-first while the full castle
  feeds everyone else; the harsh-doctrine pin.
- **`HomeAssignment_Triggers`** — birth-house bed, overflow to
  nearest bed, castle fallback; home-follows-work on assignment;
  house-completion move-in ordered and capped; owner-change fallback.
- **`Deposit_PaysTheHomesDebtFirst`** — the famine-debt contract,
  per home.
- **`Snapshot_RoundTrip_V13`** + **`Recovery_MidLocalFamine_Identical`**
  — persistence contract (snapshot-on-deploy handles old versions).
- **`NoHouses_BehavesAsCastleOnly`** — the equivalence pin: a world
  whose citizens are all castle-homed consumes exactly as the M13
  model did; the existing food test suite running green IS this pin
  in practice (its worlds have no occupied houses).
- Balance lab: `BalanceLab` curve re-baselined; the acceptance
  question is whether the population ceiling MOVES (pop 98/151 today).

## Explicitly deferred

- `AssignHomeIntent` (manual control), home UI beyond the HUD line.
- Castle-as-crafting-hub / tribute / any "castle relevance" levers —
  only if play shows the castle hollowing out.
- Per-role diets, morale, migration pressure.
- Frontier forts (garrison re-homing), settlement/second-castle
  structures — the boats/frontier milestone builds on this one.

## References

- `docs/food-consumption.md` — the castle-only decision this
  supersedes ON SCHEDULE (its Future-expansion section designed this
  milestone); the famine-debt addendum whose machinery generalizes.
- `docs/m17-defender-spec.md` — harsh local famine is what makes
  sieges emerge for free (surround a pocket, kill its haulers).
- `docs/architecture.md` §2.5/§2.6 — lazy catch-up + fencing, now
  per-home; §3 rule 7 — `ResidentCount` single mutation point.
- `docs/automation-engine.md` (M18) — standing orders are the
  ergonomic answer to stocking many houses.
