# Goal-Shaped Intents — the sim executes, the player decides

**Status:** locked; built as M30 (ahead of dynasty, battles, trade, chronicler).
**Depends on:** `MoveIntent`, the `HaulPlan` arrival-dispatch pattern, epoch
fencing, the One Stop Rule, M8 breeding economics.

## The decision

**Every intent expresses a goal; the sim handles every mechanical step between
now and that goal.** Travel and precondition-waits are execution, and execution
is the sim's job. An intent is accepted, the unit walks, the unit waits, the
goal completes — or it dissolves cleanly and says so.

Concretely: `AssignWorkers`, `AssignBuilders` and `BeginBreeding` stop
requiring "the unit is already standing there", and construction sites stop
requiring "the materials happened to arrive before the builder".

## Why

The current intent layer expresses *steps*. Because `AssignWorkersIntent`
requires `unit.Position == StructureTile`
(src/Sim.Core/Logistics/AssignWorkersIntent.cs:47), the player's actual goal —
"that person works that farm" — decomposes into a move, a wait, and a second
intent. Each wait is an **appointment**: a mental timer the player carries
("in four minutes, come back and assign").

The early-game hump is a stack of appointments. It is not the clicks that hurt;
it is the appointments attached to them. Slow-and-fire-and-forget is the
intended feel. Slow-and-check-back is a chore simulator — the worst possible
first impression for a game whose pitch is "step away, it runs."

### Why now, before the remaining systems

Dynasty, the battle layer, trade and the chronicler all add intents. This
pattern is their template; building them step-shaped manufactures retrofit
work. Separately, the road-to-playable stopwatch test is contaminated if run
against step-shaped intents — it would measure friction that is already
diagnosed and condemned.

### The boundary that protects the automation tier

- **Goal-completion (baseline, free, day one):** the sim finishes *executing* a
  decision the player already made. Waiting on a precondition is execution, not
  judgment.
- **Automation (unlocked, earned, progression):** the sim makes *decisions* on
  the player's behalf — standing orders, patrols, supply networks, perpetual
  cycles.

**Test:** one decision, one intent, fully executed — including travel and
precondition-waits. No chaining of new decisions. "Assign him when he arrives"
is a goal. "Build the farm, then a house, then start breeding" is a plan, and
plans are the automation tier.

The refactor's machinery makes chaining and standing orders easy to add. That
is exactly why the scope discipline is written down: convert what exists, hold
the line, get out.

### Availability is a precondition, never a rejection

No idle builder, no free worker, not enough food → **accept, wait, and stall
visibly**. Nothing in that category is impossible-forever. Rejection is
reserved for malformed intents: not your unit, wrong role, illegal tile, a
bound unit that is grouped. The client warns at authoring time ("no idle
builders — this will wait"): advise, accept, stall visibly, never silently.

### No silent substitution

A designated worker or builder who dies mid-plan dissolves *that sub-goal* and
announces it; the structure completes unmanned and tagged. The player re-fires.
Silent substitution *is* the automation tier's auto-replacement feature —
running it at baseline kills the unlock.

Likewise **the sim never picks participants.** "Use any available builder"
convenience lives client-side: the UI picks (nearest idle) and binds explicit
unit ids into the intent. Sugar in the client, decisions with the player,
execution in the sim — and the replay log stays legible.

### Breeding: the precondition stress-test

`BeginBreedingIntent` today demands both parents standing on the house tile,
Idle, fertile, with the food already delivered
(src/Sim.Core/Population/BeginBreedingIntent.cs) — five appointments in one
intent. Goal-shaped: both parents walk from anywhere, the first to arrive
waits, and **conception fires the moment food ≥ `BirthFoodCost`**.

Rejected alternatives:

- **Fail on arrival** — reborn appointment problem; the player must predict
  future food levels.
- **Conceive on credit** — breaks M8's charge-at-start economics and
  introduces debt states.

Waiting is visible opportunity cost: the pair is anchored at the house, not
working, not hauling. The player chose this.

The **fertility gate evaluates at the conception moment** — a deliberate
redefinition of M8's "checked once at breeding start", where start now means
conception rather than intent-firing. A parent who ages out while waiting makes
the goal impossible, so it **dissolves** rather than stalling forever.

**One child per intent.** Conceive → gestate → birth → goal complete → parents
released. "Breed whenever food allows" is the automated breeding cycle:
automation tier, unlocked. One-shot baseline, perpetual progression.

**Royal application** (this settles the road-to-playable open question): royal
conception is goal-shaped — fire once, the sim sequences travel, wait and
conception, no appointments — but **never standing**. Each royal child stays a
deliberate decision every time. The dynasty is an anchor of judgment, not of
chores.

## The visibility contract

Goal-shaped intents move work off the player's memory and into the sim. The
contract: **the sim must report on that work, or appointment-anxiety is merely
traded for silent-failure anxiety.**

- Every pending goal is inspectable — `en route` / `waiting: food` /
  `waiting: builder` on the relevant unit or structure.
- Every dissolution is announced (later, chronicler material — "the match was
  broken; the harvest never came").
- Stalled goals (waiting on something the player controls) read differently
  from in-progress goals.

This lands in the C2/C2.5 client layer and is non-optional: it is the other
half of the feature.

## Mechanics

All of it is existing machinery:

- Compound intent = `MoveIntent` + arrival-triggered follow-on — the `HaulPlan`
  shape, generalized into a `Unit.Goal` anchor dispatched by
  `MoveArrivalEvent`.
- Precondition-waits = a pending goal registered on the target entity;
  existing state-change events (deliveries, arrivals) trigger the completion
  check. No polling.
- Invalidation via epoch fencing; death via the One Stop Rule; dissolution is
  always clean (units → Idle, goal → announced).
- Deterministic and replay-safe by construction: goals are intents in the log,
  completions are events.

## What this predicts for the first hour

The stopwatch question decomposes into (a) bootstrap wall-time and (b) the
fraction of that time that is decision-full rather than appointment-full. This
targets (b) → ~zero without shortening (a). The prediction to verify in
playtest: the same bootstrap duration becomes the city-builder pleasure loop —
place, plan, watch it come alive, place next — and the safe-to-step-away moment
arrives as a felt achievement rather than a release from tedium. If the opening
still isn't fun afterwards, the remaining problem is pacing and content, not
friction — a different fix.

## Future expansion

- **Chaining and standing orders** are one field away once goals exist; they
  belong to the automation tier and stay locked at baseline.
- **Auto-replacement** of a dead participant is the automation-tier upgrade of
  §"no silent substitution".
- **Demand-driven haulage** (a site pulling its own materials without a
  designated hauler) is a genuinely new system — see the M30 spec's
  correction — and is the natural next milestone after this one.
- **New systems inherit the shape for free:** dynasty (royal conception),
  battles (march-then-fight), trade (travel-then-exchange) and the chronicler
  (dissolution announcements are its raw material).

## Test implications

Twin-run determinism across all compound intents; arrival-dispatch fencing at
every stage (pre-departure, en route, waiting, mid-execution); the breeding
matrix (food present, food late, partner dies waiting, ages out waiting, house
destroyed waiting); conception fires exactly once when multiple deliveries land
in the same tick (order-independence); construction condition-conjunction
(materials-then-builder and builder-then-materials must converge identically);
goal dissolution never leaves orphaned anchors (snapshot/restore round-trip
mid-wait).

## Update 2026-08-20 — built as M30

Built; see `docs/m30-goal-shaped-intents-spec.md` for the build spec, the phase
breakdown, and the audit results. Three corrections to the model above:

- **§3.2 was already true.** A construction site has started itself on
  conditions-met since the beginning, from BOTH sides of the conjunction
  (`AssignBuildersIntent` and `CargoTransfer`). Only the builder-travel half
  was missing.
- **§3.3's materials claim was wrong.** There is no demand-driven haulage —
  `HaulIntent` is one named hauler, one source, one destination. Building it
  here would also cross the goal/automation boundary (which hauler, from which
  stockpile, is judgment). `BuildIntent` therefore binds builder and
  worker-to-man only; materials arrive as they always did and the site stalls
  visibly as `waiting: materials`. Demand-driven haulage is the natural next
  milestone.
- **Waiting needed its own activity.** Every solo intent gates on `Idle`, so a
  pair waiting for food would have been silently retaskable. `Activity.Waiting`
  is non-Idle, which makes every existing intent refuse it without any of them
  being touched.

The §3.5 sweep is partially deferred: the "target is whatever I am standing on"
intents (`TrainUnit`, `EquipUnit`, `Craft`, `LootCache`, `Load`/`UnloadCargo`,
`Embark`/`Disembark`) need an explicit target parameter before they can be
goal-shaped at all. Named as a follow-up in the spec, not dropped.
