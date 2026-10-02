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
| `Scouting/ScoutObservation.cs:Capture` | Bucket units inside the scout's vision disc by tile | Allowed — bounded *per call*; called only from `MoveArrivalEvent.Apply` (event-driven), and only for a unit with an active mission. Same O(units)-per-call scaling flag as `BuildersPresent`; a per-tile unit index is the shared fix |

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

### `ScoutObservation.Capture` is the one write site for the observation log (M20)

A scout's observation log (`GameWorld.ScoutMissions[id].Legs`) is durable,
hashed sim state of the inverted-pure-read shape: written only by a
deterministic event, read only on the presentation side (the server-side
claims compiler — never by Sim.Core to drive the sim).

**Audit command:**

```bash
grep -rn "ScoutObservation.Capture\|\.Legs\.Add\|ScoutMissions" src/Sim.Core/
```

| File | Site | Verdict |
|---|---|---|
| `Scouting/ScoutObservation.cs:Capture` | The mutation method itself | — |
| `Movement/MoveArrivalEvent.cs` | Per-hop arrival capture, right after `Sight.Reveal` | Allowed — event-driven, the one write site |
| `Persistence/Snapshot.cs` | Serialize iteration + restore reconstruction | Allowed — canonical I/O |

`Capture` records only the scout's own live vision disc (the disc
`Sight.Reveal` just revealed), so the log is fog-honest by construction —
no `Explored`/`RememberedBiome` is consulted. It early-returns (one dict
lookup) for any unit with no active mission, so it is not a global sweep
and non-scouts pay nothing. Build progress is read via the pure
`EffectiveProgress` helper (mirrors `ConstructionSite.Pause`'s formula
without writing). Pinned by `ScoutObservationTests` (twin-run hash,
snapshot round-trip v14, fog-honesty).

**M20 Phase 3 — mission lifecycle writers.** Beyond the log, a `ScoutMission`
has identity/plan fields (init-only) and two mutable cursor fields
(`State`, `WaypointCursor`). Their write sites:

| Field | Writer | Verdict |
|---|---|---|
| Mission creation (in `GameWorld.ScoutMissions`) | `DispatchScoutIntent.Resolve` | Allowed — intent-driven, validated at resolution time |
| `State` / `WaypointCursor` | `ScoutMissionRunner.Drive` (called from `DispatchScoutIntent.Resolve` and `MoveArrivalEvent.DispatchOnFinalArrival`) | Allowed — both are intent/event-driven, like the `HaulPlan` continuation it mirrors |
| `Legs` | `ScoutObservation.Capture` | Allowed — above |

The runner is IN-SIM (not a server driver), so replay-from-intent-log and
mid-mission snapshot/restore are correct for free: the plan + cursor + state
snapshot, and the in-flight move regenerates from the unit's anchors. No new
scheduled-event types (it reuses `MoveArrivalEvent`), so `RegenerateQueue` is
untouched. `RecallRuleFired`/`LastLegSawHostile` are pure reads (the mission's
own log + `Diplomacy.AreHostile`, public knowledge). Pinned by
`ScoutDispatchTests` (twin-run hash, snapshot round-trip mid-mission, recall
rules, Lodge-gated validation).

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

## Persistence addendum (M4 — completed 2026-06-03)

M4 added durable storage (SQLite intent log + snapshot store) and the
`Recovery` orchestrator. The audit confirms the invariants that protect
the durability contract.

### Durable artifacts have schema-stable formats only

| Artifact | Format | Schema stability |
|---|---|---|
| Intents | JSON via `System.Text.Json` with the hand-written `IntentJson` type-name registry | Stable — type-names are frozen at first ship; class-shape changes that don't break JSON round-trip are safe |
| Snapshots | Binary, magic + 4-byte `FormatVersion` + canonical state encoding | Forward-compatibility via version refusal: `Snapshot.Restore` throws on mismatch, operator runs snapshot-on-deploy |

```bash
grep -rn "class.*: Intent\b" src/Sim.Core/
# every intent class above appears in IntentJson.TypeNames + IntentJson.Deserialize.
```

**No `ScheduledEvent` subclass appears in any durable format.** Events are
derived from `state × code`; persisting them would lock the durable store
to specific event types and break the M4 architectural promise.

```bash
grep -rn "ScheduledEvent" src/Sim.Persistence/
# returns only references in the Recovery orchestration path (sim.SubmitIntent
# schedules IntentEvent internally); no event types serialized.
```

### `Simulation.ScheduleWithSeq` has exactly one production caller

```bash
grep -rn "ScheduleWithSeq" src/
```

| File | Site | Verdict |
|---|---|---|
| `Engine/Simulation.cs` | Definition | — |
| `Persistence/RegenerateQueue.cs` (3 calls) | Reconstructing in-flight events from anchors | Allowed — the only production caller |
| `tests/` | Test access via `InternalsVisibleTo` | Allowed |

Live code uses `Simulation.Schedule(at, e)` (consumes the next monotonic
`Seq`). `ScheduleWithSeq` exists solely to preserve original `Seq` values
during recovery; if a future feature is tempted to call it directly,
that's an architecture smell — file a decision doc instead.

### `Recovery.Recover` anchors on the snapshot, not on genesis

The insulation property is enforced by
`RecoveryTests.PreSnapshotIntentsCanBeDeleted_StillRecovers`. The test
deletes every intent with `tick <= snapshot.tick` from the durable log
before calling `Recover`; recovery still completes successfully and
reaches the uninterrupted hash. This proves that pre-snapshot intents are
not load-bearing for live recovery — only for debug-mode genesis replay.

### In-flight anchor fields are written only by event-driven sites

The M4 anchors (`Unit.PathRemaining`, `Unit.NextArrivalTick/Seq`,
`Unit.HaulPlan`, `Extractor.NextProductionTickSeq`,
`ConstructionSite.BuildCompleteSeq`, `Group.PathRemaining`,
`Group.NextArrivalTick/Seq`) are written exclusively from:

- `MoveIntent.Resolve` / `MoveIntent.BeginMove` (movement anchor set on
  command)
- `MoveArrivalEvent.Apply` (per-hop pop + next-hop schedule)
- `Extractor.ArmIfDormant` + `ProductionTickEvent.Apply`
- `ConstructionSite.StartOrResume` + `ConstructionSite.Pause`
- `HaulIntent.Resolve` / `HaulPickupEvent.Apply` / `HaulDepositEvent.Apply`
- `FormGroupIntent` / `MoveGroupIntent` / `GroupArrivalEvent.Apply`
- `Snapshot.Restore` (read-back from the snapshot blob)
- `RegenerateQueue.From` (read-only — never writes back; only reads to
  re-schedule)

No view, no pure-read path, no host code outside the event-driven sites
writes these fields.

## Biome degradation addendum (M9 — completed 2026-06-05)

Three new pieces of state — `GameWorld.Fertility` (sparse per-tile),
`GameWorld.BiomeDegradationConfig`, and `GameWorld.RememberedBiome`
(per-player per-tile) — plus a derived-biome API on top of the
worldgen biome. All three must respect the inverted-pure-read-wall
contract: written only by event-driven sites, read by views without
mutation.

### `world.Fertility` has exactly two production write sites

`BiomeDegradation.CatchUp` (called from `OnProductionTransition` which
in turn is called from `Extractor.ArmIfDormant` and the three
"going dormant" branches in `ProductionTickEvent.Apply`) is the
production-path mutator. The catch-up scope is **radius-bounded**:
`OnProductionTransition` iterates a Chebyshev box of size
`(2*DegradeRadius+1)^2` around the transitioning extractor — never
the whole world.

`Snapshot.ReadBiomeDegradation` is the restore-only mutator.

The test-only `BiomeDegradation.CatchUpWithRate` exists (no
production caller) — it's the integer-math driver for the Phase A
observation-independence tests. Internal accessibility limits its
callers to `Sim.Tests`. If a production code path ever needs to
write Fertility outside the transition discipline, that path is the
bug.

### `BiomeDegradation.FertilityAt` / `BiomeAt` are pure reads

Both compute fresh values from `world.Fertility` + `world.Structures`
+ `world.BiomeDegradationConfig` on every call. The 100×-no-mutation
contract is pinned by
`BiomeFertilityCatchUpTests.FertilityAt_IsPureRead_NoMutation` and
`BiomeAt_IsPureRead_NoMutation`. The same tests cover
`world.RememberedBiome` is not touched by reads.

### The implicit Desert latch is observation-independent

There is no stored "is this tile latched?" boolean. The latch
predicate is `(baseline + Fertility[tile].Deviation) < DesertThreshold`
evaluated against the stored value at the most recent transition. The
M9 invariant — that the rate at a tile is constant between transition
catch-ups — guarantees the predicate is exact and observation-
independent.

### `world.RememberedBiome` has the same write sites as `world.Explored`

`Sight.Reveal` writes both in lock-step. The `now` parameter on
`Reveal` carries the tick at which the biome snapshot is taken;
Genesis passes `now: 0` (no degradation yet at world genesis), event-
driven callers pass `sim.Now`. View reads `RememberedBiome[playerId]`
for explored-but-not-visible tiles; visible tiles fall through to
the live `BiomeAt(now)` instead.

### Snapshot format

`Snapshot.FormatVersion` bumped to `6`. New sections:
`BiomeDegradationConfig` (12 fields, fixed positional order),
sparse `Fertility` dict in canonical (y, x) order, and
`RememberedBiome` in (player-id, then y, x) order. No new scheduled
events to regenerate — Fertility is **pure derived state**, so
`RegenerateQueue.From` is unchanged. The mid-degradation round-trip
test (`BiomeDegradationTests.Degradation_SnapshotMidRun_RoundTrips`)
pins this.

## Movement cost & crowding addendum (post-M9)

Crowding cost (new pure-read primitives on `world.Units`) and a per-tile
unit cap (new rejection branches in `MoveArrivalEvent` / `GroupArrivalEvent`).

### `MovementCost.PlanCost` and `ExecutionCost` are pure reads

Both functions compute fresh values from `world.Units.Values`,
`world.Structures.Values` (via `Road.EffectiveCost` and `View.VisibleTiles`),
and the supplied `playerId` / `visibleTiles` parameters. Neither
mutates anything. The 100×-no-mutation contract is pinned by
`MovementCrowdingTests.CrowdingCountAndCost_Are_PureReads_NoMutation`.

A* calls `PlanCost` many times per `FindPath` query — the pure-read
invariant is what keeps path queries out of the simulation hash.

### Fog-aware planning is deterministic

`View.VisibleTiles(world, playerId)` is itself a pure read of world
state (sources × radius, no global sweep). Same world → same visible
set → same `PlanCost` per tile → same path. The fog-of-war contract
(planner sees through own-unit tiles, sees enemies only on visible
tiles) is a property of the cost function, not of any stored state, so
no new mutation site is introduced.

### Hard-cap rejection has bounded call sites

`MovementConstants.MaxUnitsPerTile` is enforced in exactly two events:

- `MoveArrivalEvent.Apply` — checks `CountUnitsOnTile(world, To) + 1`.
  On overflow, clears the unit's path anchors and idles the unit
  (`TrySetActivity(Activity.Idle)` bumps `AssignmentEpoch`, fencing any
  surviving queued events from the prior chain).
- `GroupArrivalEvent.Apply` — checks
  `CountUnitsOnTile(world, To) + group.Members.Count`. On overflow,
  clears the group's path anchors, sets `State = Idle`, and bumps
  `MovementEpoch` (same fencing role as the unit case).

The rejection paths never write `unit.Position` / `group.Position`
beyond the Idle transition — the unit/group stays where it was. No
position drift on a rejected arrival.

### Snapshot format

`Snapshot.FormatVersion` is **unchanged** at 6. Crowding is pure derived
state (read from `world.Units.Values`, which is already snapshotted),
and the hard cap is a behavioural threshold, not stored state. No new
field, no new event type, no `RegenerateQueue` change.

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
- **A new `Intent` subclass** — must (a) be added to
  `IntentJson.TypeNames` AND `IntentJson.Deserialize` with a frozen
  type-name, (b) be `[JsonConstructor]`-annotated for round-trip,
  (c) be exercised by `IntentStoreTests.RoundTrip_EveryIntentType` or
  the equivalent. Skipping any of these breaks durability silently
  (intent submitted live but unrecoverable from the log).
- **A new in-flight anchor on an entity** — must (a) be serialized in
  `Snapshot.cs`, (b) regenerated in `RegenerateQueue.From`, (c) cleared
  to its sentinel ("not in flight") value when the process completes,
  (d) be exercised by the M4 closure-gate test or an analogue. Anchors
  that aren't cleared on completion will trigger phantom events on
  next restore.
- **A change to `Snapshot.FormatVersion`** — bump the constant, write
  a one-paragraph entry in this audit describing what changed and the
  operator's migration path. The version refusal in `Snapshot.Restore`
  will already keep mismatched binaries from corrupting state; the
  audit captures the *intent* of the bump.

## Reference

This audit verifies the architectural claims in design doc §2.2
(event-driven core), §2.3 (one global queue), the persistence model's
in-flight-correctness-gap framing, and the back-pressure mechanism
shipped in Phase D. The audit doc itself is part of M1 Phase F.

## Update 2026-06-10 — military milestone (M14)

New mutation surfaces introduced by Barracks / Soldier / Archer /
equipment, audited against the trigger checklist above:

**`Unit.Buffs` writers (was: none — M7 scaffold):**

| Site | Purpose | Verdict |
|---|---|---|
| `Equipment/EquipUnitIntent.cs` (`Resolve`) | Adds the equip buff (modifiers baked from `EquipmentCatalog` at equip time) | Allowed — intent resolution |
| `Equipment/Equipment.cs` (`DropEquipmentToGround`) | Removes equipment-kind buffs; called from `CombatRules.OnUnitDeath` and `TrainUnitIntent.Resolve` only | Allowed — event/intent-driven, one shared rule |
| `Persistence/Snapshot.cs` (`ReadBuffs`) | Restore | Allowed — serialization |

`CombatRules.EffectivePower(unit, now)` lazily FILTERS expired buffs
but never prunes — pinned by
`CombatStatsTests.EffectivePower_IsPureRead_NoMutation` (100× +
hash-equal).

**`Barracks.Holdings` writers:** the existing haul deposit/pickup
events (Barracks is a plain `StorageStructure`) plus
`Equipment/CraftEquipmentIntent.cs` (`Resolve`) — withdraw inputs +
deposit item, fail-clean (all input checks precede any mutation,
pinned by `CraftEquipmentTests.Craft_InsufficientInput_Rejected_NothingMutated`).

**Checklist compliance:**

- Both new intents (`CraftEquipmentIntent`, `EquipUnitIntent`) are in
  `IntentJson.TypeNames` + `Deserialize` with frozen names,
  `[JsonConstructor]`-annotated, round-trip-tested
  (`IntentJsonTests`).
- `EquipUnitIntent` rejects grouped units (`unit.GroupId is not null`).
- No new in-flight anchors: crafting and equipping are instant; no
  `RegenerateQueue` change. Mid-fight recovery with equipped forces is
  pinned by
  `MilitaryDeterminismTests.MidFight_EquippedForces_SnapshotRoundTrip_Identical`.
- No `Snapshot.FormatVersion` bump: append-only enum values only
  (`Soldier=9`, `Archer=10`, `Sword=5`, `Bow=6`, `Shield=7`,
  `Barracks=12`); Barracks reuses the `StorageStructure` payload via
  kind-byte dispatch; `Unit.Buffs` was serialized since v4. Rationale
  comment sits on the constant.
- New global-iteration sites: none (`EquipUnitIntent` and
  `CraftEquipmentIntent` look up single entities by key;
  `DropEquipmentToGround` iterates one unit's buff list).

## Update 2026-06-11 — extraction claims (M15)

New mutation surface: the claim lists on `Extractor.ClaimTiles` and
`ConstructionSite.ClaimTiles` (get-only lists; contents-only mutation).
Exactly four writers, all at deterministic boundaries:

| Site | Purpose | Verdict |
|---|---|---|
| `Logistics/PlaceSiteIntent.cs` (`Resolve`) | Reserves the claim on the site (explicit validated list, or deterministic `Claims.AutoSelect`) | Allowed — intent resolution |
| `Logistics/BuildCompleteEvent.cs` | COPIES the site claim onto the finished extractor (AddRange of value coords — no aliasing, pinned by `BuildComplete_TransfersClaim_AsCopy_NotAlias`) | Allowed — event resolution; sites never produce → no rate change → no catch-up at transfer |
| `World/Structure.cs` (`ArmIfDormant` lazy fill) | Fills empty claims on hand-built extractors via the same AutoSelect | Allowed — runs only inside event/intent resolution against a COMPLETE world. Deliberately NOT in `GameWorld.AddStructure`: that runs during `Snapshot.ReadStructures` mid-rebuild, where an auto-claim could see different neighbor claims than the live run (hash divergence). Restored extractors carry claims and skip the fill. |
| `Persistence/Snapshot.cs` (`ReadClaimTiles`) | Restore | Allowed — serialization |

Pure-read surface: `Claims.Validate` / `AutoSelect` / `ClaimantAt` /
`ClaimantDegradeAmount` / `InBandClaimCount` — pinned by
`ClaimsHelperTests.Helpers_ArePureReads_NoMutation` (100× + hash).
`BiomeDegradation.DeriveRate` now sources degrade from
`Claims.ClaimantDegradeAmount` (MAX fold kept on principle — order-
independent even if the one-claimant invariant were violated);
`OnProductionTransition` catch-up scope is CLAIM-BOUNDED (≤ ClaimCount
tiles — tighter than the retired radius box; the radius scan
`MaxInRangeProducingDegradeAmount` is deleted).

`Snapshot.FormatVersion` 9 → 10 (claim lists on both carriers — the
format change the Extractor Phase-A note predicted). Closure gates:
`ClaimsDeterminismTests` (twin pipeline, mid-production restore, view
purity) + `ClaimExclusionTests.MidBuild_PendingClaim_SnapshotRoundTrip`.
`GenesisSpec` gained `BiomeDegradation` config (parallel to the other
three) so demos/tests pace degradation without touching defaults.

**Bug caught by the M15 closure gate** (fixed in the same milestone):
`Snapshot.WriteFertility` filtered out `Deviation == 0` entries on the
belief they "should not exist" — but those are the M9 transition
ANCHORS (deviation 0, lastUpdate = transition tick), explicitly
load-bearing per docs/biome-degradation.md. Dropping them made a
restored producing extractor over-apply its degrade rate across the
entire pre-snapshot history. The filter was SYMMETRIC, so round-trip
hashes looked identical while live and restored sims evolved apart —
exactly the failure class the mid-production headline test exists to
catch. Lesson recorded: serialize state FAITHFULLY; "should not exist"
beliefs belong in asserts, not silent filters.

## Update 2026-06-11 — M16 bandits

New mutation surfaces, all inside the event stream:

- `SpawnBanditPartyIntent.Resolve` — allocates `world.NextUnitId` per
  party member and adds units via `Population.OnUnitAdded` (the
  canonical runtime-add hook). Deliberately does NOT call
  `ScheduleLifespan`: bandits are age-exempt, and rolling lifespans here
  would consume RNG and shift every later demographic roll.
- `DespawnBanditPartyIntent.Resolve` — clears cargo (loot leaves the
  world; nothing drops) then removes each unit through
  `CombatRules.OnUnitDeath`, the M7 single death pipeline. Validates ALL
  party members before mutating ANY (atomic despawn).
- `Genesis.Build` — registers the bandit `Player` row unconditionally;
  rejects `FactionStartSpec` claiming the reserved id.
- `Sight.Reveal` — early-returns for the bandit owner (no Explored /
  RememberedBiome rows accrue for the faction; live sight via
  `View.VisibleTiles` needs neither).

NOT a mutation surface: the `BanditDriver` (Sim.Server). It reads
through pure-read walls (`View.VisibleTiles`, `BanditRules.*`) and acts
only by `SubmitIntent`. Its internal state (party tracking, RNG) is
ephemeral and outside the determinism contract — the durable intent log
carries its decisions. Closure gate:
`BanditDriverTests.Headline_ReplayFromIntentLog_HashesMatch` (live run
with driver vs. driverless replay of the round-tripped intent log —
hash-equal; replay interleaves submissions chronologically, matching
how live submission ordered Seqs).

No snapshot format change: bandit units/Player row serialize
generically; `UnitRole.Bandit` is an append-only enum byte.

## Update 2026-06-11 — M18 automation (standing orders)

New durable state: `GameWorld.StandingOrders` (+ `NextOrderId`), snapshot
FormatVersion 12. No new scheduled events, no new anchors —
`RegenerateQueue` untouched; recovery-clean by construction.

Mutation points (the full set — grep `StandingOrders` to verify):

- `SetStandingOrderIntent.Resolve` — creates an order (allocates
  `NextOrderId`, deep-copies steps so world state never aliases the
  transient intent's lists). Fail-clean: every check precedes any
  mutation, including the id allocation.
- `ClearStandingOrderIntent.Resolve` — removes an order (claims release
  implicitly; the exclusivity check scans orders).
- `AdvanceOrderCursorIntent.Resolve` — the ONLY writer of the cursor
  block (`Enabled` / `CurrentStep` / `StepEnteredTick` / `StepRetryCount`
  / `ActionDispatched`). Server-internal (wire-rejected by type in
  `GameHost.SubmitEnvelopeJson`), durable + replayed. Fenced on
  `CurrentStep == ExpectedStep` (the §2.6 stale-token discipline applied
  to a stale intent).

Pure-read surfaces:

- `Sim.Server.Automation.ConditionEvaluator.IsMet` — never writes;
  pinned by `AutomationEvaluatorTests.Evaluator_IsPureRead_NoMutation`
  (100× pattern). Reads only owner-visible state: structure-subject
  conditions require the tile in the owner's `View.VisibleTiles` set
  (fog contract).
- `IntentFactory.Create` — pure construction; every `ActionKind` maps
  1:1 onto an existing intent (no new sim semantics; the growth rule).

NOT a mutation surface: `AutomationDriver` / `OrderRunner` (Sim.Server)
— same contract as `BanditDriver`: pure reads in, ordinary durable
intents out, ephemeral brain (in-flight intent references only; cold
start resolves via a BumpRetry that re-gates on conditions). Canonical
arbitration: orders ascending by id, no RNG. Closure gate:
`AutomationHeadlineTests.Headline_ReplayFromIntentLog_HashesMatch`
(live driver run vs. driverless chronological replay — hash-equal).

No-global-iteration note: the driver walks `world.StandingOrders`
per think — bounded by the number of orders players have installed (there
is no per-player order count since 2026-10-02; staffing orders are implicit
per structure), not by world size, and it runs server-side outside the
sim's event stream. `View.VisibleTiles` is
computed once per owner per think, not per condition.

## Update 2026-06-16 — M21 water-restores-land + canals

Two coupled features: water proximity lifts the desert latch on degraded
land, and canals mutate land tiles into Water as a build job. Both ride the
existing M9/M15 spatial-lazy-field discipline — canal completion is just a
new rate-changing event. See `docs/canals.md`.

### `TileGrid` biome now has a SECOND post-construction write site

Until M21 the only post-worldgen biome writer was `Snapshot.ReadGrid`
(restore). M21 adds one event-driven writer:

| Site | Purpose | Verdict |
|---|---|---|
| `Logistics/BuildCompleteEvent.cs` (`CompleteCanal`) | `Grid.SetBiome(p, Water)` for each finished-canal path tile | Allowed — event-driven (canal `BuildCompleteEvent`), the one production terrain-mutation point |
| `Persistence/Snapshot.cs` (`ReadGrid`) | Restore | Allowed — serialization |

The mutated grid is captured by the existing full-grid snapshot
(`WriteGrid`/`ReadGrid`, one biome byte per tile) — canal water persists for
free, off the worldgen-generator/replay path (world-generation freeze rule
addendum, `docs/world-generation.md`).

### `world.Fertility` gains a third write site — the canal transition

`BiomeDegradation.OnWaterProximityChanged` (called only from
`CompleteCanal`, before the grid mutates) catches up every on-ladder tile
within `WaterRecoveryRadius` of a new water tile, under the OLD rate,
anchoring `lastUpdate = now`. This is the M9 transition discipline applied
to a new rate-changing event (water proximity). It calls the existing
`CatchUp` (so the mutator set stays `CatchUp` only) — scope is
**radius-bounded** (`(2*WaterRecoveryRadius+1)^2` per path tile), never a
global sweep. ORDER IS LOAD-BEARING: catch up → then `SetBiome` (the audit's
"anchor before you change the rate" rule). Pinned by
`WaterRestorationTests.OnWaterProximityChanged_AnchorsRecoveryAtTransition_NotRetroactively`
and the canal completion/recovery tests.

### `DeriveRate` latch-lift is a pure read over the grid

The latch branch now consults `WaterProximity.IsNearWater` (a bounded
Chebyshev grid scan — pure, no mutation). Water proximity is invariant
between rate transitions (the only thing that adds water is canal
completion, which catches up affected tiles), so the lazy field stays exact
and observation-independent. `FertilityAt`/`BiomeAt` 100×-no-mutation is
re-pinned by `WaterRestorationTests.NearWaterRecovery_FertilityAt_IsPureRead_NoMutation`.
The degraded-only guarantee (raw desert at deviation 0 does not green) falls
out of the pre-existing `storedDev < 0` recovery guard — no special case.

### Canal reservation is a pure-read scan, like claims

`CanalReservation.IsReserved` scans in-flight canal `ConstructionSite`s for
the tile in their `CanalPath` (mirrors `Claims.ClaimantAt`; same
O(sites×len) future-index note). No stored world collection — the
reservation IS the path on the site, so it round-trips for free and cannot
drift. Consulted (never written) by `PlaceCanalIntent`, `PlaceSiteIntent`,
and `Claims.ValidateOne`.

### Checklist compliance

- New intent `PlaceCanalIntent`: in `IntentJson.TypeNames` + `Deserialize`
  with frozen name `"PlaceCanalIntent"`, `[JsonConstructor]`-annotated
  (`List<TileCoord> path`), round-trips like `PlaceSiteIntent.ClaimTiles`.
- `StructureKind.Canal = 14`: append-only enum byte.
- New serialized field `BiomeDegradationConfig.WaterRecoveryRadius`
  (positional, defaulted) and `ConstructionSite.CanalPath` (written right
  after `TargetKind` so `ReadConstruction` reconstructs the length-scaled
  site before the build-duration drift check). `Snapshot.FormatVersion`
  15 → 16 (Phase A added the config field at 15; Phase B added the path at 16).
- No new scheduled-event type and no new anchor: a canal is an ordinary
  `ConstructionSite`, so `RegenerateQueue` regenerates its `BuildCompleteEvent`
  from the existing construction anchor. Mid-canal-build recovery is pinned by
  `CanalsTests.Canal_RecoveryAfterCrash_MidBuild_ReplaysIdentical`.
- New global-iteration sites: none beyond the bounded `IsReserved` /
  `IsNearWater` / `OnWaterProximityChanged` scans noted above.

Closure gate: `CanalsTests.Canals_TwinRun_HashesMatch` (build a multi-tile
canal → sail a boat through it → a degraded field beside it recovers →
twin-run hash equality).

## Update 2026-06-16 — M22 high-terrain visibility

The highest terrain band (Mountain) is revealed to every player's view from the
start (a race to the scarcest resources). See `docs/high-terrain-visibility.md`.

**No new mutation surface, no new sim state, no snapshot change.** The reveal is
entirely in the pure-read view projection and adds the mountain biome to the
returned view's `RememberedTerrain` copy only — never to `Explored`, never to any
`world.*` collection.

### `BuildPlayerView` stays a strict pure read

The reveal loop writes only to the freshly-built `remembered` dictionary that is
part of the returned `PlayerView`; it touches no world state. `Explored` is left
untouched on purpose — it gates the road overlay (`ViewProjector`), so keeping
mountains out of it prevents an enemy-road-on-un-scouted-peak leak. The headline
fog contract (`FogDeterminismTests.ViewsOff_HashEquals_ViewsOn`) and the pure-read
wall (`HighTerrainVisibilityTests.MountainReveal_BuildPlayerView_IsPureRead_NoMutation`,
100× no-mutation) both hold.

### `GameWorld.CommonKnowledgeTerrain` is non-serialized derived data

The set of mountain tiles is computed ONCE in the `GameWorld` constructor from the
frozen grid (`Biomes.IsCommonKnowledgeTerrain`). It is **not** written by any
`Snapshot.Write*` method and **not** part of the sim hash — a read-side memo of
immutable worldgen data, recomputed identically from the grid on restore. Mountain
tiles never change (canals only flood non-Mountain land; nothing else mutates the
grid), so the eager one-time computation is always valid. Because it is
unserialized, building a view (which reads it) cannot change the snapshot —
pinned by `HighTerrainVisibilityTests.MountainReveal_DoesNotAffectSnapshotHash_AndSurvivesRoundTrip`.

### Trigger note for this audit

If `IsCommonKnowledgeTerrain` is ever widened to a band that the sim can MUTATE
(e.g. if a future feature changes Mountain/Hills tiles at runtime), the eager
constructor-time cache would go stale — re-evaluate whether to recompute on the
terrain-mutation event (the way `OnWaterProximityChanged` handles canal floods) or
make the predicate read the live grid per call.

## Update 2026-06-16 — M23 loot caches

Unowned `Cache` structures (owner `-2`) scattered into the genesis fog, looted
with `LootCacheIntent`. See `docs/loot-caches.md`. No new scheduled-event type
and no new anchor — caches are ordinary structures in the snapshot →
recovery-clean by construction (`RegenerateQueue` untouched).

### Genesis scatter consumes the seeded `Rng` deterministically

`CacheScatter.Scatter` runs once in the `Simulation` spec-ctor, AFTER the
lifespan rolls, using the sim's owned `Rng`. Determinism rests on three things:
candidate tiles enumerated in canonical `(y, x)` order; a partial Fisher-Yates
draw over `Rng.NextInt`; loot rolled in a fixed draw order (primary resource,
amount, gear chance, gear pick). It reads — never writes — every player's
genesis `Explored` set for the fog gate. **`CacheConfig.Count` defaults to 0**,
making the scatter a pure no-op that consumes zero `Rng` for every existing
scenario, so their hashes are bit-identical (proven by the unchanged 749-test
baseline and `Scatter_Count0_ProducesNoCaches`). Headline:
`CachesTests.Caches_TwinRun_HashesMatch` (twin genesis → equal hash) +
`Scatter_EveryCache_IsUnseenByAllPlayers_AtGenesis` (the spawn-in-fog invariant).

`CacheConfig` lives on `GenesisSpec`, NOT on `GameWorld` — it is a genesis-time
input, and only the RESULTING `Cache` structures are serialized. Restore loads
those structures and never re-scatters (recovery anchors on the snapshot, not
genesis — see the M4 addendum), so the config is correctly absent from the save.

### The cache fog/loot surfaces

- `Cache` is a `StorageStructure`; it serializes through the existing
  `WriteStorage`/`ReadStorage` (capacity from the catalog — stable, drift-checked
  — Holdings as the loot). `Snapshot.FormatVersion` 16 → 17 (new structure kind
  in the serialized surface; old binaries reject cache-bearing snapshots cleanly
  via the version gate). Append-only enum byte `StructureKind.Cache = 15`.
- `LootCacheIntent` is the only cache mutator outside scatter/restore: it
  withdraws cargo-capped from the `Cache` on the unit's tile and removes the
  cache when emptied. Fail-clean (all preconditions precede any mutation), same
  shape as `LoadCargoIntent`. Registered in `IntentJson` (frozen name
  `"LootCacheIntent"`, `[JsonConstructor]`), round-trip covered by the
  persistence suite.
- The unowned owner `-2` is what makes the view behavior free: `BuildPlayerView`
  surfaces a structure only when `owner == viewer || tile visible`, so a cache
  shows on sight and vanishes in fog with no new code. `ViewProjector` reveals a
  VISIBLE cache's Holdings (so the player can name the loot) — the M15
  "visible structure's contents are public" exception; it is a pure read, never
  Remembered, so the contents fog with the cache.

No new global-iteration on a timer: the scatter is a one-time O(W·H) genesis
candidate scan (like worldgen), not a per-tick sweep.

## Update 2026-06-16 — the cart (first non-combat buff)

A cart is a `Buff` that adds carry capacity and slows movement (`docs/cart.md`).
It adds two modifier dimensions to `Buff` (`CargoModifier`, `MoveCostPercent`)
and two live rollups. No new entity, event, or anchor → recovery-clean.

### The move-cost rollup is a pure-read, on the execution side only

`MoveIntent.ScheduleNextHop` scales the ground-truth `ExecutionCost` by the
unit's summed `MoveCostPercent` before scheduling the arrival:
`hopCost * (100 + slow) / 100` (long intermediate, `Impassable`-guarded, integer).
It reads `unit.Buffs` and writes nothing but the (already-existing) arrival
anchor. A* planning (`PlanCost`) is **not** touched — a uniform multiplier
doesn't change the route — so the determinism-sensitive pathfinder is untouched.
`slow == 0` (no move-cost buffs) leaves the cost identical → every existing move
is bit-for-bit unchanged (`CartTests.NoCart_Movement_Unchanged`). The scaled cost
bakes into `NextArrivalTick`, so mid-move recovery is correct for free, and
equipping requires Idle so no hop is ever half-scaled.

`Unit.CargoCapacity` becomes role value + summed `CargoModifier` (clamped ≥ 0) —
still a pure derived getter, no stored field.

### Snapshot format

`Snapshot.FormatVersion` 17 → 18: `WriteBuffs`/`ReadBuffs` now carry the two new
ints per buff (existing weapon buffs serialize them as 0, unchanged behavior).
`Resource.Cart = 8` is an append-only enum byte; it rides the existing
holdings/cargo/ground-pile serialization with no new code. Round-trip pinned by
`CartTests.Cart_RoundTripsThroughSnapshot_PreservesModifiers`; the cart drops as
an item via the existing `Equipment.DropEquipmentToGround` (one shared rule).

## Update 2026-07-05 — M25 Rival: offensive AI (zero new sim surface)

The Rival is Sim.Server brain work end to end: **no new Sim.Core mutation
points, no new events, no new anchors, no snapshot format change.** Every
lever the Rival pulls is a pre-existing player intent (`DeclareWarIntent`,
`ProposeRelationshipIntent`, `RespondToProposalIntent`, `MoveIntent`,
`TrainUnitIntent`, `LoadCargoIntent`, `UnloadCargoIntent`) resolving through
the standard `IntentEvent` wrapper.

### FillDiplomacy is a pure-read wall

`ViewProjector.FillDiplomacy` copies `world.Players` (id + `Defeated`) and
`world.Diplomacy` (relationship rows, pending wars, the viewer's incoming
proposals) onto the wire DTOs. Reads public getters only; writes nothing.
Pinned by `RivalTests.Wire_Diplomacy_IsPureRead` (300 projections across
three viewers, `Snapshot.Hash` unchanged) — the same discipline as
`View.BuildPlayerView` (M3 Phase F) and `Road.ConditionAt`.

### Driver replay re-proven with three driver kinds interleaved

`RivalTests.Rival_ReplayFromIntentLog_HashesMatch` runs bandits +
Homesteader + Rival on one clock loop for 15 game-days (war declared,
telegraph, mobilization), then replays the durable intent log driverless in
chronological same-tick batches (the docs/bandits.md interleave rule) into a
fresh sim: `Snapshot.Hash` equality. AI memory (enemy intel, campaign
roster, raid party) stays droppable-hints-only — the durable log carries
every decision, so the replay needs no brain.

## Update 2026-07-06 — M26 walls & gates (first movement-blocking structures)

Fortifications add two pure-read predicates, one new intent, and a new
consumer of the existing combat anchor — **no new anchors, no snapshot
format change** (Wall/Gate serialize as field-less kinds on the common
header, like Tower/Rubble).

### `Fortification.BlocksMover` / `BlocksPlan` are pure reads

Both are a single `world.Structures` lookup + spec read + (for gates) a
`Diplomacy.RelationshipBetween` read. `BlocksPlan` is called by A* many
times per query inside `MovementCost.PlanCost`; `BlocksMover` per hop at
schedule time (`MoveIntent.ScheduleNextHop`, `MoveGroupIntent
.ScheduleNextHop`) and at fire time (`MoveArrivalEvent`,
`GroupArrivalEvent`). Pinned by
`WallsAndGatesTests.BlockingChecks_ArePureReads` (100×-no-mutation).

The fog split mirrors crowding: `BlocksPlan` counts a blocker only when
owned by the planner or on a currently-visible tile; the ground-truth
checks are visibility-blind. Same-visible-set ⇒ same plan, so the M3 fog
headline (view spam never touches the hash) is untouched.

### Blocked-hop yields have bounded call sites

Four, all mirroring the M2 hard-cap rejection byte for byte (clear
movement anchors → Idle/epoch semantics): the two hop schedulers (early
exit before scheduling a doomed arrival) and the two arrival events
(a wall can complete, or a gate turn hostile, during the hop). No other
code path interprets blocking.

### Fort sieges reuse the M7/M24 combat anchor unchanged

`FortSiege.MaybeBeginSiegeAdjacentTo` (called from final arrivals and
blocked-hop yields) writes `world.CombatStates[fortTile]` with the same
`NextRoundTick/NextRoundSeq` fencing token; `RegenerateQueue` rebuilds
mid-siege rounds with zero new code (pinned by
`WallsAndGatesTests.MidSiegeSnapshot_RecoversAndFinishesIdentically`).
`FortSiege.TryResolveFortRound` computes damage as an order-independent
SUM over `world.Units.Values` (owner-hostility + 4-adjacency filter), so
dictionary iteration order cannot leak into the hash — the same argument
as `CombatRules.ForcePower`.

### `PlaceWallIntent` is atomic-validate, then N ordinary sites

All validation (bounds, land, structure/claim/canal-reservation
exclusion, distinctness, 4-connectivity) runs before any mutation
(fail-clean); on success it adds N standard `ConstructionSite`s — no new
build machinery, no reservation system (the sites themselves occupy the
tiles). Registered in `IntentJson` as durable type-name
`PlaceWallIntent`.

### Headline

`WallsAndGatesTests.Walls_TwinRun_HashesMatch` — intent-built wall line →
fog-blind march bonks → adjacency siege → breach (Rubble) → march through,
twice, `Snapshot.Hash` equality.

### M26 addendum — rubble clearing + the raze rate-transition fix

`ClearRubbleIntent` is a resolution-time swap (Rubble out, a clearing
`ConstructionSite` in) with no new anchors — the site rides the existing
build machinery and `RegenerateQueue` path. `BuildCompleteEvent` gained a
second no-structure branch (TargetKind == Rubble → tile left empty),
shaped exactly like the canal's.

`SiegeDamage.RazeStructure` now calls
`BiomeDegradation.OnProductionTransition` before removing a razed
extractor — razing is a RATE-CHANGING event for the claim tiles, and the
§2.5 anchor discipline requires the old-rate catch-up before the world
mutates (the same rule as production stop and canal flooding; the missing
call was a latent M24 gap). Pinned by
`ReclaimTests.RazingAProducingFarm_AnchorsItsSoilDamage`.

### M27 addendum — irrigation (WaterRecoveryAmount)

`DeriveRate`'s recovery branch now selects `WaterRecoveryAmount` inside
`WaterRecoveryRadius` — still a PURE READ (the added water scan mirrors
the latch-lift branch's, and is skipped entirely when the knob equals
`RecoveryAmount`). Rate-invariance between transitions holds unchanged:
water proximity only changes at canal completion, whose
`OnWaterProximityChanged` catch-up anchors affected tiles before the grid
mutates — the boosted rate rides the SAME transition event. Snapshot
`FormatVersion` 19→20 (one config int). Pinned by the Irrigation section
of `WaterRestorationTests` (boost math, radius boundary, 100×-no-mutation,
config round-trip). The AI side (IrrigateRung) adds no sim surface: one
ordinary durable `PlaceCanalIntent` per project, planned from the view.

### M28 addendum — boat freight (the quay warehouse)

The Dock becomes a `StorageStructure`; its holdings are ordinary
snapshotted state (the storage payload, written between the slip and the
production anchors — `FormatVersion` 20→21). `HaulStops.MoveTarget` /
`AtStop` / `OwnDockBySlip` are PURE READS (dictionary lookups), consumed
by `HaulIntent` resolution and the haul events to route a `Traversal.Water`
carrier through dock slips. No new anchors and no new events: a boat haul
uses the same `HaulPlan` + `MoveArrivalEvent` + `HaulPickup/Deposit`
orchestration as any land haul, so `RegenerateQueue` reconstructs a
mid-sail voyage from the boat's existing move anchors with zero new code.
Pinned by `BoatFreightTests` (twin-run, snapshot-mid-sail recovery,
quay-holdings round-trip). Foot-hauler behavior is byte-identical — the
`AtStop`/`MoveTarget` helpers return `tile` unchanged for `Traversal.Foot`.

### Battlefield-salvage addendum (2026-07-13) — piles on the wire + the salvage rung

Zero new sim mutation points. `ViewProjector.FillPiles` is a PURE READ of
`world.GroundResources` (visible tiles only, deterministic (Y, X) order,
rows by resource id — the inner map is a SortedDictionary), pinned by the
100×-projection hash check in `SalvageTests`. The projector's new
`GraveSource` hook relocates the grave attachment from `BuildViewJson`
into `Project` so AI brains see the same markers; `GraveTracker.Project`
mutates only host-tier per-player seen sets (presentation state — never
sim state, never hashed), and every caller holds the host lock exactly as
before. `SalvageRung` emits ordinary durable intents (`MoveIntent`,
`LoadCargoIntent`, `UnloadCargoIntent`) planned from the view — the same
replay story as every other rung.

### Dead-kingdoms addendum (2026-07-13) — spill on raze, hostile-to-all, extinction

Three new mutation points, all inside the existing event pipeline (no new
events, no snapshot format change): (1) `SiegeDamage.RazeStructure`
merges the razed structure's holdings/buffer/delivered into
`world.GroundResources` — already-snapshotted state, written from
`CombatRoundEvent.Apply` exactly like the structure swap beside it;
(2) `PlayerDefeatedEvent.Apply` writes Enemy relationship rows for the
fallen faction against every other registered id (deterministic id
order) and clears pending-war anchors — stale `WarBecomesEffectiveEvent`s
fence on their stored seq as before; (3) `CombatRules.OnUnitDeath`
schedules a `PlayerDefeatedEvent` at `sim.Now` when the owner's
`PopulationCount` hits zero — same-tick, never crosses a snapshot, and
idempotency-fenced against the castle-razing path. All three are
functions of world state already in the replay stream; `ScavengeTests`
pins the rules and the 921-test suite (884 + 37 persistence) is green.

## Rivers addendum (2026-09-17) — an edge cost, and a second terrain grid

`docs/rivers.md`. Zero new sim mutation points and zero new scheduled
events. The river mask (`TileGrid.RiverEdgesAt`) is written by exactly two
sites — `Genesis.Build` (from `GenesisSpec.Rivers`, symmetry-validated) and
`Snapshot.ReadGrid` (restore) — and is never touched afterwards: rivers are
terrain, like the biome grid, and the sim cannot carve or dam one.

### River reads are pure

| Reader | Reads | Writes? |
|---|---|---|
| `River.EdgeBetween / Opposite` | nothing (pure geometry) | No |
| `River.Crosses / CrossingCostFor` | `grid.RiverEdgesAt` | No |
| `MovementCost.TerrainCostFor` (Foot) | `Road.EffectiveCost` + `River.CrossingCostFor` | No |
| `MovementCost.PlanCost / ExecutionCost / Planner` | as above | No |
| `Pathfinding.FindPath` (via the edge `costFn`) | as above | No |
| `ViewProjector.FillHop` (client hop animation) | `ExecutionCost` | No |

Pinned by `RiversTests.RiverReads_ArePureReads_NoMutation` (100× hash
check across `Crosses`, `CrossingCostFor`, `PlanCost` and a full A* query).

### The cost delegate is now an edge cost

`Pathfinding.FindPath` takes `Func<TileCoord, TileCoord, int>`; the
tile-cost overload wraps it. `MovementCost.Planner(...)` is the single
factory the three movement intents use, so the audit surface for "what
does A* read" is one function. Costs only ever increase with the river
term, so the Manhattan heuristic stays admissible and path determinism
is unchanged (twin-run on a generated world with rivers:
`RiversTests.GeneratedWorld_TwinRun_HashesEqual`).

### Snapshot v30

One byte per tile after the biome grid, same `(y, x)` order, hashed.
Round-trip pinned by `RiversTests.Snapshot_RoundTripsTheMask_AndHashesIt`,
which also proves two worlds differing only in rivers hash differently.

### Generation is off the replay path

`RiverCarver` runs inside `MapGenerator.Build` — float math, once, frozen
to a `RiverEdge[,]` before the sim sees it, exactly like `NoiseField` and
`ContinentShaper`. Ties in the priority flood and the source pick are
broken by `(y, x)`, so same config ⇒ same rivers
(`RiversTests.Generated_SameConfig_SameRivers`).

## Update 2026-09-17 — refining structures (docs/refining-structures.md)

### New mutation surface: `Extractor.Inputs`

The refiner input store on `Extractor` (only ever non-empty for
`Spec.IsRefiner` kinds — today the Smelter) has exactly two writers:

| Site | Mutation | Trigger |
|---|---|---|
| `Extractor.DepositInput` | adds an input | `CargoTransfer.DepositInto` (haul deposit / manual unload), same shared primitive every other deposit uses |
| `Extractor.ConsumeBatches` | pays `batches × InputCost` | `ProductionTickEvent.Apply`, refiner branch, after the dormancy guards |

`SiegeDamage.RazeStructure` reads it to spill (then the structure is gone).
`Snapshot` reads and restores it (v31, enum-ordinal order from the
`SortedDictionary`). `ViewProjector` reads it into the structure DTO's
holdings. No other site touches it.

### Re-arm: one more caller of `Extractor.ArmIfDormant`

`CargoTransfer.DepositInto` now calls `ArmIfDormant` after a successful
refiner deposit — the deposit-side twin of the haul-pickup re-arm. It is
the same idempotent entry point; the gate is the new `Extractor.CanProduce`
(workers, buffer room, and for refiners ≥ 1 affordable batch), which
`ArmIfDormant`, the tick's reschedule decision, and `AssignWorkersIntent`
all read. An empty smelter with workers therefore never arms a tick that
would only fire to go dormant.

### Snapshot v31

Extractor payload: input count + `(byte resource, int amount)` pairs after
the claim list. Pinned by `RefiningTests.Snapshot_RoundTrips_ArmedAndDormantSmelters`
(hash equality before and after one production period on both sides).
Twin-run: `RefiningTests.TwinRun_SmelterChain_HashesEqual`.

## Update 2026-09-18 — M34 roads on edges (docs/roads-on-edges.md)

Road condition moved from a tile key to an ARC key (`TileEdge`, the lane
between two adjacent tiles). The three M2 properties above hold unchanged
with the key swapped; re-verified by grep after the port:

- **No global iteration.** `world.Roads` is still touched only by
  `Roads/Road.cs` (targeted reads and writes by arc), `Snapshot.cs`
  (canonical order is now `(A.y, A.x, axis)`), `ViewProjector` (three view
  builders iterate the sparse set, bounded by road count, pure reads via
  `ConditionAt`), `Sim.Host/Program.cs` (smoke print) and one new bounded
  caller: `BuildCompleteEvent.CompleteCanal` removes the four arcs
  incident to each flooded tile (`TileEdge.Around`), inside an event.
- **One mutation point, now two call sites of the same event family.**
  `Road.CreditTraffic(world, from, to, now)` is called from
  `MoveArrivalEvent.Apply` and `GroupArrivalEvent.Apply`, both after the
  position update, both with the tile left and the tile entered. The
  group site existed before (it credited `To`); it now credits the arc
  each member walked. A non-adjacent pair credits nothing.
- **Pure-read wall.** `Road.EffectiveCost(world, from, to, now)` and
  `Road.ConditionAt(world, edge, now)` write nothing; `from == to` returns
  plain terrain without touching the map. `FormGroupIntent`'s reachability
  check no longer prices roads at all (plain `Grid.TerrainCost`).
  Pinned by `RoadsOnEdgesTests.Reads_ArePure_100x` alongside the existing
  `Pathfinding_IsPureRead_NoRoadMutation`.

No new anchors, no new scheduled events. Snapshot **v32** (road block
gains an axis byte per arc); v31 streams are rejected by the existing
version check. `RoadConstants` untouched: a route of N hops credits N arcs
as it credited N tiles, so gain and decay tuning carry over.

## M35 addendum (2026-09-20) - environmental fertility

`EnvironmentalFertility.Offset`, `WaterProximity.DistanceToWater` and
`ForestDepth` are PURE reads over `TileGrid` (biome + river mask); pinned by
the 100x-no-mutation pattern. No new stored state: the per-tile baseline is
derived, so snapshot round-trip is unchanged apart from the config block
(FormatVersion 33 carries the knobs). The canal branch of
`BuildCompleteEvent` remains the ONE event that changes water proximity and
forest depth; its affected set widens to the union of the recovery and
fertility radii. `world.Fertility` still has exactly two production write
sites. Filled in per phase in `docs/m35-status.md`.

## M36 addendum (2026-09-23) - haul queue and named routes

`docs/hauling-queue-and-routes.md`. Snapshot **v35**.

**New stored state and its mutation points.**

| State | Written only by |
|---|---|
| `Unit.Cargo` (mixed resource bag) | the cargo verbs: `HaulPickupEvent`, `HaulDepositEvent`, `Load/UnloadCargoIntent`, `CacheLooting`, `ServeRouteStopIntent`, the death drop in `CombatRules.OnUnitDeath`, `DespawnBanditPartyIntent` (the same sites that wrote the old resource/amount pair, plus route stops) |
| `HaulPlan.Amount`, `HaulPlan.JobId` | `HaulIntent` (init-only after) |
| `GameWorld.HaulJobs`, `NextHaulJobId`, `NextHaulStamp` | `SetHaulJobIntent`, `ClearHaulJobIntent`, `RequeueHaulJobIntent`; `HaulDepositEvent` credits a Once job's `Delivered` and removes it when met |
| `GameWorld.HaulRoutes`, `NextHaulRouteId` | `SetHaulRouteIntent`, `ClearHaulRouteIntent`, `Add/RemoveRouteCrewIntent`; a crew's `CurrentStop` only by `ServeRouteStopIntent` |
| `Unit.RouteId` | `AddRouteCrewIntent`, `RemoveRouteCrewIntent`, `ClearHaulRouteIntent` |

**Canonical order.** `CargoHold` is a `SortedDictionary` by `Resource`
(snapshot rows and death drops iterate by enum ordinal). Jobs and routes
serialize in id order; a route's stops, rules and crews in list order (rule
order is application order; crews are ascending id). The queue ORDER is
not written: it is `(QueueStamp, JobId)`, so restoring the jobs restores
the line.

**No new anchors, no new scheduled events.** Queue work rides the existing
`HaulPlan` anchor; route crews walk with ordinary `MoveIntent`s. The driver
(`Sim.Server/Hauling/HaulingDriver`) holds no sim state: in-flight amounts
are read from haulers' plans each think, so a fresh driver after restore
agrees with the old one (pinned by `HaulQueueTests` / `HaulRouteTests`
`SnapshotMid*_RecoversToTheSameWorld`, which continue both worlds with
fresh drivers).

**Server-internal intents.** `RequeueHaulJobIntent` and
`ServeRouteStopIntent` are wire-rejected in `GameHost.SubmitEnvelopeJson`
and durable in the intent log. `ServeRouteStopIntent` serves and advances
in one resolution (fenced on the stop the driver saw), so a restart cannot
serve a stop twice.

**Dormancy.** `ClaimLedger.IsDormant` gains `RouteId is null`: a route
crew is never pulled by a queue job or an automation order. AI units never
carry a `RouteId`, so AI behavior is unchanged.

## M37 addendum (2026-09-23) - progression, Phase A (docs/progression.md)

**New mutation surface: `Player.Progress` (the `ProgressLedger`).**

| State | Written only by |
|---|---|
| `Player.Progress` (null or a ledger) | `Genesis.Build` from `FactionStartSpec.Progression`; `Snapshot` restore |
| `ProgressLedger.Counts` | `Progression.Bump`, called from `TrainingRules.Train` (RoleTrained), `Construction.Complete` (StructureCompleted), `ProductionTickEvent` on a refiner (ResourceRefined) |
| `ProgressLedger.Fired` | `Progression.Fire`, reached only through `Progression.Check` |

**Checks run inside the sim.** `Progression.Check` runs after every bump and
wherever a gauge may move: `Population.OnUnitAdded` (population) and each
`Sight.Reveal` made from a sim event or intent (`MoveArrivalEvent`,
`GroupArrivalEvent`, `DisembarkIntent`, `Construction.Complete` and the canal
flood). Genesis reveals do not check (no sim yet). A check fires rows in
catalog order and repeats until none fires, so `Fired(id)` chains resolve in
the same call regardless of row order.

**Effects are deterministic.** No RNG draw, no wall clock. `Grant` deposits
into the castle through `CargoTransfer.DepositInto`. The effects that will
place things in the world (omens, Phase B) hash a (world seed, omen id) pair
instead of drawing from the world RNG.

**Canonical order.** Counts are a `SortedDictionary` keyed by
`(Stat, Sub)`; fired ids a `SortedSet`. Snapshot v36 writes the ledger in the
player row after `GodMode`: a has-ledger bool, the count rows in key order,
then the fired ids ascending.

**No anchors, no scheduled events** in Phase A. The catalog is code and is
not serialized; a row's id is, so ids are append-only.

### M37 Phase B (2026-09-23) - omens

| State | Written only by |
|---|---|
| `GameWorld.ProgressionConfig` (+ derived `Milestones`) | `Genesis.Build`; `Snapshot` restore |
| `GameWorld.Omens`, `NextOmenId`, every `Omen` field | `Omens.Raise` (from `Threat.Apply`), `Omens.Arrive` (from `OmenDueEvent`), `Omens.OnRaiderEscaping` (from `DespawnBanditPartyIntent`), `Omens.OnUnitRemoved` (from `Population.OnUnitRemoved`) |

**Anchor:** a Pending omen's `(DueTick, DueSeq)` is its `OmenDueEvent`, regenerated in
`RegenerateQueue` (omens in id order, after the food homes). Liveness is `State`, not the
Seq: Seq 0 is a real Seq (found in `OmenTests.ASnapshotMidCountdown_ArrivesTheSame`).
A slip reschedules and moves the anchor; the event fences on `(At, Seq)`.

**No RNG.** The bearing is a fixed scan (wildness count per octant) with a hash
tie-break; the arrival tile is a fixed ring scan in hash order. Raiders are created by
`SpawnBanditPartyIntent.Materialize`, which (as before) skips the lifespan roll.

**Driver state stays disposable.** The bandit driver recognises omen raiders from
`Omens.RaidOf` (durable), so a restarted driver still marches them on the seat.

**Snapshot v37** appends the config knobs, `NextOmenId` and the omens (id order, party
ids in list order).

### M37 Phases C–D1 (2026-09-23) - arrivals, rumours, the wire

- **Omens are never removed.** A resolved omen keeps its outcome state and
  `ResolvedTick` (snapshot v37 carries both). `Omens.RaidOf` and the regenerate pass
  filter on state.
- **New writers:** `Omens.OnCacheGone` from `CacheLooting.TryLoot` (which now takes the
  `Simulation` for the tick); newcomers are added through `Population.OnUnitAdded`, roll
  their lifespan with `Population.ScheduleLifespan` (a world-RNG draw, in the sim's own
  event order, as for any birth) and start walking with `MoveIntent.BeginMove`; a
  rumour's ruin is an ordinary `Cache` added with `world.AddStructure`; the war chest
  drops through `CargoTransfer.DropToGround`.
- **The projector's `FillOmens` is a pure read** (`OmenWireTests.ProjectingOmens_IsAPureRead`).

## M38 addendum (2026-09-23) - scouting secrets (docs/scouting-secrets.md)

| State | Written only by |
|---|---|
| `GameWorld.Charts` | `Charts.Deliver` (ScoutMissionRunner at `Returned`), `Charts.OnSight` (`Sight.AfterReveal`, `Construction.Complete`'s reveals), `Charts.OnSecretGone` (`CacheLooting.RemoveIfEmptied`, `Idols.Activate`) |
| `GameWorld.IdolConfig` | `Genesis.Build`; `Snapshot` restore |
| `GameWorld.VisionGrants`, `NextVisionGrantId` | `Idols.Activate` (from `ActivateIdolIntent` or the `ActivateIdol` goal's arrival), `Idols.Expire` (from `VisionGrantExpiryEvent`) |
| `Idol` structures | `IdolScatter` at genesis (sim RNG, after the caches); removed by `Idols.Activate` |

**RNG draws** (in the sim's event order, replayed exactly): the genesis idol scatter;
an idol's circle centre; a rumour's search-area offset (moved off a public hash, which a
modded client could invert).

**Vision equivalence.** `View.VisibleTiles`, `View.Sees` and
`BanditRules.IsSeenByAnyPlayer` all include vision grants.

**Anchor.** A grant's `(EndsTick, EndSeq)` is its expiry event, regenerated in
`RegenerateQueue`.

**Snapshot v38** appends the charts, the idol config, `NextVisionGrantId` and the
grants; an `Idol` structure's payload is its grade byte.

**Consolidation.** Every sim-side reveal now calls `Sight.AfterReveal` (charts, then
progression) instead of `Progression.Check` directly; `Construction.Complete` keeps its
own order because its progression bump already runs the check.

## M39 addendum (2026-09-24) - bandit camps (docs/bandit-camps.md)

| State | Written only by |
|---|---|
| `BanditCamp` structures | `Camps.Build` (from `Omens.RaiseCamp`, the `CampRumour` effect); removed by `SiegeDamage.RazeStructure` |
| `BanditCamp.Raiders`, `RaidDeparted`, `LastRaidTick`, `LastRecruitTick`, tick anchor | `Camps.Tick` (from `CampTickEvent`), `Camps.OnUnitRemoved` (from `Population.OnUnitRemoved`), `Camps.OnMoved` (from `MoveArrivalEvent`) |
| The hoard (`Holdings`) | raiders' `UnloadCargoIntent` (bandit-issued, the ordinary deposit path); the raze spill |
| `GameWorld.CampConfig` | `Genesis.Build`; `Snapshot` restore |
| `ProgressStat.CampRazed` | `Camps.OnRazed`, called from `CombatRoundEvent` right after the raze, crediting every hostile non-bandit owner in the round's start forces |

**Anchor:** a camp's `(NextTickAt, NextTickSeq)` is its `CampTickEvent`, regenerated in
`RegenerateQueue` in (y, x) order.

**RNG:** a camp's placement is a fixed ring scan in hash order (as the ruin's). Its
search circle's offset is a sim RNG draw (as the ruin's). Recruits are materialized
without a lifespan roll (bandits die by the sword).

**Snapshot v39** adds the camp payload (hoard, target, source, raiders, departed flag,
last raid / recruit, anchor), the `CampConfig` block after the idol grants, and two
`ProgressionConfig` knobs (`SmokePopulation`, `CampCaptives`).

## M40 addendum (2026-09-24) - salvage (docs/salvage.md)

| State | Written only by |
|---|---|
| a Salvage `HaulJob` | `SetHaulJobIntent` (validated by `Salvage.Blocker`, which reads the player's chart and `View.Sees`: both pure reads); removed by `ClearHaulJobIntent` or by `HaulPickupEvent` when a salvage hauler arrives to nothing |
| a cache's holdings / a ground pile | `Salvage.TakeAll` from `HaulPickupEvent` (cache first, then pile, resource order); the cache's removal still goes through `CacheLooting.RemoveIfEmptied` |

The driver never reads a salvage source; its report counts haulers from their plans. No
snapshot change: the job kind is a new byte value in an existing field, and a salvage
`HaulPlan` carries `Resource.None`.

## Two-act pacing addendum (2026-09-24) - the landing (docs/two-act-pacing.md)

| State | Written only by |
|---|---|
| `GameWorld.LandingConfig` (the landing tick; 0 = a one-act world) | `Genesis.Build`; `Snapshot` restore |

**Pure reads:** `LandingRules.HasLanded` and `LandingRules.TruceHolds` read the config
and the clock and write nothing (pinned by `LandingTests.LandingRules_ArePureReads`).
`DeclareWarIntent` rejects while the truce holds, which consumes a `Seq` like any
rejection and mutates nothing.

**The host's pace is not sim state.** `Sim.Server`'s `PaceSchedule` switches the tick
rate at the landing tick; nothing in `Sim.Core` reads it, and it never enters the
replay log.

**Snapshot v40** adds a trailing landing block: the `LandingConfig` tick.

### Update 2026-09-25 — the landing's hosts

| State | Written only by |
|---|---|
| `GameWorld.LandingSeq` (the pending `LandingEvent`'s anchor) | `LandingRules.ScheduleAtGenesis` (Simulation spec-ctor); cleared by `LandingEvent.Apply`; `Snapshot` restore |
| `GameWorld.LandingHosts` (hosts, bands, rosters) | `LandingRules.Land` (from `LandingEvent`); rosters shrink only in `LandingRules.OnUnitRemoved` (from `Population.OnUnitRemoved`); `Snapshot` restore |

**Anchor:** `(LandingConfig.Tick, LandingSeq)`, regenerated in `RegenerateQueue`.
**RNG:** none. Sides are ordered by an integer hash, and landing tiles by
`Omens.Search`'s hash ring order. The bandits are materialized without a lifespan
roll (the bandit rule).
**Pure read:** `LandingRules.IsHostBound`. The driver's release set is ephemeral
and never hashed.

## M41 addendum (2026-09-25) - the battlefield grid (docs/battlefield-grid.md)

| State | Written only by |
|---|---|
| `GameWorld.Battlefields` (turn anchor, suspended flag, turn number) | `Battlefields.Open`, `RunTurn`, `Wake`, `Close`; restored by `Snapshot.ReadBattlefields`; the turn event is rebuilt from the anchor by `RegenerateQueue` |
| `Unit.Board` (subtile, came-from, standing order, last note, sheltered) | `Battlefields` (open, admit, turn, leave, close) and `SetBattleOrderIntent`; cleared on death by `Battlefields.Apply` |
| `Unit.Doctrine` | `SetBattleDoctrineIntent` |
| `Unit.EnteredFrom` / `EnteredTick` / `LeavingBoard` | `MoveArrivalEvent` and `GroupArrivalEvent` (every hop); `Battlefields.Leave` sets `LeavingBoard` |
| `CombatConfig.Model`, `LineSupport` | genesis; restored by `ReadBattlefields` |

- **Pure reads:**
  - `TurnPlanner`, `TurnResolver` and `BattlePathing` work on a snapshot of the board and
    return values.
  - `BattlefieldProjection` and `Battlefields.PlanPreview` build the same board for the view
    and write nothing.
  - `Battlefield.LastTurn` is presentation only: not snapshotted, not hashed.
- **Order independence:** a turn's steps resolve together by the collision table. Ties
  between friends go to the lower unit id, never to intent order (`TurnResolverTests`).
- **Headline tests** (`BattlefieldWorldTests.TwinRuns_AndARestoreMidBattle_EndIdentically`):
  - two runs of the same battle hash equal;
  - a snapshot restored mid-battle runs to the same hash.

## Structure footprints addendum (2026-09-28, docs/structure-footprints.md)

| State | Written only by |
|---|---|
| `Structure.Facing` | construction (North by default); scenario setup (`castle-facing:`); restored by `ReadStructures` (v42) |

- `Footprints.For`, `SubtileLayer` and `BattlePathing.ReachableFrom` are pure reads of the
  structure and return values.
- The board layer is rebuilt from the structure every time it is needed, so it is never
  saved.

## Subtile movement addendum (2026-09-29, docs/subtile-movement.md, M42 phase 1)

| State | Written only by |
|---|---|
| `Unit.Subtile` | `Battlefields.Placement` (`Seat`, `Reseat`, `SeparateNonHostile`), called from `GameWorld.AddUnit`, `GameWorld.AddStructure`, `MoveArrivalEvent` and `GroupArrivalEvent` (interim, until phase 2), `DisembarkIntent`, `EmbarkIntent` / `EmbarkGoal` (clear), and `RespondToProposalIntent`; restored by `Snapshot.ReadBattlefields` (v43) |
| `GameWorld.Restoring` | `Snapshot.Restore` only; while set, nothing is placed or popped |

- Placement is a pure function of the world at the moment it runs: candidate subtiles in a
  fixed order (centre rows, lowest lane, open before walls), holders read from `world.Units`,
  the pop search breadth-first in N, E, S, W order, units in ascending id order.
- Nothing reads placement to decide something else yet, so a call site can't make the result
  depend on who looked first.
- **Test:** `SubtilePlacementTests.ATwinRun_HashesEqual_WithSubtilesInTheState` and
  `SubtilesSurviveASaveAndRestore_AndRestoreDoesNotReplaceAnyone`.

**M42 phase 2:** `Unit.SubtileWalkTick/Seq` (the walk-in anchor) is written only by
`SubtileWalk` (`Begin`, `Step`, `Clear`) and cleared by `TileEntry.Enter`; restored by
`Snapshot.ReadBattlefields` and rebuilt into a `SubtileStepEvent` by `RegenerateQueue` at its
original (tick, Seq). `SubtileWalk.NextStep` and `Placement.EntryLane` are pure reads (candidate
order fixed, holders read from `world.Units`, breadth-first N, E, S, W). A group's landing in
files takes members in id order. Tests: `SubtileWalkInTests.ASaveMidWalk_RestoresAndRunsToTheSameEnd`
and `ATwinRun_OfAColumnArriving_HashesEqual`.

**M42 phase 3:** `Unit.SubtileRoute` and `SubtileRouteTick/Seq` are written only by
`SubtileRoutes` (`Begin`, `Step`, `Cancel`, `Resume`) and `SubtileRouteIntent` (which calls
`Begin`); `MoveIntent.BeginMove` cancels; `Battlefields.Close` resumes. Restored by
`Snapshot.ReadBattlefields`; the step event is rebuilt by `RegenerateQueue` at its original
(tick, Seq), and a route paused by a battle has no anchor and no event. `SubtileRoutes.Check`
and `StepProblem` are pure reads. Tests: `SubtileRouteTests.ASaveMidRoute_RestoresAndRunsToTheSameEnd`
and `ATwinRun_OfSeveralRoutes_HashesEqual`.

**M42 phase 4:** `Unit.Board` (now: tile, last note, came-from, order) is still written only by
`Battlefields` and `SetBattleOrderIntent`; a unit's place on a board is `Unit.Subtile`, written by
`Placement`, `SubtileWalk`, `SubtileRoutes` and, on the beat, `Battlefields.Apply`. Enrolment
(`Enroll`, `AdoptRoute`) runs in id order at open, admit and the start of each turn. A hop put
off to a beat is rescheduled at `NextBeat` with the same epoch and keeps its anchor, so
`RegenerateQueue` rebuilds it unchanged. Tests: the existing M41 twin-run and mid-battle
restore test (`BattlefieldWorldTests`) run on the new model.

## Update 2026-09-29: M43 step 1, the subtile pathfinder

`SubtilePathfinder.Find` and `SubtileStepRules` are **pure reads** (architecture §2.2): they read
the grid, structures, diplomacy and (for planning) the caller's visible set, and write nothing.
Per-query caches are local to one `SubtileStepRules` / one search and never stored. Determinism of the
result: integer costs only, neighbours in N, E, S, W order, ties broken by (f, subtile index), so the
same query on the same state gives the same path. Pinned by
`SubtilePathfinderTests.ASearch_IsAPureRead_AndTheSameQueryGivesTheSamePath` (100 queries, snapshot
hash unchanged). No new state, no new anchor, no snapshot change. Fog: the planner treats structures
it can't see as open ground and the walk's ground-truth check (the same rules with `visible` null)
stops the unit, so a hidden wall is never revealed by a plan's success or failure.

## Update 2026-09-29: M43, one movement (steps 2 to 4)

**Movement state.** A unit that is going somewhere is *walking*: `Unit.SubtileRoute` (the steps left)
plus its anchor `SubtileRouteTick`/`SubtileRouteSeq` (the M4 fencing token of the one queued
`SubtileRouteStepEvent`), and `Unit.PathFinalDest` (the tile it was ordered to; null for a drawn route).
`PathRemaining`, `NextArrivalTick`/`Seq`, `SubtileWalkTick`/`Seq` and `LeavingBoard` are gone; so are
`MoveArrivalEvent`, `GroupArrivalEvent` and the group anchors. A group has no events of its own: it
moves as its members' walks (`Group.PendingArrivals` counts the walkers still going).

**Mutation points (one each).**
- The walk's step: `SubtileRoutes.Step` is THE writer of `Unit.Subtile` and `Unit.Position` along a walk
  (a step across a tile edge goes through `TileEntry.Enter`), the **road link wear**
  (`Road.CreditTraffic`, the one mutation point for road condition, now per step and per link), and the
  re-aim of a walk that would stop on a friend. `Walk.Begin/Resume/Finished/Halt/Stop` are the only other
  writers of the walk's fields; `Battlefields` (open, admit, leave, close) is the only other writer of
  `Unit.Board`, and `AdoptRoute` turns a walk into a battle order (the destination tile is kept in
  `PathFinalDest`; the unit re-plans from where it lands, a pure function of the world at that tick).
- Stale steps fence on the anchor (`(tick, Seq)` must match), not on `AssignmentEpoch`: retasking replaces
  the route and reschedules, and an old event no-ops.

**Pure reads.** `SubtilePathfinder.Find`, `SubtileStepRules` (incl. `StepCost(..., now)`,
`CheapestStep`), `Road.EffectiveCost`/`ConditionAt`, `Walk.PickGoal`/`NearestFree`/`HeldByStanding`. None
writes. Pinned by `SubtilePathfinderTests` and `RoadLinkTests.PathSearches_ArePureReads_AcrossARoadSet`.

**Fog.** The planner (`Walk.Begin`, the drawn-route check) uses the owner's visible tiles; the walk's
per-step check is ground truth. A hidden wall is met by walking into it (`Walk.Halt`), never revealed by a
plan.

**Snapshot v44.** A unit row keeps `PathFinalDest` and loses the path and arrival anchor; the walk is in
the battlefield block with its anchor; roads are subtile links (owner subtile x, y, axis, condition, last
decay), sorted by owner (y, x, axis). `RegenerateQueue` rebuilds one step event per walking unit.
`CombatConfig` no longer carries a model (the grid is the only one).

## M44 addendum (2026-10-01) - stone and ore (docs/stone-and-ore-land.md)

**New state.** `GameWorld.VeinConfig` (genesis-set, immutable), `GameWorld.Veins` (terrain),
`GameWorld.KnownVeins` and `GameWorld.SurveyedBarren` (per-faction knowledge, only grows), and
`Unit.Survey` (a Miner's in-flight survey, with its `SurveyCompleteEvent` anchor). All (y, x)-sorted
`SortedSet`s keyed by faction id in a `SortedDictionary`.

**Mutation points.**
- `Veins.Seed` is THE writer of `GameWorld.Veins`, called once from `Genesis.Build` before any sight.
  It draws no sim `Rng`: a vein is `hash(VeinConfig.Seed, x, y) mod OneIn == 0` plus one per
  vein-less 8-connected mountain range (its lowest-hash tile). Pinned by
  `MiningSurveyTests.Genesis_VeinSeeding_DrawsNoSimRng`.
- `Veins.Learn` / `Veins.MarkBarren` are the only writers of `KnownVeins` / `SurveyedBarren`. Callers:
  `SurveyRules.Complete` (from `SurveyCompleteEvent`), `Veins.OnSight` (from `Sight.AfterReveal`) and
  `Veins.OnMineRaised` (from `PlaceSiteIntent` on a Mine site). All event-driven; views never write.
- `SurveyRules.Begin / OnArrival / Cancel / Complete` are the only writers of `Unit.Survey`. Cancel hooks:
  `Retask.Release` (any new order) and `Walk.Halt` (the walk to the slope failed).
  `CombatRules.ResumeInterrupted` re-issues a pinned surveyor's walk.

**Anchor.** `SurveyPlan.CompleteTick/CompleteSeq`, set when the dig starts. `RegenerateQueue` rebuilds
the event; `SurveyCompleteEvent` fences on the plan existing and `CompleteTick == At`. Pinned by
`MiningSurveyTests.Snapshot_MidDig_RoundTrips_AndFinishesIdentically` and
`Survey_CountermandedByANewOrder_AbandonsOutLoud_StaleEventNoOps`.

**Pure reads.** `Veins.IsVein / Knows / ProvenBarren`; the projector's `FillSecrets` vein block;
`ThinkContext.NearestFreeVein / NearestUnsurveyedMountain`.

**Quarry claims.** The Quarry now claims Hills (`ClaimCount 6`) with `DegradeAmount 0`: the M15 claim
writers are unchanged, `OnProductionTransition` stays a no-op for it, and off-ladder Hills never get a
`Fertility` entry. Pinned by `StoneAndOreTests.Quarry_RunsAYear_HillsNeverWear_RateStaysFull`.

**Snapshot v45.** A unit row gains the survey (flag, target, nullable tick, nullable seq) after the goal;
a trailing section holds `VeinConfig`, the vein tiles, and the per-faction known and barren sets.

## M45 — haul route UX (2026-10-01, `docs/m45-status.md`)

**New state.** `HaulRoute.Name`, `HaulRoute.Revision`, `RouteCrew.LastServe` (a `ServeReport`:
stop, tick, loaded, unloaded, `ServeNote` flags).

**Mutation points.**
- `HaulRoute.Stops` is replaced whole only by `UpdateHaulRouteIntent` (it was fixed at Set before).
  The same intent remaps every crew's `CurrentStop` (`RouteStops.Remap`, a pure function of the old and
  new lists), clears each crew's `LastServe` and bumps `Revision`.
- `HaulRoute.Name`: `SetHaulRouteIntent` and `RenameHaulRouteIntent`, both through `RouteNames.Clean`.
- `RouteCrew.LastServe`: written only by `ServeRouteStopIntent`; cleared only by `UpdateHaulRouteIntent`.
- `GameWorld.HaulJobs`: `UpdateHaulJobIntent` replaces a job record whole, keeping `JobId`,
  `QueueStamp` and `QueuedAtTick`, or removes a Once job whose new amount is already met.

**Fence.** `ServeRouteStopIntent` carries `ExpectedRevision` (the driver passes `route.Revision`) and
no-ops when the stop list changed under it. `-1` (logs from before M45) skips the fence. Pinned by
`HaulRouteEditTests.AServeSubmittedBeforeTheEdit_NoOps_OnTheRevisionFence`.

**Carriers.** `RouteCrews.Carries` (every role but Soldier and Archer) replaces the Hauler-only test
in the serve. Pure.

**Snapshot v46.** A trailing section after the veins: per route (id order) its id, name and revision,
per crew its last serve. Pinned by `HaulRouteEditTests.Snapshot_CarriesNameRevisionAndLastServe_AndRecovers`
and `TwinRun_WithAnEditMidway_SameHash`.

**Pure reads.** The projector's `FillHaul` (unit `CargoCapacity`, `HaulRouteId`) and the route block's
`Name` and `Last*` fields.

## Rest healing (2026-10-02, `docs/unit-healing.md`)

**New state.** `Unit.NextRestHealTick` / `Unit.NextRestHealSeq`: the anchor of a resting unit's
queued `RestHealEvent`. Null = dormant. `StructureSpec.Shelters` is catalog data (Castle, House,
Barracks), not world state.

**Mutation points.**
- The anchor is written only by `Rest.Schedule` / `Rest.Interrupt` (`src/Sim.Core/Healing/Rest.cs`)
  and by `Snapshot.ReadUnits`. `Rest.ArmIfDormant` is called from `TileEntry.Enter` (after
  `Interrupt`: a fresh rest on every tile change), `Battlefields.Close` (`ArmAllOn` the tile),
  `Construction.Complete` (a shelter finished: `ArmAllOn` its tile) and `DisembarkIntent`.
  `Interrupt` is also called from `Battlefields.Enroll`, `EmbarkIntent` and `EmbarkGoal`.
- `Unit.Health` gains one writer: `RestHealEvent.Apply`, `+HealPerPeriod` clamped to
  `CombatRules.MaxHealth`. Every other writer is unchanged.

**Anchor.** `RegenerateQueue` rebuilds the event per unit (id order); `RestHealEvent` fences on
`(NextRestHealTick, NextRestHealSeq) == (At, Seq)` and on the unit existing. Pinned by
`RestHealingTests.MidRest_SnapshotRestore_HealsIdentically` and `TwinRun_IsDeterministic`.

**Event volume.** One event per resting wounded unit per game-hour; a unit at full health, off a
shelter, on a board or aboard a boat owns none. No global iteration: `ArmAllOn` scans units for
one tile, the same O(units)-per-call shape flagged in invariant 1, called once per battle close
and per shelter completion.

**Pure reads.** `Rest.IsSheltered`, `Rest.IsResting`, `CombatRules.MaxHealth` (moved from
`Sim.Server.BattlefieldProjection`); the projector's `UnitDto.MaxHealth` / `Resting`. Pinned by
`RestHealingTests.IsResting_IsAPureRead`.

**Snapshot v47.** A unit row gains the rest-heal anchor (nullable tick, nullable seq) after the
death anchor.

## M46 Phase A — groups as records (2026-10-02, `docs/m46-groups-spec.md`)

**New state.**
- `GameWorld.NextGroupId`: the group id counter.
- On each group: `Name`, `Kind` (units / groups), `ParentId` and `Children`.
- `GroupState.Dismissed` (4).

**Mutation points.**
- `NextGroupId` is written only by `GroupRules.NewId` and by `Snapshot.ReadGroups`. `NewId` throws if
  the counter points at a taken id: fail loudly, never reuse.
- `ParentId` and `Children` are written only together, by `GroupRules.SetParent` and
  `GroupRules.Remove`.
- `Name` is written by `CreateGroupIntent` and `RenameGroupIntent`, always through `GroupRules.CleanName`.
- `PendingArrivals` comes down only through `GroupRules.OneLessPending`. It is called from:
  - `Walk.NoteWalkEnded`: a walk finished, or a halted walk that had an errand.
  - `CombatRules.OnUnitDeath`: a member that died while walking with an errand.

  Before M46, a death never decremented the count, and a halted `Forming` member never did either. The
  group then stayed `Forming` or `Moving` for ever. Pinned by `GroupPendingArrivalTests`.
- A group whose last member dies is **kept**. Only `DeleteGroupIntent` and `DisbandGroupIntent` (through
  `GroupRules.Remove`) take a group out of `world.Groups`.

**Pure reads.**
- `GroupRules.UnderCommand`: the single predicate behind the solo-work gates, which used to be
  `GroupId is not null`. The route-crew gate keeps membership until M47.
- `GroupRules.Depth`, `GroupRules.Height`, `GroupRules.JoinRefusal`, `GroupRules.ParentRefusal`.

**No anchors.** Phase A schedules nothing.

**Snapshot v48.** The groups block opens with `NextGroupId`. Each row gains its name, kind, parent and
children (ascending) after its members. Pinned by `GroupRecordTests.Snapshot_RoundTripsTheRecord` and
`TwinRun_SameHash`.

## M46 Phase C — the group walks (2026-10-02, `docs/m46-status.md`)

**New state on each group.**
- `MarchPath`: the lead path, the owner's view at order time. It is stored and never recomputed on
  restore (architecture §4 rule 7).
- `MarchLead`: the lead's index on the path.
- `NextStepTick` / `NextStepSeq`: the column's step anchor.
- `Stragglers`: members that fell out of the column.

**Mutation points.**
- The march state is written only by `GroupMarch`:
  - `Begin` (from `MoveGroupIntent`)
  - `Step` (from `GroupStepEvent`)
  - `Close`, `DropColumn`, `OnEnrolled`, `WakeColumns`
  - `Snapshot.ReadGroups`
- `GroupStepEvent` fences on `(NextStepTick, NextStepSeq) == (At, Seq)`, the same as a unit's
  `SubtileRouteStepEvent`.
- A column member's movement goes through `SubtileRoutes.Advance`, the one place a step changes the
  world (road wear, `TileEntry.Enter`, the combat trigger). It is now shared by solo walks and the
  column.
- `Group.Position` is written by the march: the lead's tile, or the destination at closing. The
  `TileEntry` write ("the lowest-id member's tile") is gone.

**The marker route.** While in the column, a member carries a one-step `SubtileRoute` to its slot
with **no step anchor of its own**. It counts as walking (friends pass through it), and
`RegenerateQueue` schedules nothing for it. The group's anchor is what moves it. The marker is dropped
when the column pauses for a battle, closes, or is retasked.

**Anchor.** `RegenerateQueue` rebuilds one `GroupStepEvent` per marching group, in id order. Pinned by
`GroupMarchTests.TwinRun_AndMidMarchRestore_EndTheSame`.

**No global iteration on a timer.** `GroupMarch.WakeColumns` walks `world.Groups` only when a board
closes or a unit leaves one. It is bounded by the number of groups.

**Event volume.** One step event per marching group per step, not one per member.

**Pure reads.** `GroupMarch.Plan`.

**Snapshot v49.** Each group row gains the march after its epoch.

## M46 Phase D — muster and dismiss (2026-10-02, `docs/m46-status.md`)

**New state.**
- On each group: `Awaiting` (the members the muster still waits for) and `MusterPlaces` (each member's
  place in the block).
- `Unit.SavedTask`: the errand that puts the unit back to work.
- `Extractor.HeldBy`: slots held for workers away at a muster.

**Mutation points.**
- `Awaiting` and `MusterPlaces` are written only by `GroupMuster` (`Muster`, `CallIn`, `Settle`,
  `Drop`, `DismissLeaf`), by `MoveGroupIntent.Halt`, by `GroupRules.OneLessPending` (a death), and by
  `Snapshot.ReadGroups`.
- `Unit.SavedTask` is written by:
  - `GroupMuster.SaveAndRelease` (set) and `Return` (cleared on dismiss)
  - `AssignWorkersIntent.GiveAwayHeldSlots` (cleared: the slot was given away)
  - `CombatRules.OnUnitDeath` (cleared)
  - `Snapshot.ReadUnits`
- `Extractor.HeldBy` is written by:
  - `GroupMuster.SaveAndRelease` (add)
  - `GroupMuster.Return` and `ReleaseHold` (remove)
  - `AssignWorkersIntent.GiveAwayHeldSlots` (remove, newest first)
  - `Snapshot.ReadExtractor`
- **Readers that count a held slot.** `PredicateEvaluator.WorkerCount` (the staffing count) uses
  `Workers + HeldBy`. Production, the arrival check (`WorkAssignment.TryAssignWorker`) and
  `AssignWorkersIntent`'s free-slot check read `Workers` only. An explicit assignment can take a held
  slot; the automation won't refill one.
- **The end-of-job hook.** `GroupMuster.OnFreed` runs where a task that must finish first ends:
  - `HaulDepositEvent` (a delivery)
  - `BirthEvent` and `Population`'s breeding stop (parents freed)
  - `SurveyRules` (report and cancel)
  - `ScoutMissionRunner` (home)
  - `DisembarkIntent`
  - `Battlefields.Close` and `Leave`

  It only sends the unit on (a walk). It never decides anything a later read could see differently.

**No anchors.** Every muster walk is an ordinary solo walk with its own step anchor.

**Pure reads.** `GroupMuster.Progress`, `GroupMuster.Busy`, `GroupMuster.AnchorRefusal`.

**Snapshot v50.**
- Each unit row gains its saved task.
- Each extractor row gains its held slots.
- Each group row gains its awaited members and its places.

Pinned by `GroupMusterTests.TheRoundTrip_IsDeterministic_AcrossAMidMusterRestore`.

## M46 Phases E–F — merge, split, the wire (2026-10-02)

**Mutation points.** `MergeGroupsIntent` and `SplitGroupIntent` change group membership and the tree
only through the existing writers: `GroupRules.SetParent`, `GroupRules.Discard` (new: removes an
emptied record without dismissing anyone), `GroupRules.NewId`, and `GroupMuster.CallIn` /
`GroupMuster.Release` (new: a member leaving a group under command goes back to its saved task).

**Pure reads.** `ViewProjector.ToGroupDto` (reads `GroupMuster.Progress`), the saved-task and
held-slot fields. Pinned by `GroupWireTests.TheProjection_IsAPureRead`.

**No new state, no format change.**

## M47 — route groups (2026-10-02, `docs/m47-route-groups-spec.md`)

**State changes.**
- `RouteCrew.Members` became `RouteCrew.GroupId`.
- `Unit.RouteId` is gone. A unit's route is derived (`RouteCrews.RouteOf`, a scan over the few routes).
- Each group gained `RouteSuspended` and `PendingMuster`.

**Mutation points.**
- `RouteCrew.GroupId` is written only when a crew is created (`RouteCrews.PutOn`, from
  `AssignGroupToRouteIntent` and the M36 `AddRouteCrewIntent`) and by `Snapshot`.
- A crew leaves its route only through `RouteCrews.Release` (from `RemoveRouteCrew`, `ClearHaulRoute`,
  `UnassignGroupFromRoute`, and `GroupRules.Remove`).
- `RouteSuspended` and `PendingMuster` are written by:
  - `RouteCrews.PutOn` and `RouteCrews.Release`
  - `GroupMuster.Muster` (a pending muster) and `DismissLeaf` (resume)
  - `MoveGroupIntent` (a player's move suspends)
  - `ServeRouteStopIntent` (answers the pending muster, in the same intent as the serve)
  - `Snapshot`

**The driver** (`HaulingDriver.RunCrew`) submits only `MoveGroupIntent(ForRoute)` and
`ServeRouteStopIntent`, the same trust boundary as before. Pinned by
`RouteGroupTests.TwinRun_AndMidLegRestore_EndTheSame` and `HaulRouteTests`.

**Snapshot v51.**
- A crew row carries its `GroupId`.
- A unit row loses `RouteId`.
- A group row gains `RouteSuspended` and `PendingMuster`.

## Unit availability (2026-10-02, `docs/unit-availability.md`)

**No new state.** `UnitAvailability.Busy` is a pure read over existing state: the board, the battle
on the unit's tile, embarked, breeding, survey, scout mission, pursuit, haul plan, goal, activity, and
walk. Pinned by `UnitAvailabilityTests.IsAPureRead`.

**Readers.**
- `ClaimLedger.IsDormant` (now `IsFree && !UnderCommand && !claimed`)
- `HaulingDriver.RunCrew`, `BanditDriver.Free` (and its steal and unload checks)
- `ServeRouteStopIntent` (who is at the stop), `GroupMuster.Busy`
- `ViewProjector` (`UnitDto.Busy`, own units), which `ThinkContext.IsIdleStill` reads

**Behaviour change.** A unit on a battle's tile is never free. Before this, only the bandit driver
knew it. Pinned by `UnitAvailabilityTests.AFightAtAStop_HoldsTheCrew_NoServeNoLeaving`.

## M49 — group stance (2026-10-02, `docs/m49-group-stance-spec.md`)

**New state on each group.** `Stance` (Passive / Defensive / Aggressive) and `ReturnTo` (where a
group away helping or charging goes back to).

**Mutation points.**
- `Stance` is written only by `SetGroupStanceIntent` (on the whole subtree) and `Snapshot`.
- `ReturnTo` is written only by `GroupStances`:
  - `OnMemberEnrolled` (aid: set, from `Battlefields.Enroll`)
  - `Charge` (set, from `ChargeGroupIntent`)
  - `ReturnIfDone` (cleared, from `GroupRules.OneLessPending`, `GroupMarch.StopHere` and
    `Battlefields.Close` via `ReturnAllDone`)
  - `Snapshot`

**Battlefield doctrine** is one pure read, `BattleDoctrine.Effective(world, unit)`: the unit's own,
else its stance's for its role, else the role default. It replaced four copies (`Battlefields` ×2,
`BattlefieldProjection` ×2).

**The stance driver** (`Sim.Server/Groups/GroupStanceDriver`) is fog-fair: it computes
`View.VisibleTiles` once per owner per think and submits only `ChargeGroupIntent`, a durable intent, so
an intent-log replay reproduces every charge without the driver.

**No global iteration on a timer in the sim.** `ReturnAllDone` walks the groups only when a board closes.

**Snapshot v52.** Each group row gains `Stance` and `ReturnTo`. Pinned by
`GroupStanceTests.TwinRun_AndMidFightRestore_EndTheSame`.
