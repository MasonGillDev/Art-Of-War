# M18 Spec — Automation Engine (Standing Orders + Driver)

> **Shipped 2026-06-11.** Headline green
> (`AutomationHeadlineTests.Headline_ReplayFromIntentLog_HashesMatch`); host
> smoke: `dotnet run --project src/Sim.Host -- --automation`. One deviation
> from the text below: the Core-side caps live in `AutomationConstants`
> (RoadConstants-style statics, enforced at Set time only), not serialized
> config — retuning a cap never invalidates snapshots.

> Companion to `docs/automation-layers.md` (the placement decision doc). This
> spec covers the ENGINE only: the atom vocabularies, the order model, the
> driver, and the contracts. Unlock structures, military orders, and client UI
> are explicitly out of scope.

## What we're adding

A generic standing-order engine in two halves:

- **Sim.Core/Automation/** — the durable order model: orders, conditions,
  actions, cursors, and the intents that mutate them. Core stores and
  validates; it never evaluates.
- **Sim.Server/Automation/** — the `AutomationDriver`: evaluates orders
  against each player's fog-filtered view every think period and acts by
  submitting ordinary intents (the `BanditDriver` contract).

The design principle, locked by the user: **the engine knows only atoms and
sequencing.** "SupplyLine", "Route", "StandingProduction" are *templates* —
data shapes the client (or tests) compile into steps. Adding a future order
variety means new template data or a new atom enum value + one evaluator
case — never new engine machinery.

## The atom vocabularies

### Conditions (`ConditionKind` — append-only, serialized)

A `ConditionSpec` is a pure predicate over what the owning player can SEE:

| Kind | Params | Reads |
|---|---|---|
| `Always` | — | — |
| `StoreAtLeast` / `StoreBelow` | structure tile, `Resource`, threshold | structure holdings (or extractor buffer) |
| `CargoFull` / `CargoEmpty` | unit id | unit cargo vs `CargoCapacity` |
| `UnitAtTile` | unit id, tile | unit position |
| `ElapsedTicks` | threshold | `now - cursor.StepEnteredTick` |

All params are integers/enums/coords. Steps carry `Conditions:
List<ConditionSpec>` with **AND** semantics (empty = always) — v1 UIs emit
zero or one condition, but the list shape is in the schema from day one so
compound conditions later are a pure additive change. OR is deferred (a future
`Any` group kind, additive).

**Fog rule (contract):** the evaluator may only read state visible to the
order's owner (live-visible via `View`, or owner-owned entities). A condition
on an unseen subject evaluates to *unknown* → the step does not advance. This
is what makes automation structurally incapable of maphacking.

### Actions (`ActionKind` — append-only, serialized)

An `ActionSpec` maps **1:1 onto an existing intent** — an action atom is a
parameter bag, never new sim semantics:

| Kind | Emits |
|---|---|
| `MoveTo` | `MoveIntent` |
| `HaulTrip` | `HaulIntent` |
| `LoadCargo` | `LoadCargoIntent` |
| `UnloadCargo` | `UnloadCargoIntent` |
| `Train` | `TrainUnitIntent` |
| `Craft` | `CraftEquipmentIntent` |
| `AssignWorkers` / `UnassignWorkers` | the matching intents |

**Rule for growth:** a new gameplay verb gets its own intent (own milestone,
own validation) FIRST; automation then gains the atom by adding an enum value
and one `IntentFactory` case. The military branch (patrol, auto-engage) waits
on this rule — its verbs don't exist as intents yet.

### Steps and orders

```
OrderStep     = { Conditions: List<ConditionSpec>,   // wait-until, AND
                  Action: ActionSpec }
StandingOrder = { OrderId, OwnerId, Enabled,
                  Kind: OrderKind,                   // template tag — drives UI + future branch gating
                  ClaimedUnits: List<int>,
                  Steps: List<OrderStep>,
                  Loop: LoopMode }                   // Once | Loop (append-only)
```

Stored in sparse `GameWorld.StandingOrders: SortedDictionary<int, StandingOrder>`
(canonical by-id iteration), serialized in snapshots. `OrderKind` is engine-
inert (the runner only reads Steps) but is a real serialized field: it's what
the future per-branch unlock gate keys on, and what the client renders.

## Locked decisions

1. **Durable cursor via a server-internal intent.** Per-order progress
   (`CurrentStep`, `StepEnteredTick`, dispatch fence) is Core state on the
   order, mutated ONLY by `AdvanceOrderCursorIntent` — a server-internal
   intent the driver submits, wire-rejected in `GameHost.SubmitEnvelopeJson`
   exactly like the bandit spawn/despawn intents (M16 precedent, recorded in
   `docs/intent-authorization.md`). Why not ephemeral (the bandit choice):
   bandit brains forgetting mid-raid is flavorful; a player's caravan
   forgetting which stop it was on after a server restart is a bug report.
   Durable-via-intent means crash recovery resumes mid-route exactly, replay
   reproduces cursors, and snapshot round-trip covers them. Log chattiness is
   bounded by step advances — activity-bounded, like everything else.
2. **Bounded retry, then auto-disable.** When an emitted intent rejects (or a
   step makes no progress for `MaxStepRetries` consecutive thinks), the driver
   disables the order (`Enabled = false` via cursor intent) and a notice is
   surfaced through the existing rejection-notice channel. This is the
   anti-wedge rule — M16's "stockless structures wedge parties forever"
   pitfall, closed structurally. No infinite silent retries, no silent drops.
3. **Claims are exclusive; manual control wins.** A unit may be claimed by at
   most one order (validated at `SetStandingOrderIntent.Resolve`). Claiming
   does not lock the unit: the player can always command it manually, and the
   driver never fights — it only acts on a claimed unit that is idle **by
   anchors** (`PathRemaining == null`, `NextArrivalTick == null`, `HaulPlan ==
   null`, `Activity == Idle`). A manually-busied unit simply stalls its step
   until free.
4. **Cap enforced at Set-time.** `AutomationConfig.MaxOrdersPerPlayer` —
   config constant, balance knob. Per the project convention, tests derive
   from the config value, never hard-code it.
5. **Driver contract inherited from M16/M17 verbatim:** `Think(sim, now)` on
   the sim thread inside the GameHost clock-loop lock, self-gated by
   `ThinkPeriodTicks`; players iterated in ascending id, orders in ascending
   id, claimed units in ascending id — canonical arbitration so two runs over
   the same state decide identically. Replay interleaves chronologically
   (`Run(until: at)` then submit, Seq order).

## What's deferred (engine leaves the seam, builds nothing)

- **Unlock-structure gating** — one resolution-time precondition in
  `SetStandingOrderIntent` mapping `OrderKind` → required `StructureKind`,
  added when the endgame structures milestone lands. The `Kind` field exists
  now so the gate is a one-line change.
- **Military atoms** (patrol, auto-engage) — blocked on the verbs existing as
  intents.
- **OR / compound condition groups; pooled haulers; cap tiers; client UI.**
- **Order editing** — v1 is Set (whole order) / Clear. In-place step edits are
  a future intent.

## Headline test

`AutomationDriverTests.Headline_ReplayFromIntentLog_HashesMatch` — run a
scenario with the driver live (orders installed, supply line + route + standing
production all exercised); replay the captured intent log with the driver
disabled; `Snapshot.Hash` matches. Same shape as
`BanditDriverTests.ReplayFromIntentLog_HashesMatch`.

## Phases

- **A — Core order model.** `Sim.Core/Automation/`: `StandingOrder`,
  `OrderStep`, `ConditionSpec`, `ActionSpec`, enums, cursor fields;
  `GameWorld.StandingOrders`; `SetStandingOrderIntent` /
  `ClearStandingOrderIntent` with full resolution-time validation (ownership,
  cap, subject existence, claim exclusivity, claimed units owned + ungrouped +
  unembarked). Done: snapshot round-trip test over every field + validation
  rejection tests.
- **B — Cursor intent.** `AdvanceOrderCursorIntent` (server-internal), wire
  guard in `GameHost`, entry in `docs/intent-authorization.md`. Done: cursor
  round-trips; wire rejects it; replay including cursor intents reproduces
  cursor state.
- **C — Evaluator + factory.** `ConditionEvaluator` (pure; per-atom unit
  tests; fog tests: unseen subject ⇒ no advance) and `IntentFactory` (per-atom
  mapping tests). Done: every atom tested in isolation; 100×-no-mutation test
  on the evaluator.
- **D — Runner + driver.** `OrderRunner` (step sequencing, dispatch fencing,
  bounded-retry auto-disable) + `AutomationDriver` (think loop, canonical
  ordering, idle-by-anchors guard). Done: pitfall regressions — mid-march unit
  not re-ordered; rejecting step auto-disables with notice; loop-mode order
  cycles.
- **E — Templates + headline.** Test-side template builders (SupplyLine /
  Route / StandingProduction compiled to steps — these double as the reference
  implementations for the future client UI); headline replay test;
  `docs/determinism-audit.md` updated (orders + cursors: mutation sites are
  the three intents only).
- **F — Wire + smoke.** Orders + cursor status in `ViewDto` (owner-only);
  host smoke scenario: a SupplyLine keeps the castle stocked from a LumberCamp
  end-to-end while a Route unit loops a two-stop circuit, printed readably.

## Files

Created: `src/Sim.Core/Automation/{StandingOrder,ConditionSpec,ActionSpec,SetStandingOrderIntent,ClearStandingOrderIntent,AdvanceOrderCursorIntent,AutomationConfig}.cs`,
`src/Sim.Server/Automation/{AutomationDriver,OrderRunner,ConditionEvaluator,IntentFactory}.cs`,
`tests/Sim.Tests/{AutomationOrderTests,AutomationEvaluatorTests,AutomationDriverTests}.cs`.
Modified: `GameWorld` (orders dictionary), `Snapshot` (serialize orders/cursors),
`GameHost` (driver hook + wire guard), `ViewProjector`/`WireDtos` (owner-only
order views), `docs/{intent-authorization,determinism-audit}.md`.

## Verification

`dotnet build` / `dotnet test` green per phase (net10 SDK from `~/.dotnet`);
Phase F host smoke; determinism audit grep-verified.
