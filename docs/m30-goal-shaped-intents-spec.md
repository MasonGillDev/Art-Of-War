# M30 — Goal-Shaped Intents (build spec)

**Design doc:** `docs/goal-shaped-intents.md` (the *what* and the *why*).
This is the *how*.

---

## What the design doc assumed away

Three findings from reading the code before building. Two are small; one
changes the scope of §3.3.

### 1. "The site begins construction automatically" already exists

`ConstructionSite.ConditionsMet` + `StartOrResume`
(src/Sim.Core/World/Structure.cs:384) are already called from **both** sides of
the conjunction — the builder-arrives side (AssignBuildersIntent.cs:64) and the
materials-arrive side (CargoTransfer.cs:60). The condition-conjunction
convergence the design doc asks for is built and shipped.

What is missing from §3.2 is only the **travel** half: a builder must already
be standing on the site tile to be assigned. That is the same defect as §3.1,
and the same fix covers both.

### 2. There is no demand-driven haulage — §3.3's materials claim is wrong

The design doc says materials "flow via the site's registered demand through
existing back-pressure logistics (no hauler designated — routing haulage to
demand is existing system behavior)". **No such system exists.** `HaulIntent`
is one explicit trip with a named hauler, a named source and a named
destination (src/Sim.Core/Logistics/HaulIntent.cs:19). Nothing registers
demand; nothing routes to it.

Worse, building it here would **cross the boundary the doc exists to
protect**: choosing *which* hauler and *which* stockpile feeds a site is
judgment — the automation tier's job, and already the shape of the M18/pivot
order system.

**Resolution:** `BuildIntent` binds the builder and the worker-to-man;
materials remain player-hauled (or hauled by an existing standing order), and
the site stalls **visibly** as `waiting: materials`. This is consistent with
§3.3's own rule — availability is a precondition, not a rejection — and it
keeps the composite honest: every participant in a goal is named by the player.
Demand-driven haulage is written up as the natural next milestone.

### 3. Waiting units would be silently stolen

Every solo intent gates on `Activity.Idle`. A pair of parents standing at a
house waiting for food would be Idle, so `AssignWorkersIntent` or `HaulIntent`
could quietly retask them out from under a pending goal — the exact
silent-failure the visibility contract forbids.

**Resolution:** a new `Activity.Waiting` (append-only enum, value 5). Waiting
is a non-Idle activity, so every existing Idle gate rejects it *for free* — no
sweep through the intent catalog, no missed site. `MoveIntent` is the single
deliberate override: a new march is the player countermanding the goal, so it
clears the anchor and takes the body.

---

## The mechanism

### `Unit.Goal` — one more on-unit anchor

Third instance of the pattern `HaulPlan` established and `Pursuit` confirmed
(src/Sim.Core/World/HaulPlan.cs, src/Sim.Core/World/Pursuit.cs): durable state
on the unit from which the arrival handler derives what happens next, so that
(a) a snapshot captures "what happens next" as pure state and `RegenerateQueue`
rebuilds the queue from anchors alone, and (b) a driverless replay of the
intent log reproduces the whole compound intent from the one intent that
started it.

```
GoalKind : byte { AssignWorker = 1, AssignBuilder = 2, Breed = 3 }

GoalPlan {
    GoalKind  Kind
    TileCoord TargetTile      // the structure the goal is about
    int       PartnerUnitId   // Breed: the other parent. 0 otherwise.
}
```

`Unit.Goal` is mutually exclusive with `HaulPlan` and `Pursuit` in practice —
goal intents bind Idle units, and the intents that create the other two anchors
reject non-Idle units.

### Dispatch

`MoveArrivalEvent.DispatchOnFinalArrival` gains a `Goal` branch after the
`HaulPlan` branch (goals are errands: pursuit and scouting still win, per the
precedence already documented there). The branch calls
`GoalRules.OnArrival(sim, unit)`.

### Waiting and re-checks — no polling, no new event types for the food path

- **Arrival** → `GoalRules.OnArrival` tries to complete; on failure the unit
  takes `Activity.Waiting` at the target tile.
- **Food delivered to a house** → the existing deposit path
  (`CargoTransfer.Deposit`, already the site's `StartOrResume` trigger) also
  calls `GoalRules.OnHouseSupplied`.
- **Partner arrives** → the arriving parent's own `OnArrival` sees the other
  already waiting and conceives.

One new event type is unavoidable and justified: `GoalExpiryEvent`, scheduled
only for breed goals at the tick the *earlier* parent ages out of fertility.
Without it, "a goal that can never complete dissolves" is only true if
something else happens to poke it — a pair waiting on food that never comes
would stall forever, which the design doc explicitly forbids.

### Dissolution

`GoalRules.Dissolve(sim, unit, reason)`: clear `Goal`, clear any pending
registration on the target structure, `TrySetActivity(Idle)` (which bumps the
epoch and stales any in-flight arrival), and record the reason for the
visibility contract. Triggers:

| Trigger | Hook |
|---|---|
| Participant dies | `Population.OnUnitRemoved` (the One Stop Rule site) |
| Target structure destroyed/razed | structure-removal path |
| Parent ages out while waiting | `GoalExpiryEvent` |
| Player countermands | `MoveIntent.Resolve` |
| Precondition impossible on arrival (worker cap full, house occupied) | `GoalRules.OnArrival` |

### Partial acceptance (multi-id intents)

`AssignWorkersIntent` / `AssignBuildersIntent` take id **lists** and silently
skip ineligible ids (src/Sim.Core/Logistics/AssignWorkersIntent.cs:40). Goal
shaping keeps that per-id discipline exactly:

- present + eligible → assign now (unchanged path)
- elsewhere + eligible → **goal + walk**
- malformed (not owned, grouped, embarked, too young, dead) → skipped, as today
- nothing assigned and nothing dispatched → reject, as today

So a mixed list does the immediate work immediately and dispatches the rest —
no new rejection modes, wire shape unchanged.

---

## Phases

### Phase A — the anchor and the two assign goals
- `Activity.Waiting`; `GoalKind` / `GoalPlan`; `Unit.Goal`.
- `GoalRules` (new, `src/Sim.Core/Intents/GoalRules.cs`): `Begin`, `OnArrival`,
  `Dissolve`, `OnStructureRemoved`.
- `MoveArrivalEvent` dispatch branch; `MoveIntent` countermand.
- `AssignWorkersIntent` / `AssignBuildersIntent`: dispatch instead of skip.
- Death hook in `Population.OnUnitRemoved`.

**Prove:** unit walks and takes the post; arrival into a full worker cap
dissolves and idles; retask mid-walk (new MoveIntent) leaves no orphan anchor;
target razed mid-walk dissolves; twin-run determinism; snapshot round-trip
mid-walk.

### Phase B — breeding
- `House.PendingBreed` (the registration that makes a house *reserved* while
  its pair walks — without it, two breed intents could target one house).
- `BeginBreedingIntent`: validate malformed-only, bind both parents, dispatch.
- Conception attempt on: both-waiting, food delivered, and never otherwise.
- Fertility evaluated at conception; `GoalExpiryEvent` at the fertility
  deadline.

**Prove:** the full matrix — food present, food late, partner dies waiting,
ages out waiting, house destroyed waiting; conception fires exactly once when
two deliveries land in the same tick; parents released on birth; snapshot
round-trip mid-wait.

### Phase C — persistence and the wire
- Snapshot `FormatVersion` bump: `Unit.Goal`, `Activity.Waiting`,
  `House.PendingBreed`.
- `RegenerateQueue`: rebuild `GoalExpiryEvent` from the anchor.
- Wire: a per-unit goal tag (`en route` / `waiting: food` / …) and the
  structure-side equivalent, so the client can honour the visibility contract.

### Phase D — the audit sweep (results)

Every intent in `src/Sim.Core/**/*Intent.cs`, checked against the principle.

**Converted in M30**

| Intent | Was | Is |
|---|---|---|
| `AssignWorkersIntent` | skipped ids not on the tile | walks them there, takes the post on arrival |
| `AssignBuildersIntent` | same | same |
| `BeginBreedingIntent` | five simultaneous preconditions | both walk, first waits, food wakes the conception |
| `BuildIntent` (new) | — | place + builder + worker-to-man in one gesture |

**Already conforming** — no change needed

- `HaulIntent` / `HaulPickupEvent` / `HaulDepositEvent` — the origin of the
  pattern; `HaulPlan` is what `GoalPlan` is modelled on.
- `DispatchScoutIntent`, `EngageUnitIntent` (pursuit), `FormGroupIntent`
  (rendezvous), `MoveGroupIntent`, `MoveIntent` — all already express a goal
  and sequence their own travel.
- `PlaceSiteIntent` / `PlaceCanalIntent` / `PlaceWallIntent` /
  `ClearRubbleIntent` — placement gestures with no unit to move; the site's
  own condition-conjunction does the rest.
- `UnassignWorkersIntent`, `DisbandGroupIntent`, the diplomacy pair, the
  automation-layer intents (`ClaimUnit`, `SetOrder`, `ClearOrder`,
  `OrderStatus`), and the bandit spawn/despawn pair — no travel in their
  meaning at all.

**Converted 2026-08-23 (sweep, part 1): `TrainUnitIntent`.** "Train him as a
builder" used to mean walk him to the school, wait, come back, fire the
intent — the middle two steps being exactly the appointment this milestone
exists to delete. It now takes an **optional** `TrainerTile`: named, the sim
walks him there and flips the role on arrival; omitted, the pre-existing
"must be standing on it" contract holds byte for byte. Optional is what let
all seven existing call sites (AI ladders, client, tests) stay untouched
instead of needing a flag-day change.

Two supporting pieces landed with it: `GoalPlan.Arg`, one generic int
parameter whose meaning is defined by `Kind` (here the `UnitRole`), and
`TrainingRules`, which extracts the per-unit training body so the hand path
and the walked path share one implementation — the same argument that put the
assignment helpers next to `WorkAssignment.Release`. Snapshot **v29** carries
`Arg`. A training goal that lost its role across a restore would resume the
walk and then train the wrong thing, so that round-trip is a test.

**Client half, 2026-09-14.** The sim could walk a citizen to the school before
the client could ask it to, so the appointment survived in the UI for three
weeks: `CommandPanel` only offered a trade when the selection was *standing
inside* the building. It now offers every trade you own a trainer for, from
anywhere, and **the button names the destination** — "Builder — School (4,
12)". Choosing the nearest is client convenience; choosing it invisibly would
be the silent failure the visibility contract exists to prevent, so the tile is
on the face of the control before it is pressed.

Supporting: `TrainUnitAtPayload` (a separate class, because JsonUtility cannot
omit a field and an always-present `TrainerTile` of (0,0) would read as a real
instruction — the same split as `PlaceSitePayload`/`PlaceDockPayload`),
`IntentFactory.TrainAt`, `OrderIssuer.NearestTrainerFor`, `GoalTrain` in
`SimVocabulary`, and the Train case in the HUD's errand noun. Two tests in
`IntentJsonTests` feed the client's **verbatim** JSON through the server
deserializer — including the absent-field case that must land as null rather
than as (0,0) — so a rename on either side of the seam fails a test instead of
silently dropping orders in a running game.

**Sweep completed 2026-09-14: Equip, Loot, Embark.** Each takes an optional
target tile, so the standing-there contract is untouched and every existing
call site kept working. The interesting part was that the three need
**different answers** to "the precondition is unmet on arrival", and the
differences are not arbitrary:

| errand | unmet on arrival | why |
|---|---|---|
| Equip | **waits**, indefinitely | a shelf gets restocked; the deposit that brings a sword is the wake-up (`CargoTransfer`) |
| Loot | **dissolves** | a cache never refills — losing the race is a real outcome, and waiting for a refill that cannot come is the stall the contract forbids |
| Embark | **waits** for an absent hull, **dissolves** at a full one | a boat sails back; a full boat does not empty on any schedule the sim can wake on |

Embark is the one with two moving parts, so it needed a second wake-up:
`EmbarkGoal.OnBoatArrived`, hung off `MoveArrivalEvent` and gated on
`Role == Boat`. It also keeps **all-or-nothing on the immediate path and
per-passenger on the walked one** — a player looking at a crew on a quay is
entitled to "all of them or tell me why", but an errand spread over game-days
cannot honour that without letting one straggler hold the whole crew, forever
if they die en route.

Two defects the tests caught rather than review:

1. `EmbarkIntent` derived the quay from the **boat's** position, so a hull out
   at sea was beside no dock and the intent rejected — which made the
   boat-arrives-last case unreachable and `OnBoatArrived` dead code. Fixed with
   an optional `DockTile`; naming the quay is what makes "meet her at the
   harbour" expressible at all.
2. A passenger arriving at a **full** hull waited forever, because only the
   boat-arrival path checked the cap. Now both do.

No `FormatVersion` bump: `GoalKind` is an append-only byte enum and `GoalPlan`
already carries `TargetTile`, `PartnerUnitId` and `Arg`, so the three new kinds
changed no shape. `Arg` carries the item/resource; `PartnerUnitId` carries the
hull, which is not derivable from the berth (several boats can share a dock)
and which moves.

**Left step-shaped, deliberately** — the rest of the "target is whatever I am
standing on" family: `EquipUnitIntent`, `CraftEquipmentIntent`, `LootCacheIntent`,
`LoadCargoIntent`, `UnloadCargoIntent`, and the `Embark`/`Disembark` pair.

These have the same defect: the target is derived from where the body already
is, so there is nothing for a goal to aim at until each one names a target
tile. Train showed the conversion is mechanical once that parameter exists —
optional tile, re-check on arrival, one shared apply — and the remaining pieces
it needed (`GoalPlan.Arg`, a new `GoalKind` member) are now in place, so each
of these is a self-contained follow-up rather than a blocked one.

`CraftEquipmentIntent` was on this list by mistake and is now off it: it
already names a `BarracksTile` and no body travels, so it is an order to a
building with nothing to goal-shape. (Whether it should *wait* on materials
rather than reject is a separate precondition question, and belongs with
demand-driven haulage.)

`LoadCargoIntent` and `UnloadCargoIntent` are the real remainder.
Load-at-a-distance is very nearly `HaulIntent`'s first leg with no destination,
so converting it adds a second way to say one thing. Unload is the better half
— "go empty yourself at that stockpile" is a genuine one-decision goal you
cannot express today — but the cleaner fix may be upstream: teach `HaulIntent`
deposit-first semantics so it accepts a laden carrier, which removes the reason
`UnloadCargoIntent` exists as an escape hatch at all. That question should be
settled before either is converted.

`DisembarkIntent` is not a conversion at all: the passengers are already
aboard, so no body walks. The useful version — "sail to that dock and put
everyone ashore" — is a new composite (boat moves, arrival dispatches the
unload), and should be built as one if it is wanted.

### Phase E — `BuildIntent` composite

One gesture: place site, bind builder, bind worker-to-man.
`ConstructionSite.WorkerToManId` carries the manning sub-goal;
`BuildCompleteEvent` fires it as an `AssignWorker` goal on the finished
structure. Materials are **not** part of the composite (correction 2).

Two rules the implementation settled:

- **Placement validation is delegated**, not copied: `BuildIntent` calls
  `PlaceSiteIntent.Resolve` and returns its rejection verbatim, so the
  composite can never drift from the plain placement it is built on.
- **Nothing rejects after the site exists.** Once placement succeeds the goal
  ("a structure should exist here") has been accepted; an unavailable builder
  is announced and the site stands tagged, because a late rejection would
  leave the world changed while telling the player their intent was refused.
- At completion the bound worker is **released from whatever they were doing**
  and dispatched. That is not the sim retasking someone on its own initiative:
  the player bound that body to this post when they placed the site.

---

## Built state (2026-08-20)

All five phases landed. Suite: **987 + 39 green, 0 failures.**

New: `GoalPlan` / `GoalKind` (src/Sim.Core/World/GoalPlan.cs), `Unit.Goal`,
`Activity.Waiting`, `GoalRules` (src/Sim.Core/Intents/GoalRules.cs),
`GoalDissolvedEvent`, `BreedGoal`, `GoalExpiryEvent`, `House.PendingBreed`,
`ConstructionSite.WorkerToManId`, `BuildIntent`, snapshot **v27**, and the
`GoalKind`/`GoalState`/`GoalX`/`GoalY` fields on `UnitDto`.

Refactor picked up along the way: the two per-unit assignment implementations
moved into `WorkAssignment` beside `Release`, so the intent path and the goal
path share one implementation — the same "these two must never drift" argument
that put `Release` there after the ghost-worker bug.

Test churn worth knowing about: `ProductionTests.AssignWorkers_NotOnTile_*` and
`BuildIntentTests.AssignBuilders_NotOnSiteTile_*` **asserted the old defect**
and were inverted; two `HouseBreedingTests` rejections became waits. One
unrelated red test (`GhostWorkerTests.TheWorkSlotIsReusableAfterADeath`) was a
stale fixture — its lone starving worker had become the realm's last unit, so
the death was extinction, extinction is defeat, and a defeated player's intents
all reject. Fixed by giving the fixture a survivor.

---

## Risk register

- **`Activity.Waiting` is a new enum value in a serialized enum.** Append-only
  is the rule and 5 is free, but every `switch` over `Activity` needs an audit
  — including the client's.
- **Ordering inside `MoveArrivalEvent`** is load-bearing and already carries
  three branches. The goal branch goes last among errands; the comment block
  there must be extended, not replaced.
- **`GoalExpiryEvent` must survive restore** or a restored pair stalls forever
   — the exact failure the event exists to prevent.
- **Scope creep into the automation tier** is the standing danger: chaining and
  standing orders become trivial once goals exist. They stay locked.
