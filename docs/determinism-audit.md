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

## Reference

This audit verifies the architectural claims in design doc §2.2
(event-driven core), §2.3 (one global queue), the persistence model's
in-flight-correctness-gap framing, and the back-pressure mechanism
shipped in Phase D. The audit doc itself is part of M1 Phase F.
