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
per think — bounded by the per-player order cap
(`AutomationConstants.MaxOrdersPerPlayer`), not by world size, and it
runs server-side outside the sim's event stream. `View.VisibleTiles` is
computed once per owner per think, not per condition.
