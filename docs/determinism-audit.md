# Determinism Audit (Phase F)

This is a one-time audit recording the structural invariants the M1
architecture rests on. Re-run when any of the trigger conditions in the
last section land.

## Invariants verified

### 1. No global tick — no per-time iteration over the world

The event-driven sim's load-bearing claim is "scale with the number of
decisions, not with elapsed time or map size" (design doc §2.2). That
breaks the moment any code starts walking the whole world on a timer.

**Audit commands:**

```bash
grep -rn "Structures\.Values\|Structures\.Keys\|foreach.*Structures" src/Sim.Core/
grep -rn "Units\.Values\|Units\.Keys\|foreach.*world\.Units\|foreach.*World\.Units" src/Sim.Core/
```

**Iterations of `world.Structures.Values`:**

| File | Purpose | Verdict |
|---|---|---|
| `Persistence/Snapshot.cs:175` | Canonical serialization in (y, x) order | Allowed — serialization is the one legitimate global iterator |

**Iterations of `world.Units.Values`:**

| File | Purpose | Verdict |
|---|---|---|
| `Persistence/Snapshot.cs:119` | Canonical serialization in id order | Allowed — same reason |
| `World/Structure.cs:208` (`ConstructionSite.BuildersPresent`) | Count builders on a specific tile | Allowed — bounded *per call*; called only from `BuildCompleteEvent` and `AssignBuildersIntent` (event-driven, not time-driven) |
| `Logistics/BuildCompleteEvent.cs:49` | Free builders on the completing site's tile | Allowed — same shape; called once per build completion |

**Neither iteration is on a global timer.** Every call site is inside an
event's `Apply` or an intent's `Resolve` — both fire only as the sim
schedules them, not on wall-clock or grid-size cadence. The invariant
holds.

**Known scaling concern (not a determinism violation).** Both
`BuildersPresent` and `BuildCompleteEvent` scan all units to find ones
on a specific tile. That's O(units) per call. Fine at M1 scale; at large
unit counts both will want an index — e.g. `Dictionary<TileCoord, List<int>>`
of "units at tile" maintained on every position write. The fix is
mechanical when it matters; recording the spot here so it doesn't get
forgotten.

### 2. `ProductionTickEvent` has bounded call sites

The back-pressure model (Phase D) is correct iff `ProductionTickEvent` is
scheduled only by the three sites that own the "should production run
right now?" decision: assignment, re-arm, and self-continuation.

**Audit command:**

```bash
grep -rn "new ProductionTickEvent" src/
```

**Constructors:**

| File | Site | Verdict |
|---|---|---|
| `Logistics/ProductionTickEvent.cs:63` | Self-reschedule at end of `Apply` | Allowed |
| `World/Structure.cs:119` (`Extractor.ArmIfDormant`) | Re-arm after worker count or buffer space change | Allowed |

`AssignWorkersIntent.Resolve` schedules production via
`Extractor.ArmIfDormant` (line 51), not by constructing the event
directly — so the only constructor call sites are the two above. The
invariant holds.

`Extractor.ArmIfDormant` itself is `internal` and called from exactly
three places (one being a test), all expected:

| File | Site | Verdict |
|---|---|---|
| `Logistics/AssignWorkersIntent.cs:51` | After assignment, when extractor was dormant | Allowed |
| `Logistics/HaulPickupEvent.cs:81` | After pickup frees buffer space | Allowed |
| `tests/Sim.Tests/ProductionTests.cs` | Test-only re-arm via `InternalsVisibleTo` | Allowed |

### 3. No view path writes back to state

The persistence model (`docs/persistence-model.md`) and the deleted-Phase-B
lazy-regen design both rest on "reads are pure — never write back from a
view." If a view computes derived state and persists it, observation
timing becomes part of state, replay diverges.

There are no view paths in the codebase today (no UI, no clients, no
read-only projection types). This is a *forward-looking* invariant
recorded here so the first view path that lands knows the rule. When
that first reader-of-derived-state is introduced, this section needs
expanding with the file paths that satisfy / are forbidden from
satisfying the rule.

## Roads addendum (M2)

M2 introduced per-tile road condition with lazy decay and traffic gain.
Three properties were verified after Phase E:

### Road state has no global iteration

```bash
grep -rn "world\.Roads\|\.Roads\[" src/Sim.Core/
```

`world.Roads` is touched in:

| File | Purpose | Verdict |
|---|---|---|
| `Persistence/Snapshot.cs:339, 362` | Canonical serialization (ordered by `(y, x)`) | Allowed — same as Structures/Units |
| `Roads/Road.cs` (several) | Targeted reads + writes by tile key | Allowed — bounded per-tile, not iteration |

No code iterates the road set on a timer or by global sweep. Lazy decay
runs on touch — each `CreditTraffic` calls `CatchUpDecay` for *that one
tile* before applying gain.

### `CreditTraffic` is called only from the one mutation point

```bash
grep -rn "CreditTraffic" src/Sim.Core/
```

| File | Site | Verdict |
|---|---|---|
| `Movement/MoveArrivalEvent.cs:52` | After unit position update on real arrival | Allowed — the one mutation point |
| `Roads/Road.cs` (definition) | — | — |

Tests call `CreditTraffic` directly via `InternalsVisibleTo`. No
production code path other than `MoveArrivalEvent.Apply` mutates road
state.

`CatchUpDecay` (internal) is called only by `CreditTraffic` and
`ConditionAt` (which is read-only — see next item) and by tests.

### No read path writes road state

The pure-read wall:

| Read site | Calls | Mutates? |
|---|---|---|
| `Roads.EffectiveCost` | `ConditionAt` (pure read) | No |
| `Roads.ConditionAt` | (computes decay-adjusted condition) | No |
| `Pathfinding.FindPath` (via `costFn`) | `Road.EffectiveCost` | No |
| `MoveIntent.ScheduleNextStep` (via `costFn`) | `Road.EffectiveCost` for path query + per-step arrival cost | No |

Enforcement is a runtime test: `Pathfinding_IsPureRead_NoRoadMutation`
in `RoadPathfindingTests.cs` runs 100 path queries over a roaded world
and asserts `Snapshot.Hash` is unchanged. A future reader that writes
would surface there.

## Fog addendum (M3)

M3 introduced per-player explored memory + live visibility derivation.
The "fog never touches the sim" property is the headline determinism
contract; this audit pins the structural invariants behind it.

### Explored memory has exactly one write path

```bash
grep -rn "world\.Explored\|Sight\.Reveal" src/Sim.Core/
```

| File | Site | Verdict |
|---|---|---|
| `Vision/Sight.cs:Reveal` | The mutation method itself | — |
| `Movement/MoveArrivalEvent.cs:56` | Per-hop arrival reveal | Allowed — event-driven |
| `Logistics/BuildCompleteEvent.cs:66` | New vision structure reveal | Allowed — event-driven |
| `World/Genesis.cs:62,72` | Initial Castle + unit reveal | Allowed — genesis setup |
| `Persistence/Snapshot.cs` | Serialize iteration + restore reconstruction | Allowed — canonical I/O |
| `Vision/View.cs:79` | Read-only copy into PlayerView.Explored | Allowed — defensive copy, not a write |

`Sight.Reveal` has exactly three production callers, all event-driven.
No view path writes explored. The inverted pure-read wall holds.

### Live visibility never mutates

`View.VisibleTiles` and `View.BuildPlayerView` are both PURE READS —
they iterate the player's owned vision sources, union their discs into
a fresh HashSet, and return it. They never write `world.Roads`,
`world.Explored`, `world.Units`, or any other sim state.

Enforced by runtime test:
- `LiveVisibilityTests.VisibleTiles_IsPureRead_NoMutation` — 100×
  calls, snapshot hash unchanged.
- `FogDeterminismTests.ViewsOff_HashEquals_ViewsOn` — the headline
  test: same scenario hashed with and without view spam, hashes match.

### Players registry has no global iteration

`world.Players.Values` is iterated only in `Persistence/Snapshot.cs`
(canonical serialization). `View.BuildPlayerView` takes a single
playerId and accesses only that player's explored set — no
per-all-players sweep.

### No global per-tile fog sweep

Visibility is computed by iterating the player's vision sources and
unioning their discs (`sources × r²`). There is no "for each tile,
check if visible to player P" iteration anywhere.

## Groups addendum (M5)

M5 introduced `Group` as a first-class entity (`GameWorld.Groups`) with
`FormGroupIntent`, `MoveGroupIntent`, `DisbandGroupIntent`, and the
`GroupArrivalEvent` for per-hop arrivals. Groups reuse the existing
fencing-token and M4 anchor patterns.

### Group state has bounded mutation sites

```bash
grep -rn "world\.Groups\|\.Groups\[" src/Sim.Core/
```

`world.Groups` is touched in:

| File | Purpose | Verdict |
|---|---|---|
| `Persistence/Snapshot.cs` (`WriteGroups` / `ReadGroups`) | Canonical serialization in id order | Allowed — I/O, not event-driven |
| `Persistence/RegenerateQueue.cs` (`From`) | Per-group `RegenerateGroupMoveAnchor` reads anchor + regen event | Allowed — read-only over current state |
| `Groups/FormGroupIntent.cs` (`Resolve`) | Creates a new group, sets members' GroupId | Allowed — event-driven |
| `Groups/MoveGroupIntent.cs` (`Resolve`) | Bumps epoch + sets anchors on existing group | Allowed — event-driven |
| `Groups/DisbandGroupIntent.cs` (`Resolve`) | Removes group, clears members | Allowed — event-driven |
| `Groups/GroupArrivalEvent.cs` (`Apply`) | Updates group position, pops PathRemaining, transitions Idle on final arrival | Allowed — event-driven self-mutation |
| `Movement/MoveArrivalEvent.cs` (`DispatchOnFinalArrival`) | Decrements `Group.PendingArrivals` when a Forming member reaches rendezvous | Allowed — event-driven |

No view path or pure-read mutates `world.Groups`. No global iteration on a
timer.

### `GroupArrivalEvent` has bounded callers

```bash
grep -rn "new GroupArrivalEvent" src/Sim.Core/
```

| File | Site | Verdict |
|---|---|---|
| `Groups/MoveGroupIntent.cs` (`ScheduleNextHop`) | Self-reschedule via the per-hop helper | Allowed |
| `Persistence/RegenerateQueue.cs` (`RegenerateGroupMoveAnchor`) | Recovery-only | Allowed |

`ScheduleNextHop` is called from `MoveGroupIntent.Resolve` (first hop) and
from `GroupArrivalEvent.Apply` (continuation). The chain is self-driving;
no other code path constructs `GroupArrivalEvent`.

### `Group.MovementEpoch` has bounded bumpers

`group.BumpEpoch()` is called only from:
- `MoveGroupIntent.Resolve` (retasking)
- `DisbandGroupIntent.Resolve` (cancellation)

Stale `GroupArrivalEvent`s self-fence on epoch mismatch, mirroring the
M2/M4 pattern for `Unit.AssignmentEpoch`.

### Solo intents reject grouped units

Verified by code:

| Intent | Check |
|---|---|
| `MoveIntent.Resolve` | `if (unit.GroupId is not null) return Reject(...)` |
| `HaulIntent.Resolve` | `if (hauler.GroupId is not null) return Reject(...)` |
| `AssignBuildersIntent.Resolve` | `if (unit.GroupId is not null) continue` (per-id skip) |
| `AssignWorkersIntent.Resolve` | `if (unit.GroupId is not null) continue` (per-id skip) |
| `UnassignWorkersIntent` | No check (grouped+Working unreachable by construction) |

### M4 anchor pattern extends

`Group` carries `PathRemaining`, `PathFinalDest`, `NextArrivalTick`,
`NextArrivalSeq`, and `MovementEpoch`. `RegenerateQueue.From` iterates
`world.Groups` and rebuilds queued `GroupArrivalEvent`s with their original
`Seq` via `Simulation.ScheduleWithSeq` — same shape as units. The
M4 headline contract (mid-flight snapshot+restore = uninterrupted hash)
extends to groups, proven by
`GroupMovementTests.MovingGroup_SnapshotMidFlight_RestoreReachesSameHash`.

## What would break these invariants and require re-running this audit

Re-run the greps above when any of the following lands:

- **A new `ScheduledEvent` subclass** — verify its `Apply` doesn't open
  a new global iteration; verify its construction sites are bounded.
- **A new `Intent` subclass** — same.
- **A new entry point on `Simulation`** other than `Submit*` or
  `Schedule` — e.g., a "tick everything" method (don't add one).
- **A new structure type** that needs to know about units or other
  structures globally — index it instead of scanning.
- **The first view path / read-model / UI projection** — pin the
  no-write-back property at that moment, before the second view exists
  and the pattern is established.
- **Any feature that needs to find "all X in the world"** — index it,
  don't scan, and update the table above to record the new bounded
  caller.
- **A new caller of `Road.CreditTraffic` or `Road.CatchUpDecay`** —
  road state has exactly one mutation point (`MoveArrivalEvent.Apply`).
  Any new caller breaks that property and needs justification.
- **A new road-state-derived field** (e.g. road type / band marker /
  edge-condition) — re-verify lazy catch-up math against the
  observation-independence property; ensure pure reads still match
  write-path output for the same `now`.
- **A new caller of `Sight.Reveal`** — explored memory has exactly
  three event-driven write sites today. Any new caller must be itself
  event-driven (called from inside a `ScheduledEvent.Apply` or
  `Genesis.Build`), never from a view path or query.
- **A new field on `PlayerView` / `View` consumer** — re-run the
  headline `ViewsOff_HashEquals_ViewsOn` test if any computation in
  the view path looks like it might touch sim state. The invariant
  is "views are pure reads"; new fields must not break it.
- **A new vision source kind** (a new structure or unit role with a
  non-zero radius from `Sight.RadiusFor`) — confirm it shows up in
  `View.VisibleTiles` correctly and reveals into explored on creation
  / movement.
- **A new Group state or anchor field** — re-run the
  `MidFlightSnapshotTests` / `GroupMovementTests` mid-flight extension to
  confirm RegenerateQueue still rebuilds correctly.
- **A new caller of `new GroupArrivalEvent(...)`** — should be only
  `ScheduleNextHop` or `RegenerateGroupMoveAnchor`. Anything else needs
  justification.
- **A new intent that targets a Unit** — must add the
  `unit.GroupId is not null` rejection check (consistent with §M5
  audit).

## Reference

This audit verifies the architectural claims in design doc §2.2
(event-driven core), §2.3 (one global queue), the persistence model's
in-flight-correctness-gap framing, and the back-pressure mechanism
shipped in Phase D. The audit doc itself is part of M1 Phase F.
