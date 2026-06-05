# M9 — Biome Degradation (COMPLETE — 2026-06-05)

## Where we are

**M9 done end-to-end.** A producing extractor degrades the per-tile
fertility of every tile in its radius (including its own). When the
extractor's tile drops out of its required biome (LumberCamp on
Forest → Grassland; Farm on Grassland → Desert), the next
`ProductionTickEvent` rejects via a new biome-mismatch guard and the
extractor goes dormant. **The "extract forever from one tile"
complaint is closed** — production from any single LumberCamp or
Farm is now bounded by exhaustion.

Recovery (Forest ↔ Grassland) materialises lazily on read. The desert
latch is implicit (no stored flag): once `stored fertility < DesertThreshold`,
`DeriveRate` forces recovery to zero — permanent Desert. The fertility
math is the first **spatial lazy field** in the codebase: caught up
only at extractor production-state transitions, computed purely on
demand, no global tick or propagating field.

## The headline contracts — proven

```
1. FertilityAt is integer-exact and observation-independent within a
   rate segment (the M2 lazy-catch-up shape).
2. FertilityAt / BiomeAt are PURE READS — 100× no-mutation hash test.
3. MaxInRangeProducingDegradeAmount returns MAX (not sum) over
   overlapping armed extractors.
4. A LumberCamp on Forest degrades its OWN tile to Grassland and the
   next ProductionTick goes dormant via biome-mismatch — no infinite
   single-tile extraction (THE headline).
5. A Farm on Grassland degrades its OWN tile to permanent Desert via
   the implicit latch.
6. Snapshot.Hash(uninterruptedDegradationRun)
      == Snapshot.Hash(snapshotMidDegradation_restore_continue)
   The mid-degradation round-trip contract — pure derived state, no
   new scheduled events, RegenerateQueue unchanged.
7. View.BuildPlayerView is pure-read after degradation; fog preserves
   last-seen biome until re-scouted.
```

- `BiomeFertilityCatchUpTests.CatchUpWithRate_IsObservationIndependent`
- `BiomeFertilityCatchUpTests.FertilityAt_IsPureRead_NoMutation`
- `BiomeFertilityCatchUpTests.BiomeAt_OffLadderTiles_AlwaysReturnWorldgenBiome`
- `BiomeFertilityCatchUpTests.DesertLatch_OncePushedBelowThreshold_RecoveryNoOps`
- `BiomeFertilityCatchUpTests.GeneratedDesert_IsImplicitlyLatched_NeverRecovers`
- `BiomeDegradationTests.LumberCamp_DegradesOwnTile_OverTime`
- `BiomeDegradationTests.TwoOverlappingLumberCamps_DegradeAtMax_NotSum`
- `BiomeDegradationTests.OnProductionTransition_PreStopThenPreStart_GivesExpectedDeviation`
- `BiomeDegradationTests.Recovery_AfterProductionStops_LazyReadsClimbBackToForest`
- `BiomeDegradationTests.Rotation_NewTransition_MaterializesAccumulatedRecovery`
- `BiomeDegradationTests.Latch_HoldsAcrossDormancy_DespiteRecoveryRateConfigured`
- **`BiomeDegradationTests.LumberCamp_OnForest_DegradesOwnTile_ThenStops_TheHeadline`** (THE HEADLINE)
- **`BiomeDegradationTests.Farm_OnGrassland_DegradesToDesert_PermanentlyDead`**
- `BiomeDegradationTests.LumberCamp_CannotBePlaced_OnDegradedGrassland`
- `BiomeDegradationTests.Degradation_TwinRun_HashesMatch`
- `BiomeDegradationTests.Degradation_SnapshotMidRun_RoundTrips`
- `BiomeDegradationFogTests.RememberedBiome_Stale_BehindFog`
- `BiomeDegradationFogTests.ReScout_RefreshesRememberedBiome_ToCurrent`
- `BiomeDegradationFogTests.View_DoesNotLeak_CurrentBiome_OnNonVisibleTiles`

## What landed

**Phase A — Fertility model + lazy catch-up math (isolated).**
- `BiomeDegradationConfig` (12 fields): per-biome baselines (F/G/D
  + H/M/W), `ForestThreshold` / `DesertThreshold`, `RecoveryAmount`
  / `RecoveryPeriod`, `DegradePeriod`, `DegradeRadius`. Immutable
  post-genesis; snapshot-serialized.
- `Fertility` per-tile struct: `(int Deviation, long LastUpdateTick)`
  + sparse `GameWorld.Fertility` dict. Deviation is signed (range
  [-baseline, 0]); recovery clamps at 0, degrade floors at -baseline.
- `BiomeDegradation` static API: `BaselineFertility` / `Band` /
  `IsOnLadder` / `FertilityAt` / `BiomeAt` (pure reads); `CatchUp` /
  `CatchUpWithRate` (mutating). 100× pure-read invariant.
- Implicit desert latch: `(baseline + stored deviation) < DesertThreshold`
  forces recovery to zero in `DeriveRate`. No flag stored.
- Generated-desert tiles (baseline < DesertThreshold) are
  implicitly latched from t=0.

**Phase B — Extractor-sourced degradation (MAX, not sum).**
- `StructureSpec.DegradeAmount` (non-zero for LumberCamp + Farm).
- `BiomeDegradation.MaxInRangeProducingDegradeAmount` — MAX over
  armed extractors in Chebyshev `DegradeRadius`.
- `BiomeDegradation.OnProductionTransition` — radius-bounded catch-
  up. Called from `Extractor.ArmIfDormant` (pre-start, TickArmed
  still false) and `ProductionTickEvent.Apply` stop branches (pre-
  stop, TickArmed still true). Anchors lastUpdateTick = now.

**Phase C — Recovery + permanent desert latch.**
- `DeriveRate` returns recovery rate when no degrade source in
  range AND deviation < 0 AND not latched. Returns 0 if latched.
- Forest ↔ Grassland reversible; below-DesertThreshold is permanent.
- Lazy: recovery materialises through pure reads; stored state
  updates only at the next transition catch-up.

**Phase D — Extractor self-regulation (THE HEADLINE).**
- `ProductionTickEvent.Apply` adds a biome-mismatch guard at the
  top: if `BiomeAt(extractor.At, now)` ≠ `Spec.RequiredBiome`, the
  tick rejects, OnProductionTransition catches up under the pre-stop
  rate, TickArmed → false. The player must relocate.
- `PlaceSiteIntent` validates against `BiomeAt(now)`, not the
  worldgen biome. A formerly-Forest tile that has degraded to
  Grassland now legitimately accepts a Farm and rejects a LumberCamp.

**Phase E — Fog × biome (stale terrain memory).**
- `GameWorld.RememberedBiome: Dictionary<int, Dictionary<TileCoord, Biome>>`
  — per-player per-tile snapshot of the last-seen biome.
- `Sight.Reveal` signature gained a `now` parameter. Every reveal
  also writes `RememberedBiome[playerId][tile] = BiomeAt(world,
  tile, now, config)` for every tile in the disc.
- `View.BuildPlayerView` consults `RememberedBiome[playerId]` for
  remembered-but-not-visible tiles. Currently-visible tiles fall
  through to live `BiomeAt(now)`.
- Snapshot `FormatVersion → 6`. Serializes
  `BiomeDegradationConfig`, sparse `Fertility`, and `RememberedBiome`.

**Phase F — Determinism, persistence, host, docs.**
- Twin-run + mid-degradation snapshot round-trip pinned by tests.
- `dotnet run --project src/Sim.Host -- --degradation` smoke:
  LumberCamp on Forest with one Lumberjack, manual instant haul
  per loop, prints biome at each step. Goes dormant via biome-
  mismatch around sim.Now = 300 (50 Wood produced before
  exhaustion).
- Guard greps: no global tile sweep, no condition-dependent rate,
  no path recomputation. Pure-read 100×-no-mutation tests for all
  M9 pure-read APIs.
- `docs/biome-degradation.md` decision doc lands (the spatial-lazy-
  field model, MAX-not-sum, F/G/D ladder, implicit latch, anchor
  discipline, F/G/D-only scope).
- `docs/architecture.md` §8 — M9 row added.

## Test counts

- Sim.Tests: **354 passing** (+42 new M9 tests: 17 Phase A in
  `BiomeFertilityCatchUpTests`, 18 Phase B+C+D in
  `BiomeDegradationTests`, 7 Phase E in `BiomeDegradationFogTests`).
- Sim.Persistence.Tests: **29 passing** (no new M9-specific tests
  here — Fertility is pure derived state, no new scheduled events
  to regen).
- Total: **383 / 383 green**.

## Carried debts updated

- **Extract-forever from one tile** — CLOSED by M9 for LumberCamp +
  Farm. The headline complaint is fixed.
- **Quarry/Mine relocation pressure** — opens. Hills/Mountain don't
  participate in the M9 fertility ladder. A future milestone that
  extends the ladder.
- **Worker-intensity scaling** — opens. Next layer on the per-kind
  fixed-rate model.
- **Auto-wake on recovery** — opens. Reuses M1 compute-next-viable-
  tick pattern; manual relocation is the M9 stop-gap.
- **Trade as the non-war exit from collapse** — its own milestone;
  M9 finally gives it real stakes (a player who farms a region into
  permanent Desert must war or trade).
- **Self-spreading desert** — explicitly NOT a debt; the spatial-
  lazy-field model forbids it.
- **Sieges / capturing structures / win conditions** — still open.
- **Emergent ford** — still open.
