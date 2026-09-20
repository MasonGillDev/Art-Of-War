# The Automation Substrate — one order record, claims, recipes as data

**Status: decided 2026-08-06. Supersedes the M18 order MODEL; retains the M18
trust boundary.** The vision this serves is `docs/automation-as-core-game.md`
(the laws, the judgment test, the scenarios). This doc is the engineering
base those ideas stand on — designed so that adding automation N+1 is a data
row, never engine work.

## The decision

Player automation is rebuilt on four commitments:

1. **One universal order record.** Every automation in the game — supply
   line, breeding, training quota, staffing, caravan reinforcement, patrol —
   is a single `Order` type: Subject + Trigger + Program + Crew + Reinforce
   + Flags. There are exactly two program shapes at launch: **Maintain**
   (the thermostat: detect shortfall → pull → recipe → release) and
   **Routine** (a repeating step sequence: routes, patrols, sweeps). New
   automation behaviors are added as *recipe rows* (data configurations of
   Maintain) or new step verbs — not as new engine paths.

2. **A first-class Claims Ledger in sim state.** `Claim { UnitId, OrderId,
   Purpose: Crew | InFlight }`, serialized, mutated only by server-internal
   intents (the M18 durable-cursor precedent). A unit is claimed the moment
   an order **commits** to it — claim-on-commit — and the claim is visible
   to every condition from that instant. "Dormant" becomes a derived,
   queryable fact: not working, not grouped, not breeding, not claimed.

3. **Selectors are WHERE clauses, never rankings.** The only place any
   order answers "who": a dumb filter (role, age, dormancy, radius,
   !Protected, …) resolved in a canonical total order — (distance, y, x,
   id), first match wins. No scoring, no "best," no judgment. This is the
   mechanical enforcement of the vision's zero-judgment law.

4. **Sequential evaluation with visible pending claims.** The driver
   evaluates a player's orders in (Priority, OrderId) order within a think
   tick, and claims made earlier in the pass are visible to conditions later
   in the pass. This makes in-flight-aware conditions work
   (`count(Hauler) + inflight(Hauler) < N`), bounds redundant-producer
   overshoot to +1 per producer, and resolves all pool contention
   deterministically at claim time.

**What is scrapped:** the M18 order model — step-cursor template compilation
(SupplyLine/Route/StandingCraft as step programs), `ConditionSpec`/`ActionSpec`
as the sole vocabulary, named-units-only subjects. That code may be deleted
outright; it cannot express acting on units that don't exist at authoring
time, and every workaround bolts judgment or a god-system onto it.

**What is kept, unchanged:** the M18 trust boundary and its disciplines —
orders as inert Sim.Core state; evaluation in a server-side driver that
reads fog-filtered views and emits ordinary intents; durable progress via
server-internal intents; append-only serialized enums; retry/auto-disable;
fail-clean steps; the replay headline test (driver off, intent log replays,
snapshot hashes match); the fog contract (a condition on a subject the owner
cannot see never evaluates true); canonical arbitration ordering. Three
driver generations (M16 bandits, M17 AI, M18 automation) paid for those
lessons; we are replacing the language, not the trust model.

## Why

### What forced the redesign

The vision's feed-and-breed loop (user-designed, 2026-08-06) requires four
capabilities the M18 model structurally lacks:

- **Filtered subjects** — a breeding order must pull "2 fertile dormant
  units within radius," none of which have IDs when the order is authored.
- **Pull-based maintenance** — quotas ("keep haulers ≥ 6"), staffing ("keep
  this farm at 3"), and reinforcement ("keep this caravan at 3+2") are all
  level-triggered pulls from shared pools, not fixed step sequences.
- **In-flight-aware conditions** — without counting committed-but-unfinished
  work, redundant producers race and overshoot unboundedly.
- **Per-order self-healing** — replacement must be a property each order
  carries (heal my own crew from the pool), not a global vacancy-watcher.
  A global handler is a mini-Homesteader: cross-cutting, illegible, and
  judgment-bearing. Relocating it into each order keeps it dumb and local.

### Alternatives that lost

- **Extend the M18 template model** (add filter atoms to step programs).
  Lost: subjects stay named-unit; the thermostat identity gets buried in
  compiled steps; every new behavior needs new template-compilation code —
  exactly the reinvent-the-wheel tax this decision exists to end.
- **In-Core evaluation.** Already lost in `docs/automation-layers.md` and
  the facts haven't changed: it grows the determinism surface per order
  kind; the driver grows it by zero.
- **A global job market for labor** ("any idle unit serves any unmet
  order"). Lost for hauling in M18 (claimed crews are the precondition for
  escorts and the war layer) and lost again here for labor in its naive
  form. Pooled *labor* is allowed only behind the Claims Ledger: pools are
  drained by claim-on-commit through canonical selectors, so assignment is
  sticky, deterministic, and legible — a pull, never a market.
- **Frozen-snapshot batch evaluation** (all orders read the same tick-start
  state). Lost to sequential-with-visible-claims: frozen snapshots make
  in-flight counting impossible within a tick and turn every shared pool
  into a race resolved by collision instead of by claim.

## The layers

### Layer 0 — Sim primitives

| Primitive | Status | Notes |
|---|---|---|
| Verbs (intents) | exist | Move, Haul, Load/Unload, Assign/Unassign, Train, Craft, Equip, BeginBreeding, DispatchScout, FormGroup. Growth rule stands: automation never gets a verb the player lacks; new sim verbs arrive as ordinary intents first. |
| Facts | exist | Pure reads over the fog-filtered view: stocks, roles, ages, activity, counts, positions. |
| Claims Ledger | **new** | Serialized sim state; claim/release via server-internal intents; survives restart; replay-identical. |
| Unit flags | **new** | `Protected` (never conscripted — sacred crews); eligibility gates (trainable age, fertility) exposed as facts. |

### Layer 1 — The order record

```
Order {
  OrderId, OwnerId, Priority
  Subject:   Structure(tile) | Group(id) | RoleCount(role) | Route(a, b)
  Trigger:   AND/OR tree of threshold predicates over Facts (claim-aware counts included)
  Program:   Maintain { target, recipe, selector, fallback? }
           | Routine  { steps[], loop }
  Crew:      Named[unitIds] | Pull(selector)
  Reinforce: crew spec this order self-heals toward (optional)
  Flags:     ConscriptOptIn, …
  Status:    held claims, retry budget, last outcome (durable)
}

Selector {
  Filter: role==R, age≥A, dormant, withinRadius(anchor, r), !Protected, …
  Order:  (distance, y, x, id) — total order, first match wins
}
```

A `fallback` is a fixed preference list (try same-role dormant, else no-role
via the named school), not a branch on situation — an ordered eligibility,
still judgment-zero, per the five-question test in the vision doc.

### Layer 2 — The recipe catalog (data, not code)

| Row | Subject | Shortfall | Pulls | Recipe |
|---|---|---|---|---|
| SupplyLine | Structure (dst) | `stock(dst,R) < min && stock(src,R) > reserve` | — (standing crew) | haul src→dst |
| Breed | Structure (house) | `pop < target` (or Always) | 2 × fertile, dormant, radius | route both → BeginBreeding |
| TrainQuota | RoleCount(R) | `count(R) + inflight(R) < target` | no-role, trainable | route → school → Train(R) |
| Staff | Structure (extractor) | `workers < target` | same-role dormant; fallback no-role→train | route → Assign |
| Reinforce | Group (caravan) | `crew < spec` | per-slot role match, same fallback | route → join |

Breeding never computes global food headroom — that would be goal-AI. It
composes: a SupplyLine keeps the house's larder stocked; Breed fires on the
house's local stock plus whatever castle floor the player ANDs in. The
physical larder is the answer to "can the economy handle another mouth,"
updated by the world itself. Conscription (pulling working units when no
dormant exist) is strictly `ConscriptOptIn` and never takes `Protected`
units.

### Layer 3 — The driver

Per think tick, per player, canonical order: read fog view → evaluate orders
by (Priority, OrderId), claims visible as the pass proceeds → emit ordinary
intents → journal every outcome (fired, stood down, delivered, crew death,
suspended) to a per-order event stream. That stream is the morning report's
backbone. Priority doubles as the async-doctrine suspension order: when
capacity shrinks, lowest-priority orders sleep first — the player's own
triage protects their food lines.

### Layer 4 — Surfaces

Spatial view, panel, and script are three windows compiling to the same
`SetOrder` intent — authoring speed differs, mechanical power never does
(surface parity, per the vision). Blueprints = serialized order sets with
relative anchors. Design-time prediction is pure math over catalog rows;
the morning report compares predicted against actual.

## Acceptance tests

- **Headline (inherited):** run with the driver live; replay the intent log
  with the driver off; `Snapshot.Hash` matches.
- **Keystone lab:** a kingdom running only SupplyLine + Breed rows survives
  160 unattended game-days — zero starvation deaths, population grows.
  (The "survives a working day plus a night at 4 tps" pin.)
- Claims round-trip the snapshot; a restart mid-pull resumes, not repeats.
- Claim exclusivity: two orders want the same unit → deterministic winner,
  loser takes next-in-canonical-order or stands down. Replays identically.
- In-flight counting: two schools maintaining one quota → exactly one
  trains; the other reads quota-met-with-inflight and stands down.
- `Protected` units are never selected by any conscripting pull.
- Priority suspension: capacity reduced → lowest-priority orders suspend
  first, deterministically.
- Fog: a selector anchored where the owner has no vision pulls nothing.

## Build phases

- **A — Layer 0:** Claims Ledger + claim/release internal intents + dormant
  fact + `Protected` flag + snapshot round-trip.
- **B — Order record + Maintain + SupplyLine row:** feature-parity with the
  old supply line on the new model, plus the driver pass and journal stream.
- **C — Breed row + the keystone lab.** Payload first: this is the async
  proof the whole vision hangs on.
- **D — Cutover:** Routine shape (routes/patrols), wire swap, delete M18
  order-model code. The Unity client's OrderMode keeps speaking the old wire
  until this phase; the client repo is unversioned — coordinate the swap
  deliberately.
- **E — Remaining rows:** Train, Staff. **Reinforce is blocked on a sim
  verb** — adding a unit to an existing `Group` has no intent (only Form /
  Move / Disband), and automation never gets a verb the player lacks, so it
  needs its own milestone first. Conscription (`ConscriptOptIn`) is likewise
  deferred: taking a unit that is already Working requires staging an
  unassign before the move, which is a third recipe stage nothing else needs
  yet.

Deferred beyond E: the Reflex/trigger-chain program shape, blueprints,
caps-as-economics, design-time prediction UI, unlock-progression gating
(`docs/automation-as-core-game.md`, "Earning the tools").

## Update 2026-08-06 — the substrate is built; M18 is gone

Shipped: the Claims Ledger + `Protected` (A); the universal Order record,
Maintain, the Haul recipe, the driver pass and journal (B); the Breed
recipe and the claim lifecycle (C); the Routine program and the cutover
(D); Train and Staff (E).

**The M18 order model is deleted.** `StandingOrder`, `OrderStep`,
`ConditionSpec`, `ActionSpec`, its three intents, `AutomationDriver`,
`OrderRunner`, `ConditionEvaluator`, `IntentFactory`, its snapshot section
and its tests are all gone; `GameHost` runs `SubstrateDriver`, and
`ViewDto.Orders` now carries the substrate shape. Snapshot format is v25.

**THE CLIENT MUST BE PORTED.** The Unity repo is separate and unversioned,
so it still speaks the M18 wire and its OrderMode will show nothing until
updated. The delta:

- `OrderDto` replaced: was `{Kind, Loop, CurrentStep, Dispatched,
  RetryCount, StepEnteredTick, ClaimedUnits, Steps[{Conditions[], Action*}]}`;
  is now `{Priority, Program, Recipe, Subject*, Target, Source*, Resource,
  CrewMode, NamedCrew, HeldUnits, Selector, Trigger[][], Steps[], Enabled,
  RetryCount, LastFiredTick, CurrentStep}`.
- `ConditionDto` / `OrderStepDto` are gone; `PredicateDto`, `SelectorDto`,
  `TriggerClauseDto`, `RoutineStepDto` replace them.
- Intents: `SetStandingOrderIntent` / `ClearStandingOrderIntent` →
  `SetOrderIntent` (payload is a whole `Order`) / `ClearOrderIntent`.
- `HeldUnits` is new and worth surfacing: it is who an order is holding
  from the labour pool right now, which is what makes a pooled order's
  behaviour legible on the map.

Four decisions were forced by the build and are now part of the contract:

- **Borrow-and-return, not claim-per-errand.** A Pull order keeps its hand
  while it has work and releases when satisfied. The first cut claimed a
  body per firing and released none, draining the labour pool one unit at a
  time.
- **Contention is not breakage.** "No free hand" never walks an order
  toward auto-disable — labour shortages are transient and an order that
  killed itself over a busy afternoon would be dead by the time the player
  woke up. It is news for the morning report. A NAMED crew with nobody left
  alive still disables (it genuinely cannot recover), which is where law 4
  lives.
- **Recipes observe, never assume.** A staged recipe releases its hands
  when it can SEE the effect landed (breeding started, the role changed,
  the worker is Working) — not when it submitted the intent, whose outcome
  it cannot know synchronously. A rejected verb then simply retries.
- **Pending claims are visible to predicates, not just selectors.** Quota
  conditions count claims submitted earlier in the same pass, or two
  producers both fill one shortfall.

`Staff` deliberately does NOT train a substitute when a trade is scarce: it
reports and waits, and the player's Train quota is the answer. The two
compose through the shared labour pool with neither aware of the other —
law 3 applied to the workforce.

## Update 2026-08-11 — the client is ported, and the journal is on the wire

The Unity client now speaks the substrate. Two things came out of doing it
that are contract, not implementation detail.

### The dashboard's question changed, so the wire had to

M18's order panel answered **"did it break?"** — every row showed a cursor
and a STOPPED flag. Under the substrate that question is nearly always "no":
contention is not breakage, so an order with no free hands waits patiently
and forever. The question worth asking became **"is this working, and if not,
what is it short of?"**, over four states:

| state | meaning | player action |
| --- | --- | --- |
| Resting | trigger not met — the shelf is full | none, healthy |
| Working | dispatched, or its work is in flight | none |
| **Short of hands** | trigger met, nothing free to do it | **the decision**: more people, or raise priority |
| Stopped | a named crew died | re-crew it |

The client could not compute that. A resting order and a starving one are
**identical** through `HeldUnits` + `LastFiredTick`: both empty, both stale.
The one state that needs a player is the one the wire could not express.

The rejected alternative was to re-evaluate triggers client-side — they are
on the wire as DNF clauses, so it looks possible. It would mean a second
`PredicateEvaluator` in the client reproducing the fog contract,
`CountInFlight`, and pending-claim visibility, and when it drifted the
dashboard would lie confidently. A status readout that is sometimes wrong is
worse than none.

Instead `OrderDto` gained **`LastOutcome` / `LastOutcomeTick` / `LastDetail`**,
sourced from `OrderJournal` — which already recorded exactly this vocabulary
and simply was not projected. The journal keeps a `Last(orderId)` map beside
its ring so projection is O(1) per order, and `PruneTo` retires rows for
cleared orders. It stays presentation-only: never hashed, never replayed, and
a server with `ViewProjector.OrderSource` left null projects a byte-identical
world. That is the same seam `GraveSource` uses.

Live-verified end to end: two orders both idle and never-fired projected
`lastOutcome 2` ("Waiting", larder full) and `lastOutcome 3` with detail
*"no untrained adult free"* ("NoCrew") — indistinguishable before, obvious now.

One nuance the live run exposed: `Waiting` **with a detail** means the work is
already in flight (a circuit crew mid-walk, a breeding pair still travelling),
not that the order is idle. The client reads a detail-bearing `Waiting` as "En
route"/"Working" rather than "Resting", or a busy order reads as an idle one.

### Authoring is five sentences, not one form

The order record has Subject + Trigger + Program + Recipe + Crew + Selector +
Priority. Exposed generically that is a database form — the IDE failure mode,
and the opposite of "the machine is the game". The client instead offers five
guided flows, each a sentence with map-click blanks (Supply Line, Breed,
Train, Staff, Circuit), and **derives the trigger from the sentence** — so
there is no predicate editor for the common cases. Full DNF authoring is
deferred; it likely wants a different surface entirely.

Each flow mirrors the server's *structural* validation (subject kind, crew
mode, trainer building per `RoleTrainerCatalog`) so the UI cannot offer a
doomed order. It deliberately does **not** mirror viability: an order pointed
at an empty farm is legal and simply waits, matching the server's stance.

Two consequences worth recording:

- **Priority is now a primary verb**, not a config field. Once contention is
  the normal resting state, priority is how the player resolves it, so it
  lives on every dashboard row. But `SetOrderIntent` always mints a fresh id
  (`world.NextOrderId++`) — there is **no in-place edit** — so a re-prioritise
  is Clear + Set: the order returns with a new number and its crew is released
  for one think. The deferred "in-place edit" item now has teeth.
- **The machine is drawn on the map.** Each dashboard row has a visibility
  toggle, and any number can be on at once: supply lines draw source →
  destination, circuits draw a closed polyline through their stops,
  Breed/Staff/Train draw a pad on the building they maintain, all tinted to
  match their row. The overlay deliberately survives the zoom-band switch —
  seeing the whole logistics network at once is what the zoomed-out bands are
  for.

### An anchor must be released on EVERY exit path

Found by the first real play session, and the most expensive bug of the port:
a named supply line whose hauler never moved, reporting its idle crew as
busy forever.

`HaulPlan` is the on-unit anchor meaning "this body is mid-haul", and every
consumer that asks whether a unit is free reads it — deliberately, because a
marching hauler reads `Activity.Idle` between steps, so anchors are the truth
and the activity flag is not. That makes clearing the anchor on every exit
path load-bearing. `HaulPickupEvent`'s *destination*-leg failures (no route,
dock razed) already failed clean; its *source*-leg failures did not. So a
hauler that walked to a farm whose buffer had emptied went Idle still holding
a dead anchor and was **bricked permanently** — invisible to every selector,
and eternally "busy" to the order that named it. One empty pickup retired the
unit and wedged the line.

Arriving at a dry source is ordinary (a farm can empty while the hauler walks
over), which is exactly why it must never be terminal. Pinned by
`HaulFailCleanTests`.

Two lessons worth carrying:

- **Fail clean everywhere, not on the paths you happened to harden.** The
  discipline existed in the same file, one screen below, and had simply never
  been back-applied to the earlier branch.
- **A status label can hide the bug it describes.** The first fix here was to
  the driver's *reporting* — a busy-but-working crew was mislabelled NoCrew.
  That was a real defect and worth fixing, but it also renamed the symptom of
  a deeper one, and briefly made a wedged order look healthy. Where a status
  is derived, check the derivation against the world before trusting it.

The driver also now refuses to dispatch toward a source that holds nothing
(the pre-check `RunRoutine`'s Load stop already made): otherwise a caravan
paces to an empty farm and back forever. And it skips orders belonging to a
DEFEATED player — every intent a defeated player submits is rejected, so a
driver that kept thinking for one submitted rejected work forever, including
the housekeeping that would have reaped its own claims.

### Presence in world.Units IS liveness — DeathTick is not a tombstone

The worst bug of the pass, found only by playing: **the entire substrate was
inert on the real server** — no named crew ever dispatched, no selector ever
pulled, every role/population count read zero — while every one of 900+ tests
passed.

`Unit.DeathTick` is the unit's *pre-rolled old-age death date*, set at genesis
on **every** unit the server spawns (`Population.ScheduleLifespan`). Actual
death — age, combat, starvation — **removes the unit from `world.Units`**.
So presence in the table is the one true liveness test, and `DeathTick` must
never be read as "dead". The substrate read it as "dead" in five places
(named-crew scan, both selector paths, role count, population count) and in
`ClaimLedger.IsDormant`. Hand-built test worlds use the `(GameWorld, seed)`
ctor, which skips the lifespan roll, so `DeathTick` was null everywhere in
tests and set everywhere in production — a **fixture/production divergence
that made the test suite blind to a total outage**.

Fixes: all six sites now treat presence as liveness. `SubstrateLifespanTests`
rebuilds the genesis condition (every unit carries a future `DeathTick`) and
pins each consumer; `ClaimsLedgerTests` had pinned the *wrong* semantic and
was corrected. Live-verified: a named supply line on a genesis crew fired,
walked, and picked up on a real server.

The transferable rule: **when a fixture hand-builds world state, every field
the production path sets and the fixture doesn't is a place where tests
cannot see a bug.** The keystone lab should eventually build its colony
through the spec-aware ctor for exactly this reason.

### The journal vocabulary is the dashboard — no state may be inferred

Second lesson from the same session: the client rendered any detail-bearing
`Waiting` as "Working" (to make "crew is on the trip" read as progress), so
when the empty-source pre-check landed — also reporting `Waiting` with a
detail — a supply line pointed at an empty farm displayed **"Working"** while
its hauler stood idle. A missing state cannot be patched around in
presentation. `JournalOutcome` gained **`InFlight`** (work dispatched earlier
is still underway: crew mid-trip, pair travelling, circuit marching) and the
empty-source case became **`Blocked`** (trigger met, the world can't serve
it — news, but never burns retry budget). The client now maps outcomes
strictly 1:1 and never reads the detail string.

Still deferred, unchanged: the unlock progression (explicitly out of scope for
this pass — the user wants to play the substrate as built), blueprints,
caps-as-economics, the map-native order editor, the morning report (whose
backbone is now on the wire), and the tactical war layer.

## References

- `docs/automation-as-core-game.md` — the vision and laws this implements.
- `docs/automation-layers.md` — the trust boundary retained here; its order
  vocabulary is superseded by this doc.
- `docs/m18-automation-engine-spec.md` — the outgoing model (historical).
- `docs/persistence-model.md`, `docs/architecture.md` §2.4/§3.3 — the
  determinism and recovery contracts every layer above obeys.

## Update 2026-09-17 — the production client speaks the substrate

The v2 production client (`Art Of War(prod)`) gained the whole automation
surface in one pass; the user's brief was "I can't play without the full set of
automations", so this is form-first, with the "do it once, then keep doing
this" offers deferred until progression exists server-side (the user: gating
"is not a client thing").

**Wire.** `Aow.Wire/OrderDto.cs` mirrors the server's projection field-for-
field (camelCase; `trigger` is the clause array, `selector` an object) and
`OrderVocabulary` carries the append-only enums, the caps, and the role→trainer
table. `Aow.Wire/OrderPayloads.cs` mirrors the Core record PascalCase.
`Aow.Net/OrderIntents.cs` derives each recipe's trigger from its sentence
(Supply line, Staff, Train, Breed, Circuit/Patrol) plus `FromDto` for
re-issuing an existing order at a new priority. Live-verified against a
scratch server: install + claim (named circuit with posture), install of a
pull supply line, a structural rejection surfacing as a notice ("Builder trains
at a School, not a Castle"), and Clear.

**Surfaces.** One dock page, *Machine* (K): the list worst-first (Stopped,
Short of hands, Blocked, Starting, Working, Resting) with priority arrows,
Change, Clear, Go and a per-order map toggle; and the form on the same page —
five tabs, one sentence each, every blank defaulted so a castle food line is
two clicks (`Hud/OrderForm.cs`). Buildings come from a nearest-first list of
your own or a borrowed map click (`PlayerController.TilePick`), numbers are
steppers, there is no free text and no predicate editor. Change = Clear + Set,
labelled as a "re-forming". The machine is drawn on the land
(`Automation/OrderOverlay.cs`, created at runtime, no scene wiring) and on both
maps (`MapPainter` threads), tinted by STATE not identity. A building's context
card lists the orders that touch it — the warehouse is the interface.

**Rules carried over from the debug client, still contract:** state strictly
1:1 with `lastOutcome` (never inferred from the detail string); a detail-
bearing InFlight reads as "En route"; form mirrors structural validity only;
priority is a primary verb.

Deferred: the in-place offer after a manual haul/train/assign, blueprints, the
Reinforce recipe (blocked on a sim verb), conscription, DNF authoring.

## Update 2026-09-19 — a pull line holds only what is walking for it

Found in play: a pulled castle supply line was holding four haulers while
the house line beside it reported "no free hauler in reach" with two of
those haulers standing idle. The driver borrowed a fresh body every think
its held hauler was mid-trip, and released nothing while the trigger stayed
met (a Blocked line at a dry source released nothing either). One line
quietly benched the whole pool, and every other selector saw an empty
world — claimed is not dormant.

The tempting fix — one borrowed hand per line — failed the keystone lab
outright: the fat colony could no longer out-breed the lean one, because a
long haul genuinely needs several bodies in flight to keep a larder above
its floor. Throughput scaling with trip length is load-bearing.

The rule now: **a pull line's held set is the bodies in flight plus the one
it dispatches this think.** Every other held hand that is idle and empty is
returned to the pool that think, a Blocked line returns all its idle hands,
and a line borrows from the pool only when every hand it holds is mid-trip
AND it is under `AutomationConfig.MaxPulledHands` (default 6, a driver knob,
not sim state — 3 starved the keystone lab's granary artery; 6 carries it). Returned hands are re-borrowable next think, by this order
or by a hungrier one earlier in the pass — which is exactly the contention
the priority ladder is for. Pinned by `SubstrateCrewStatusTests`
(`PullLine_HoldsOnlyHandsInFlight_NeverBenchesThePool`,
`PullLine_ReturnsIdleHands_WhenTheSourceRunsDry`).

Two things this leaves open: `MaxPulledHands` is a global knob where a
per-order "up to N haulers" field would let the player size a line (an
`Order` field, so a snapshot and wire change — deferred with the in-place
edit item); and the borrow-and-return doc above still describes the old
"keeps its hand while it has work" policy for the single-hand case, which
remains true — the change is only that a line never keeps a *second* idle
hand.

A finding from sizing the cap, recorded so nobody re-derives it: **the
keystone lab colony was already extinct before this change.** On a clean
checkout of the prior commit the "fat" colony ends day 160 with 45
starvation deaths and no one alive; the lab asserts only `births > 0` and
`fat > lean`, so it has been passing on a dying colony for some time. With
this change at cap 6 the same colony ends alive (15 births, 29 starved,
population 15). Re-pinning the lab to its original zero-starvation claim is
open work, and the numbers above are the baseline to beat.
