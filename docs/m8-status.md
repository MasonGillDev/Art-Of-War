# M8 — Population: Aging, Breeding & Death (COMPLETE — 2026-06-04)

## Where we are

**M8 done end-to-end.** Age is derived from `Unit.BornTick` (no global
sweep, no mutable age field — reflection-pinned). Lifespan is rolled
ONCE per unit at creation, materialized to `Unit.DeathTick`, scheduled
as a `DeathByAgeEvent`, M4-regenerable. Breeding rides the new
`StructureKind.House`: `BeginBreedingIntent` validates and starts;
`BirthEvent` completes after `GestationTicks` and spawns a role-less
child. **Stop-on-removal is one rule** (`Population.OnUnitRemoved`)
called from both combat death and old-age death — zero combat ↔
breeding coupling. The strategic loop is closed.

## The headline contracts — proven

```
1. AgeYears = (now - BornTick) / TicksPerYear  — pure derivation,
   no state mutation under time advancement
2. Snapshot.Hash(uninterruptedLife) == Snapshot.Hash(midLifeSnapshotAndRestore)
   for a unit with a pending DeathByAgeEvent
3. Snapshot.Hash(uninterruptedBreeding) == Snapshot.Hash(midGestationSnapshotAndRestore)
   ← THE M8 CRUX
4. Snapshot.Hash(uninterruptedBreeding) == Snapshot.Hash(crashAndRecoverBreeding)
   ← durable-store crash recovery
5. Combat-killing-parent and aging-killing-parent both flow through
   the same OnUnitRemoved helper; combat and aging never name breeding
6. Attack-but-survive doesn't disrupt breeding; window check at start
   means aging past 40 mid-gestation continues
```

- `PopulationAgeTests.AgeYears_DerivesFromBornTick_AcrossTicks`
- `PopulationAgeTests.Unit_HasNoMutableAgeField` (reflection sentinel)
- `PopulationDeathTests.MidLife_SnapshotRoundTrip_DeathFiresAtSameTick`
- **`HouseBirthTests.MidGestation_SnapshotRoundTrip_BirthFiresAtSameTick`** (THE CRUX)
- **`RecoveryTests.MidGestation_RecoveryProducesChild`** (durable crash recovery)
- `RecoveryTests.DeathByAge_RecoveryFiresAtCorrectTick`
- `HouseBirthTests.Combat_KillsParent_MidGestation_StopsBreeding`
- `HouseBirthTests.OldAge_KillsParent_MidGestation_StopsBreeding`
- `HouseBirthTests.AttackedButSurvived_BreedingContinues`
- `HouseBirthTests.ParentAgesPast40_MidGestation_BreedingContinues`

## What landed

**Phase A — Age model.**
- `PopulationConfig` (8 fields): TicksPerYear, MinTrainAge,
  MinFertileAge, MaxFertileAge, GestationTicks, BirthFoodCost,
  LifespanMinYears, LifespanMaxYears. Defaults baked in.
- `Population.AgeYears`, `CanTrain`, `CanBreed` — pure reads.
- `Unit.BornTick` (immutable, defaults to a deeply-negative sentinel
  so hand-constructed test units are "adult by default" without
  needing per-test setup).
- `Unit.DeathTick + DeathSeq` — M4-anchor pair for the death event.
- `FactionStartSpec.StartingAgeYears` (default 30) +
  `UnitSpawn.StartingAgeYears?` override.
- `GameWorld.PopulationConfig + NextUnitId` (monotonic id counter for
  births). Snapshot `FormatVersion → 5`.

**Phase B — Old-age death + spec-aware Sim ctor.**
- `DeathByAgeEvent` reuses the M7 `OnUnitDeath` clean-death pipeline
  (drops cargo, group cleanup, removes from world). Fences on
  anchor mismatch.
- `Population.ScheduleLifespan` — rolls uniform `[Min, Max]` years
  from `sim.Rng`, materializes `DeathTick`, schedules
  `DeathByAgeEvent`. Floors at `sim.Now + 1` so a Genesis unit
  configured near max-lifespan still gets one tick to live.
- **`Simulation(GenesisSpec, ulong)` ctor** — the new canonical
  construction path. Folds genesis lifespan rolling inside the sim's
  owned construction; RNG is consumed in canonical (faction-id,
  unit-id) order. **No post-hoc `ScheduleAllLifespans` helper
  exists** — that pattern was rejected as a determinism landmine
  (see decision doc). The old `Simulation(GameWorld, ulong)` ctor
  stays for `Snapshot.Restore` and tests that build worlds by hand.
- `RegenerateQueue.From` extended: pending DeathByAgeEvents
  reconstructed from unit anchors in canonical id order.
- Host + Server migrated to the new ctor.

**Phase C — Training gate.**
- `AssignBuildersIntent` and `AssignWorkersIntent` add a
  `Population.CanTrain` check alongside their existing role/idle
  validation. Children skipped silently (per the "partial success"
  pattern; the intent only rejects if NO assignment + NO trigger).

**Phase D — House structure + BeginBreedingIntent.**
- `StructureKind.House = 9` + `StructureCatalog` row (player-buildable,
  Wood 30, 60-tick build).
- `House : StorageStructure` with optional `BreedingOccupation`
  (ParentAId, ParentBId, BirthTick, BirthSeq).
- `BeginBreedingIntent` validates ownership, presence, idleness,
  fertility window, food balance, parent-uniqueness, and
  no-already-breeding. On success: withdraws food, occupies both
  parents, schedules `BirthEvent`.
- Snapshot adds House/Occupation serialization. `BuildCompleteEvent`
  knows how to construct a House.
- `IntentJson` registers `BeginBreedingIntent`.

**Phase E — Birth + stop-on-removal (THE CRUX).**
- `BirthEvent.Apply` fences on occupation anchor; spawns role-less
  child at the house tile with `BornTick = sim.Now`; rolls the
  child's lifespan; frees both parents; clears occupation.
- `Population.OnUnitRemoved` scans Houses for an occupation that
  names the removed unit. If found: clears occupation (fencing the
  queued BirthEvent), frees the surviving parent. Food NOT refunded
  (raids should bite). One helper, two callers
  (`CombatRules.OnUnitDeath` + `DeathByAgeEvent.Apply`). Combat and
  aging never name breeding.
- `GameWorld.NextUnitId` — monotonic id allocator. Genesis seeds
  to `max(spawned ids) + 1`; BirthEvent increments.
- `RegenerateQueue.From` extended: pending BirthEvents reconstructed
  from house occupations in canonical (y, x) order.

**Phase F — View, host, server, docs.**
- `UnitView.AgeYears` derived at view-build time.
- `View.BuildPlayerView` has a new `(world, playerId, now)` overload;
  the original `(world, playerId)` still works (uses `now = 0`).
- `docs/population-model.md` decision doc captures
  derived-age-no-tick, stop-on-removal-one-rule, lifespan-inside-sim
  (not as a post-hoc sweep), children-act-don't-train, and rejected
  alternatives.
- `docs/architecture.md` §8 — M8 row added.

## Test counts

- Sim.Tests: **311 passing** (+34 new M8 tests).
- Sim.Persistence.Tests: **29 passing** (+1 mid-gestation recovery,
  +1 death-by-age recovery).
- Total: **340 / 340 green**.

## Carried debts updated

- **Spawning / population** — CLOSED by M8 (birth rate paired to M7's
  death rate).
- **Age-affects-capability** — opens. Plugs into `Unit.Buffs` (M7
  seam) as a `PowerModifier < 0` over an age threshold. Separate
  milestone or a small follow-on.
- **Auto-rebreed / population policies** — server-side convenience,
  later.
- **Per-unit food consumption / starvation** — separate milestone.
- **Mobs / wildlife population / AI faction** — reuses this birth/age
  shape + capture-on-death (M7) when the AI/intent-generator layer
  lands.
- **Sieges / capturing structures** — next combat extension.
- **Emergent ford** — still deferred.
