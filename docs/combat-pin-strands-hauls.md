# Combat pin strands in-flight units

**Status:** FIXED 2026-09-21 (see the update at the bottom). Sim + driver fix, no wire change.
**Symptom:** a hauler on a food line stands still holding cargo, at reduced health, while
the Machine page reports the order as "Working — crew is on the trip". The castle starves.

## The report

Order #5, *Keep Castle above 300 Food from Farm*, crew Hauler #3. The hauler stood next
to a house at night, carrying 25, health 4/10, not moving. The order showed green and
"crew is on the trip (just now)". The realm was in famine with 1.4 days to the next
death.

The live world was gone by the time this was traced, so the reading below is from the
code. It accounts for every detail on screen, including the damage.

## Mechanism

Only combat lowers a unit's health (`Combat/CombatRoundEvent.cs:184-203`; starvation
kills outright, age kills outright, nothing else touches `Unit.Health`). So the hauler
fought, almost certainly a bandit party that crossed its path on the return leg.

### 1. The pin is one-way

When a fight begins on a tile, `CombatTrigger.MaybeBeginCombatOnTile` pins every
belligerent so it fights instead of walking off (`Combat/CombatTrigger.cs:102-110`):

```csharp
if (u.PathRemaining is not null || u.NextArrivalSeq is not null)
{
    u.PathRemaining = null;
    u.PathFinalDest = null;
    u.NextArrivalTick = null;
    u.NextArrivalSeq = null;
    u.BumpEpoch();
}
```

That is right for the fight. The epoch bump fences the in-flight `MoveArrivalEvent`, so
the hop the unit was on never lands. But the pin touches only the movement anchors. It
leaves in place:

- `Activity = Hauling`
- `HaulPlan = { Phase = ToDest, DestTile = castle, Resource = Food }`
- `CargoAmount = 25`

### 2. Nothing un-pins

Combat ends at one of three `world.CombatStates.Remove(Tile)` sites in
`CombatRoundEvent.Apply` (`:76`, `:113`, `:171`). None of them looks at the survivors.
The haul continues only from `MoveArrivalEvent.Apply` (`Movement/MoveArrivalEvent.cs:183-203`):
arrive at the plan's stop, schedule `HaulPickupEvent` or `HaulDepositEvent`. With no
path and no pending arrival, that code is never reached again. The unit is a statue
with a plan.

Groups get the same pin (`CombatTrigger.cs:113-123`) and the same non-resume, but a
group is set back to `Idle` so at least it reads as stopped. A solo hauler keeps
`Activity.Hauling`, which is the worse outcome: everything downstream reads "busy".

### 3. The driver reads "busy" as "on the trip"

`SubstrateDriver` (`Automation/SubstrateDriver.cs`) judges a held hand with
`IsFreeForWork` (`:892`):

```csharp
u.Activity == Activity.Idle && u.PathRemaining is null
&& u.NextArrivalTick is null && u.HaulPlan is null && !u.IsEmbarked
```

The stranded hauler fails on `Activity` and `HaulPlan`, so:

- `HeldAndFree` (`:208`) never returns it: the order will not reuse it.
- `ReleaseIdleSurplus` (`:192`) and the tidy in `:184` release only when
  `IsFreeForWork && CargoAmount == 0`: the order will not let it go.
- With no hand to dispatch, the order reaches `HoldsLiveHands` (`:356`), which asks
  only whether a claimed unit still exists. It does. The journal writes
  `InFlight, "crew is on the trip"` every think, forever.

Even if the activity were somehow Idle, the laden rule at `:370-378` would report
`Blocked, "unit N is carrying 25 Food"` and burn the retry budget until the order
suspends. So there is no path by which this unit ever delivers or is ever replaced.

The label is honest by its own definition (the order holds a living hand) and wrong
about the world. This is the exact failure `docs/automation-as-core-game.md` law 4
warns about: attrition must *erode visibly*, not hide behind a green row.

### Who else is stranded

The same pin hits every solo unit with a standing obligation and a travel leg:

| Obligation | Anchor | Continues from | After a pin |
|---|---|---|---|
| Haul | `Unit.HaulPlan` | `MoveArrivalEvent` at the stop | stranded, `Activity.Hauling` |
| Goal (worker / builder / breed, M30) | `Unit.Goal` | `MoveArrivalEvent` at the goal tile | stranded, goal shows "en route" |
| Pursuit (M29) | `Unit.Pursuit` | `PursuitRules.Step` from `MoveArrivalEvent` | stranded mid-chase |
| Scout mission | `ScoutMissionRunner.Advance` from `MoveArrivalEvent` | stranded mid-mission |

The fix must cover all four, or the next report is a builder who "is on the way" to a
site forever.

## The fix

Two parts. The first repairs the world; the second makes the driver unable to lie about
it. Do both.

### A. Resume interrupted travel when the fight ends (Sim.Core)

Add one helper and call it at every combat-end site:

```csharp
// Sim.Core/Combat/CombatRules.cs
// A pin (CombatTrigger) clears movement anchors and nothing else. When the
// tile's combat ends, every survivor that still carries an obligation with a
// travel leg gets that leg re-issued from where it stands. Pure function of
// world state at the tick the combat ends: deterministic and replay-safe.
public static void ResumeInterrupted(Simulation sim, TileCoord tile)
{
    var world = sim.World;
    foreach (var u in world.Units.Values)                 // same scan CombatTrigger uses
    {
        if (u.Position != tile || u.IsEmbarked || u.GroupId is not null) continue;
        if (u.PathRemaining is not null || u.NextArrivalTick is not null) continue; // already walking

        // Same dispatch order as MoveArrivalEvent.Apply, so a unit never gets two legs.
        if (u.Pursuit is not null)
        {
            PursuitRules.Step(sim, u);                    // already handles "target gone"
            continue;
        }
        if (world.ScoutMissions.TryGetValue(u.Id, out var m)
            && m.State != Scouting.ScoutMissionState.Returned)
        {
            Scouting.ScoutMissionRunner.Advance(sim, u);
            continue;
        }
        if (u.HaulPlan is { } plan)
        {
            var stop = plan.Phase == HaulPhase.ToSource ? plan.SourceTile : plan.DestTile;
            if (Logistics.HaulStops.AtStop(world, u, stop))
                sim.Schedule(sim.Now, plan.Phase == HaulPhase.ToSource
                    ? new Logistics.HaulPickupEvent(u.Id, plan.SourceTile, plan.DestTile, plan.Resource, u.AssignmentEpoch)
                    : new Logistics.HaulDepositEvent(u.Id, plan.DestTile, u.AssignmentEpoch));
            else
                Movement.MoveIntent.BeginMove(sim, u, stop);
            continue;
        }
        if (u.Goal is { } goal)
            Movement.MoveIntent.BeginMove(sim, u, goal.TargetTile);
    }
}
```

Call it just before each `world.CombatStates.Remove(Tile)` in `CombatRoundEvent.Apply`
(`:76`, `:113`, `:171`). Do not call it from `CombatTrigger`; the point is that units
stay put while the fight lasts.

Notes for the implementer:

- `MoveIntent.BeginMove` is `internal static` (`Movement/MoveIntent.cs:83`); it already
  does the pathfinding and schedules the first hop. Reuse it rather than re-deriving.
- The "already at the stop" branch matters: a hauler pinned on the castle tile itself
  should deposit, not walk a zero-length path.
- Groups are out of scope here. A pinned group is set `Idle` and the player re-orders
  it; that is existing, visible behaviour.
- `MoveArrivalEvent.Apply` dispatches Pursuit, then scout mission, then HaulPlan, then
  Goal (`Movement/MoveArrivalEvent.cs:165-210`). Keep that order here so a unit never
  gets two legs.
- A Goal walker that is already on `TargetTile` needs the goal's own arrival handling,
  not a move; check what `MoveArrivalEvent` does for `Goal` at the tile and mirror it.
- Old snapshots already contain stranded units. `ResumeInterrupted` only runs on combat
  end, so those stay stranded. Part B handles them.

### B. Detect a stalled hand in the driver (Sim.Server)

Add a predicate beside `IsFreeForWork`:

```csharp
// A hand that is neither free nor going anywhere: an obligation with no
// travel leg and no pending arrival. Combat pins used to produce these (see
// docs/combat-pin-strands-hauls.md); the sim now resumes them, and this is
// the belt to that brace — an order must never report a statue as a trip.
private static bool IsStalled(Unit u) =>
    !IsFreeForWork(u)
    && u.PathRemaining is null
    && u.NextArrivalTick is null
    && u.Activity is Activity.Hauling
    && !u.IsEmbarked;
```

`Activity.Hauling` is the right gate for this driver: a Working or Building unit has no
travel leg by design, and a goal walker is not a driver hand. Then:

1. In the release sweeps (`:184`, `:200`), also release a hand that `IsStalled`. It
   keeps its cargo (`MoveIntent` already lets a laden unit be retasked), and the order
   is free to pull another.
2. In the no-hand branch, before `HoldsLiveHands` (`:356`), if every held hand is
   stalled, journal `Blocked, "unit N stalled carrying 25 Food at (x,y)"` instead of
   `InFlight`. This is the sentence the player needed to read.
3. Optionally, have the driver itself re-issue the leg for a stalled *own* hand rather
   than release it. That duplicates Part A in the wrong layer; prefer release plus a
   loud journal line and let Part A do the resuming.

### What this does NOT change

- `Unit.Health` stays down. There is no regeneration in the sim today; that is a
  separate design question, not this bug.
- The wire. `UnitDto` already sends `activity`, `cargoAmount`, `hopTo*` and `goalState`;
  after the fix, a resumed hauler shows a hop again and a released one shows Idle. If
  the client ever wants to draw "stalled" before the sim resumes it, that is one
  additive flag on the unit row and a one-line mirror; not needed if A lands.
- Combat itself. The pin is correct; only the un-pin was missing.

## Tests to pin it

All in `tests/Sim.Tests`, config-derived (no hard-coded tick counts; see
`docs/architecture.md` testing standards).

1. **`HaulResumesAfterCombatPin`** — hauler on the ToDest leg, bandit steps onto its
   tile, combat runs to the bandit's death, assert the hauler is walking again on the
   next tick and the deposit lands at the castle within the expected travel time.
2. **`HaulResumesWhenPinnedAtTheStop`** — same, with the fight on the destination tile;
   assert deposit without a move.
3. **`GoalWalkerResumesAfterCombatPin`** and **`PursuitResumesAfterCombatPin`** — one
   each, same shape.
4. **`StalledHandIsReleasedAndReported`** — construct the stranded state directly
   (activity Hauling, plan set, no path, cargo > 0, claimed by a Pull order), run one
   driver think, assert the journal says Blocked with the unit id and the claim is gone.
   This also covers pre-fix snapshots.
5. **Determinism**: a replay of a recording that contains a pinned haul must produce the
   identical world hash; `ResumeInterrupted` runs inside an event `Apply`, so it should,
   but the existing replay test suite should be run to prove it.

## Rescuing a game in progress

Right-click move the hauler onto the castle tile, then Unload. The move is authoritative
and keeps the cargo, but it sets the unit Idle, so `HaulDepositEvent` (which requires
`Activity.Hauling`, `Logistics/HaulDepositEvent.cs:48`) will not fire on arrival; the
explicit Unload finishes it. The unit becomes free and empty, and the order's claim
clears on the next think.

## Ownership

- Part A and its tests: sim (Sim.Core, `Combat/`, `Movement/`).
- Part B and its test: automation driver (Sim.Server, `Automation/`).
- Client: nothing, unless the stalled flag is wanted before A lands (contract seam,
  one field).

## Update 2026-09-21 — shipped, with two deviations from the plan above

Parts A and B landed as written, with `CombatRules.ResumeInterrupted` called from a
single `EndCombat` helper at the three unit-fight end sites in `CombatRoundEvent`
(fort sieges end in `FortSiege` and have nobody standing on the tile). Tracing the
pin through `MoveArrivalEvent` showed the strand had a second half the plan missed,
which changed where two of the four obligations are protected:

1. **The pin lands INSIDE an arrival, and the arrival then dispatches.** The trigger
   runs before the path pop, so after a mid-route pin `PathRemaining` is null and
   `DispatchOnFinalArrival` runs on the wrong tile. That is what actually happened to
   each obligation before the fix: a haul was left in place (stranded, as reported);
   a **goal** was dissolved with "no work slot free" (the worker never reached its
   post, and the reason lied); a **scout** advanced from the wrong tile (it
   self-healed by accident); a **pursuit** was released by rule 4. So the fix has a
   third piece: `MoveArrivalEvent.Apply` now returns right after the trigger when the
   pin landed short of `FinalDestination`, leaving every obligation exactly as it was.
   A pin ON the destination is a real arrival and still dispatches (the hauler pinned
   on the castle deposits under the pin, before round 1 fires; the test pins that).

2. **Pursuit is not stranded, it was ENDED.** A chase commits one hop at a time, so a
   pin always lands at a hop target and reaches `PursuitRules.Step` rule 4, which
   released the chase on the grounds that nothing would re-enter `Step` after the
   fight. The un-pin is that re-entry, so rule 4 now *suspends* (keeps the anchor,
   issues no leg) and `ResumeInterrupted` steps the chase again when the bystander
   fight ends. The catch itself is unchanged (rule 3, co-location, still releases).
   The patrol driver reads a held `Pursuit` as "in pursuit of unit N" throughout,
   which is true: the pursuer is still chasing, just pinned.

The "who else is stranded" table therefore reads, after the fix: haul — resumed at
combat end; goal — protected from the false dispatch, resumed at combat end; scout —
protected the same way, resumed at combat end; pursuit — suspended, resumed at combat
end. Groups remain out of scope, as planned.

Tests: `tests/Sim.Tests/CombatPinResumeTests.cs` — the four resume cases, the
stalled-hand driver test, and a twin-run + snapshot hash across a pinned haul.
