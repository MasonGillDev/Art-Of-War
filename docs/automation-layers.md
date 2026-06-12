# Automation Layers — Standing Orders + Server-Side Driver

**Status: designed, not built.** Decisions below are locked (2026-06-11); the
milestone spec will phase the build.

## The decision

Player automation ("Factorio-style logistics" — declarative standing orders,
never user code) is split across the existing trust boundary:

1. **Orders are inert Sim.Core state.** A `StandingOrder` is durable sim data,
   installed/removed by two new intents, serialized into snapshots. Core never
   evaluates an order.
2. **Evaluation is a server-side `AutomationDriver`** (Sim.Server), shaped
   exactly like `BanditDriver` (M16) and the M17 AI players: pure view-reads
   in, ordinary durable intents out, ephemeral brain.
3. **Per-player order count is capped** (config constant; later a balance
   knob unlock structures can raise).
4. **Supply lines claim specific haulers.** No pooled job market in v1 — an
   order names the units that serve it.

## Why

### Why orders live in Core

Standing orders are *player truth*. They must survive server restarts and
crashes, and they must replay. The snapshot/intent-log machinery (M4) already
provides exactly that for sim state — putting orders anywhere else means
building a second persistence system with its own recovery semantics. Orders
as a sparse `GameWorld.StandingOrders` dictionary get snapshot round-trip,
crash recovery, and replay for free, at the cost of one dictionary and two
intents (`SetStandingOrderIntent` / `ClearStandingOrderIntent`) that mutate
nothing else.

Core gets **no evaluation**: no new event types, no anchors, no
`RegenerateQueue` cases. Per the M14 lesson — zero new anchors → recovery-clean
for free.

### Why evaluation lives in the server (the alternative that lost)

The losing alternative was in-Core evaluation: a data DSL advanced by sim
events (the generalized-`HaulPlan` model). It was the original recommendation
and it is genuinely defensible — exact-tick reactions, in-sim `(At, Seq)`
fairness between contending automations. It lost because M16 changed the
facts:

- `BanditDriver` proved the driver shape end-to-end. Its headline test
  (`BanditDriverTests.ReplayFromIntentLog_HashesMatch`) demonstrates that an
  out-of-sim brain preserves determinism — the intent log records the brain's
  *outputs*, so replay reproduces the world without the brain. M17 (AI
  players) validated the same shape a second time, including the arbitration
  discipline.
- In-Core evaluation grows the determinism surface: every order kind becomes
  event types + anchors + regeneration cases + audit entries + fencing
  interactions with `AssignmentEpoch`. The driver grows it by zero.
- Fog-fairness is structural in the driver: it reads the player's
  fog-filtered view, so automation *cannot* react to anything its owner
  couldn't see. In-Core evaluation would have to enforce that by API
  discipline, forever.
- The costs accepted: reactions happen at think-cadence rather than the exact
  sim tick, and cross-player contention is arbitrated by driver submission
  order rather than in-sim Seq fairness. At this game's time scale
  (docs/time-and-scale.md — 1 tile = 1 km march), think-cadence latency is
  invisible.

Free-form scripting (Lua/JS/expressions) was ruled out at the start: arbitrary
user code can never run in Core (floats, unbounded loops, nondeterminism), and
it isn't the product goal — the consumable surface is dropdowns and sliders.

### Why claimed haulers (not pooled)

A pooled model ("any idle hauler serves any unmet supply line") requires a
global job-market arbitration rule, and every tweak to it re-litigates
fairness. Claimed haulers ("these units serve this line") avoid the problem
entirely, are more physical (consistent with docs/extraction-model.md), and
are more legible to the player — you can *see* which caravan belongs to which
route. Pooling can be added later as a separate order kind without disturbing
claimed lines.

### Why capped

Orders are sim state and driver work; an unbounded count is a snapshot-bloat
and think-time hazard. A cap is also a natural lever for the planned unlock
structures (below) to raise.

## The layers

| Layer | Where | What |
|---|---|---|
| A — Order state | `Sim.Core/Automation/` | `StandingOrder` records in sparse `GameWorld.StandingOrders` (by order id, canonical iteration); `SetStandingOrderIntent` / `ClearStandingOrderIntent`; append-only `OrderKind` + `WaitCondition` enums; integer params only |
| B — Evaluation | `Sim.Server/Automation/AutomationDriver` | Per think-tick, per player: read fog-filtered view + that player's orders, submit ordinary intents (`HaulIntent`, `MoveIntent`, `TrainUnitIntent`, `CraftEquipmentIntent`, `UnloadCargoIntent`). `Think()` runs on the sim thread inside the GameHost clock-loop lock, self-gated by a think period — same contract as `BanditDriver.Think` |
| C — Wire | `Sim.Server/Wire` | Player's own orders + live status in `ViewDto`; order intents through the existing `POST /intent` envelope |
| D — UI | Unity client | Dropdown/slider order editors. "Consumable by the average player" is solved here, not in the sim |

## Order vocabulary (v1)

1. **SupplyLine** (subject: structure) — "keep structure D stocked to ≥ N of
   resource R, drawing from source S, served by claimed haulers [ids]."
   Driver: when the view shows D below threshold and a claimed hauler is
   available, dispatch a haul.
2. **Route** (subject: unit) — ordered stop-list of
   `(TileCoord, WaitCondition, threshold)`. `WaitCondition`: `CargoFull`,
   `CargoEmpty`, `Ticks`, `StoreAtLeast`, `StoreBelow`. The train-schedule
   primitive; driver advances stop-to-stop.
3. **StandingProduction** (subject: structure) — "keep training/crafting X
   while condition holds" (e.g. train Soldier while castle Food ≥ N). Driver
   submits the train/craft intent when the condition reads true.

Deliberately absent: enable/disable gates on extractors. The economy already
self-regulates physically (buffer-full / no-workers / biome-mismatch dormancy
in `ProductionTickEvent`); "pause the farm" is expressible as unassigning
workers. If an in-sim production gate ever proves necessary, it is the one
case that would justify revisiting Core evaluation — as its own decision.

## Driver disciplines (inherited from M16/M17 — do not relearn these)

- A marching unit reads `Activity.Idle`. Guard on `NextArrivalTick` /
  `PathRemaining`, never on `Activity`, or the driver re-orders every think.
- Replay interleaves chronologically: `Run(until: at)` then submit, in Seq
  order. Front-loading intents reorders same-tick execution and diverges.
- Arbitration must be canonically ordered (order id, then unit id, then
  `(y, x)`) so two runs of the driver over the same view decide identically.
- Stockless / invalid subjects must fail the order's step cleanly, not wedge
  the order's claimed units forever.

## Acceptance tests

- **Headline:** run a scenario with the AutomationDriver live; replay the
  intent log with the driver disabled; `Snapshot.Hash` matches. (Copy
  `BanditDriverTests.ReplayFromIntentLog_HashesMatch`.)
- Snapshot round-trip for `GameWorld.StandingOrders` (every field).
- `SetStandingOrderIntent` resolution: ownership, subject exists, cap
  enforced, claimed units owned and not grouped/embarked.
- Fog-fairness: an order whose source the player has never seen produces no
  intents until the source is scouted.
- Pitfall regression: a route unit mid-march is not re-ordered on the next
  think.

## Future expansion

- **Unlock gating (designed, separate spec):** automation intents gated by
  per-branch unlock structures — hauling (SupplyLine + Route), production
  (StandingProduction), military (patrol / auto-engage standing orders,
  deferred — wants M5 group primitives). Gate = one resolution-time
  precondition mapping `OrderKind` → required `StructureKind`. Branch
  structures are expensive and visible (fog telegraph → coalition politics);
  recommended-but-not-locked: orders of a branch suspend while the player
  lacks a functioning structure of that branch.
- Pooled logistics as a new `OrderKind` alongside claimed lines.
- Cap raises tied to unlock-structure tiers.
- Order status enrichment in `ViewDto` (per-order progress, last rejection).
- `OrderKind` / `WaitCondition` are append-only — new kinds slot in without
  disturbing serialized orders.

## Update 2026-06-11 — engine spec'd (M18)

The engine spec is `docs/m18-automation-engine-spec.md`. Two refinements over
this doc's first draft:

- **Generic engine, typed templates.** The engine knows only atoms
  (`ConditionKind`, `ActionKind`) and step sequencing; SupplyLine / Route /
  StandingProduction are data templates compiled to steps. `OrderKind`
  survives as a serialized tag for UI + future branch gating only.
- **Cursors are durable, brains stay ephemeral.** Per-order progress is Core
  state mutated by a server-internal `AdvanceOrderCursorIntent` (M16
  wire-guard precedent) — a player's mid-route caravan resumes exactly after
  a restart, unlike bandit parties where forgetting is flavorful.

## Update 2026-06-12 — Layer D shipped (Unity client)

The last unbuilt layer. The client (Unity repo) gained:

- **Wire mirrors** (`Net/Wire.cs`): `WireOrder` / `WireOrderStep` /
  `WireOrderCondition` + the automation enums; `WirePlayerView.orders`.
  Verified field-for-field against the live server's projection.
- **Intent payloads + template builders** (`Net/Intents.cs`):
  `SetStandingOrderIntentPayload` (PascalCase, mirrors the Core intent's
  property names exactly) and `IntentFactory.SetSupplyLine` / `SetRoute` /
  `SetStandingCraft` / `ClearOrder` — the client-side twins of the test-side
  `AutomationTemplates` reference implementations, as planned in M18 Phase E.
- **`OrderMode` (hotkey O, `Game/Input/OrderMode.cs`)**: a dashboard panel
  listing own orders with live cursor status (step k/n, run/wait, done vs
  STOPPED — distinguished by `retryCount`, which Once-completion resets and
  auto-disable leaves exhausted) and per-order Clear; plus three guided
  click-flows: Supply Line (hauler → source → dest, resource palette +
  keep-below threshold stepper), Route (unit → stops, each with an
  Always/CargoFull/CargoEmpty departure gate), Standing Craft (item →
  barracks, gated `StoreAtLeast` on the catalog costs so an under-stocked
  barracks waits instead of burning its retry budget). Caps (16/16) are
  client-side mirrors of `AutomationConstants` — craft-cost-label convention.

No server change was needed: Phase F's wire + the notice pipeline (rejection
toasts, auto-disable notices) already carry everything the UI consumes.
End-to-end verified against the running host with byte-identical
JsonUtility-shaped payloads: install → project → resolution-reject → clear.

Still open from Future expansion: in-place order editing (today: Clear +
re-create), per-order progress/last-rejection enrichment in `ViewDto`,
unlock gating, military atoms, pooled haulers.

## References

- `docs/architecture.md` §2.4 (re-arm), §3.3 (resolution-time validation)
- `docs/bandits.md` + `src/Sim.Server/Bandits/BanditDriver.cs` — the proto-driver
- `docs/ai-players.md` — second driver, arbitration lessons
- `docs/persistence-model.md` — intents-as-truth, why driver outputs replay
- `docs/extraction-model.md` — everything-physical, why claimed haulers fit
