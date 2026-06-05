# Biome Degradation (M9)

## Decision

A producing extractor degrades a per-tile **fertility** value in a radius
around itself (including its own tile). Fertility is a **spatial lazy field**
sourced **only** from in-range extractors — not a propagating field — caught
up at extractor production-state transitions and read purely from the current
extractor set on demand. The biome of a tile derives from a band of its
current fertility:

```
fertility >= ForestThreshold              → Forest
DesertThreshold <= fert < ForestThreshold → Grassland
fertility < DesertThreshold               → Desert (LATCHED, permanent)
```

When an extractor's tile's derived biome no longer matches its
`RequiredBiome`, the next `ProductionTickEvent` rejects and the extractor
goes dormant. **This is what ends "extract forever from one tile."**

## Why a spatial lazy field (and what was ruled out)

### Why source from extractors, not propagation

The straightforward "desert spreads to neighbours" cellular-automaton model
would force a global per-tick sweep over every tile (or a per-tile schedule
with O(neighbours) coupling). That breaks the engine's no-global-tick
discipline (`docs/architecture.md` §4 rule 3) and re-introduces all the
storage/perf problems the lazy-derived-state pattern was built to avoid.

Extractor-sourced degradation keeps the math local and lazy:

- A tile's "current rate" is `MAX (not sum)` of the in-range producing
  extractors' degrade amounts, or the configured recovery rate if no
  extractor is producing and the tile has deviation to recover.
- The rate is **constant between extractor production-state transitions**.
  That's the M9 invariant.
- A pure read of `FertilityAt(tile, now)` looks at the current extractor set
  and applies the rate over `(now - lastUpdateTick)`. Pure: no mutation.
- A write happens only when an extractor transitions (arms / disarms /
  becomes dormant). The write catches up every tile in that extractor's
  radius and anchors `lastUpdateTick = now`.

### Why MAX, not sum, over overlapping extractors

If two LumberCamps' radii overlap on a shared tile, that tile's rate would
be 2× under summation. That creates an obvious exploit ("park enough camps
to instantly desertify a small strip") and breaks the spec's "biome
geography drives the economy" intent — geography becomes a problem to
exploit, not a resource to manage.

MAX makes overlapping camps an inefficiency, not a multiplier. Two camps on
the same tiles produce roughly the same degrade as one (per-tile);
production output stacks (each camp fills its own buffer), but the land
exhausts at the same rate.

### Why an IMPLICIT desert latch (no stored flag)

The original sketch in the spec called for an explicit `DesertLatched: bool`
per-tile flag. On reflection, the latch is exactly equivalent to the
predicate `(baseline + stored deviation) < DesertThreshold`. A tile whose
*stored* fertility (at its last catch-up) is below threshold is latched —
the M9 invariant of "rate is constant between catch-ups" guarantees that
between any two consecutive catch-ups, the stored value at the start of the
interval correctly determines whether the latch fires.

Implementation: `DeriveRate` returns 0 (or `-degradeRate` if a producer is
in range) when `storedFertility < DesertThreshold`. Recovery is never
returned for a latched tile. No flag is stored or serialized.

Trade-off: the latch decision is re-derived on every read. The cost is one
addition and one comparison per FertilityAt call — trivial.

### Why a step penalty on downward band crossings (and asymmetric — no upward bonus)

The smooth-gradient model that landed first (deviation moves linearly with
elapsed × rate, biome = `Band(current fertility)`) had a glaring problem:
when a Forest tile crossed `ForestThreshold=75` going down, fertility
settled at 74. Recovery at `+1/30` climbed back to 75 in 30 ticks. The
biome-mismatch dormancy was a **30-tick pause** — barely a pause at all.
"Extract forever" wasn't really fixed; it became "extract, brief nap,
extract." Players could just duty-cycle and the strategic pressure
disappeared.

The fix: **when degrade crosses a band threshold going down, snap fertility
to the next band's baseline** instead of stopping at threshold-1.

| Crossing (degrade direction) | Without snap | **With snap** |
|---|---|---|
| Forest → Grassland (across 75) | settles at 74 | **snaps to GrasslandBaseline 50** |
| Grassland → Desert (across 25) | settles at 24 | **snaps to DesertBaseline 10** |

A Forest tile that crosses into Grassland now needs `(75 − 50) × 30 = 750`
ticks of rest to climb back to Forest — vs. 30 in the smooth model. The
biome flip becomes a durable loss, not a blip. The player really does have
to move on.

**The penalty is paid by the LAND**, not by the extractor. Every tile in
any producing extractor's radius that crosses the threshold snaps. The
extractor's own tile is one of those; the radius tiles share the same fate.
This is the right primitive — extractors are mobile, the land isn't.

**Asymmetric: only on the DOWN direction.** Recovery climbs smoothly
through the upward crossings — there's no instant snap up to
`ForestBaseline=100` when a recovering Grassland tile crosses 75. Matches
the design intent ("easy to cut, hard to regrow") and avoids the
exploitative inverse (let a tile dip, then recover for cheap).

The snaps for Forest→Grassland (to 50) and Grassland→Desert (to 10) use
the same mechanism for symmetry. The G→D snap doesn't materially change
gameplay because the latch is already permanent once below
`DesertThreshold`; the uniformity is for the model's clarity, and it
makes the math at the deep-degrade boundary stable.

**Implementation:** the catch-up math (`ApplyMath` in
`Sim.Core.Biomes.BiomeDegradation`) iterates band crossings during the
elapsed-time integration. At most two snaps per call
(Forest → Grassland → Desert); recovery direction skips the snap branch.
Observation-independent across the snap (a single catch-up at T equals
many catch-ups summing to T). Pure reads (`FertilityAt`) apply the same
logic — no mutation.

### Why F/G/D only, not Mountain/Hills

Quarry (Mountain) and Mine (Hills) do not degrade their tiles in M9. The
fertility ladder is Forest ↔ Grassland → Desert (latched); Mountain and
Hills sit off the ladder and `BiomeAt` returns their worldgen biome
regardless of stored fertility. `StructureSpec.DegradeAmount` is 0 for
those kinds.

This is the answer to a real ambiguity in the spec ("one degrade rate per
extractor type" vs. the F/G/D ladder). Chosen because:

- The headline complaint — *extract forever from one tile* — is most
  glaring for Wood (LumberCamp) and Food (Farm), the two extractors a
  player relies on continuously and en masse. Closing them closes the
  practical problem; Stone and Ore are typically built in single instances
  per terrain band.
- Extending the ladder to Hills/Mountain would multiply the threshold-and-
  band design (Mountain → Hills → Grassland → ?) without any of the rates
  having been tuned. Premature.
- Leaving Quarry/Mine indefinite preserves a knowable "stable extractor"
  for early scenarios; tuning F/G/D is hard enough.

If Quarry/Mine relocation pressure becomes gameplay-important, it's its own
milestone — the ladder extends one band at a time, and the locked decisions
here don't constrain that extension.

### Why pure-read derives the rate from world state

`FertilityAt(tile, now)` reads the current `Structures` dict to determine
the producing-extractor set, derives the rate, and applies it from the
tile's stored `(deviation, lastUpdateTick)` baseline. This is correct
because the rate is **invariant** between extractor production-state
transitions — and catch-up writes happen at every such transition, refreshing
`lastUpdateTick = now`. So between any two consecutive transitions, the
stored deviation + lastUpdateTick + the current rate uniquely determines
fertility at any read time.

Without this property, the lazy model would be incorrect — but with it, the
model is exact and observation-independent. The math, the rate derivation,
and the anchor discipline form one tight contract.

### Why anchor `lastUpdateTick = now` on every transition catch-up

The catch-up math returns a "carry remainder" (road-decay-style:
`completed_periods × rate` applied, with the partial period banked into
`lastUpdateTick`). Within a CONSTANT-RATE segment, the carry is what makes
the math observation-independent.

But at a TRANSITION the rate is about to change. Carrying the old rate's
remainder into the next interval would interpret partial elapsed-time under
the OLD rate as partial elapsed-time under the NEW rate — incorrect, and
fragile in subtle ways.

So at every transition catch-up, the carry is **dropped** and
`lastUpdateTick` is anchored to `now`. The new rate's window starts cleanly
from this point.

A consequence: the catch-up writes an entry even when `deviation` didn't
change (e.g., a tile being freshly anchored at the moment its rate becomes
non-zero). Without this, a never-previously-touched tile would inherit
`lastUpdateTick = 0` from the absent-entry default, and the next read
would over-apply the post-transition rate over the entire simulation
history. The anchor is what makes the lazy model load-bearing across the
"first time this tile saw any rate" boundary.

The test-only `CatchUpWithRate` keeps the carry-and-sparse-remove behaviour
because it's not at a transition — it's a math driver for the observation-
independence tests.

### Why `Spec.DegradeAmount` on `StructureSpec` and a single `DegradePeriod` on the config

`StructureSpec.DegradeAmount` is per-kind (LumberCamp = 1, Farm = 2, others
= 0). A single global `BiomeDegradationConfig.DegradePeriod` (10 ticks)
applies to all extractor-driven degrade. This keeps the MAX comparison a
simple integer max on `DegradeAmount` — no cross-rate ratio comparison
needed when two different extractor kinds overlap.

The road-decay precedent (`RoadConstants.DECAY_PERIOD` is global, decay
amount is per-tile) is the same shape. If a future tuning needs per-kind
periods, we'll do per-kind LCM-period normalisation; for M9 the single
period is enough.

### Why per-player per-tile remembered biome

For fog-of-war: the player should see the **last-seen** biome on tiles that
are explored-but-not-visible. With M9 degradation, a tile's biome can shift
behind the fog. Showing the worldgen biome would leak the "real" state
(player doesn't know whether the tile has degraded); showing the current
biome would leak information that's not in vision (player has no source on
this tile).

`world.RememberedBiome[playerId][tile]` is updated on every
`Sight.Reveal` call to the current derived biome. Between reveals (when
the tile is in fog), the value is frozen at whatever the last reveal
stored — "stale by up to one reveal" in the worst case.

Snapshot serialization adds the new field; the format-version bumps to 6.

## What gets built (M9)

- New `Sim.Core.Biomes` module:
  - `BiomeDegradationConfig` (thresholds, baselines, recovery rate, degrade
    period & radius). Serialized in the snapshot.
  - `Fertility` (per-tile mutable: signed `Deviation` + `LastUpdateTick`).
    Sparse in `GameWorld.Fertility`.
  - `BiomeDegradation` static API: `FertilityAt` / `BiomeAt` (pure reads);
    `OnProductionTransition` / `CatchUp` (mutating, called from extractor
    transitions). Plus `CatchUpWithRate` (test-only math driver).
- `StructureSpec.DegradeAmount` (non-zero for LumberCamp + Farm).
- `Extractor.ArmIfDormant` calls `OnProductionTransition` before flipping
  `TickArmed=true`.
- `ProductionTickEvent.Apply` adds:
  - Biome-mismatch guard at top (the headline behavior).
  - `OnProductionTransition` call before each "going dormant" branch.
- `PlaceSiteIntent` uses `BiomeAt(now)` for placement validation.
- `View.BuildPlayerView` consults `world.RememberedBiome[playerId]` for
  remembered-not-visible tiles.
- `Sight.Reveal(world, playerId, center, r, now)` — new `now` parameter;
  writes `RememberedBiome[playerId][tile] = BiomeAt(world, tile, now, config)`
  for each tile in the disc.
- `Snapshot.FormatVersion → 6`; serializes `BiomeDegradationConfig`, sparse
  `Fertility`, and `RememberedBiome`.

## Future expansion

- **Worker-intensity scaling.** Once `DegradeAmount` is solid as a per-kind
  constant, scale it with worker count: more workers → faster degrade.
  Adds gameplay choice "extract slow and steady vs. fast and exhausting."
- **Auto-wake on recovery.** When a dormant LumberCamp's tile recovers
  (Forest band reached again), auto-rearm via the M1 compute-next-viable-
  tick pattern. For M9 the player manually relocates; auto-wake is a quality-
  of-life follow-on.
- **Extend ladder to Mountain/Hills.** Add a Mountain → Hills → Grassland
  step. Each band would need its own threshold + degrade rate. Compatible
  with the M9 design; just bigger.
- **Per-edge degrade radius.** The current radius is a Chebyshev box. A
  per-extractor-kind radius (Lumber radius 2, Farm radius 1) is a one-field
  change.
- **Visible biome morphing on the client.** The sim reports current biome;
  the client interpolates the visual transition. Pure renderer concern.

## What's deferred (explicit)

- **Worker-intensity scaling** (next layer).
- **Auto-wake on recovery** (M1 pattern, later).
- **Self-spreading desert** — explicitly NOT done; it breaks lazy/local
  and would force a global tick.
- **Mine/Quarry relocation pressure** — Hills/Mountain don't degrade in
  M9. A future ladder extension if needed.
- **Trade as the non-war exit from land collapse.** Its own milestone; M9
  gives it stakes.

## Acceptance tests

- `BiomeFertilityCatchUpTests.CatchUpWithRate_IsObservationIndependent` —
  the integer math is observation-independent within a rate segment.
- `BiomeFertilityCatchUpTests.FertilityAt_IsPureRead_NoMutation` /
  `BiomeAt_IsPureRead_NoMutation` — 100× no-mutation.
- `BiomeFertilityCatchUpTests.Band_TransitionsAtThresholds` — bands fire at
  the right fertility values.
- `BiomeFertilityCatchUpTests.GeneratedDesert_IsImplicitlyLatched_NeverRecovers`
  — generated-desert tiles never recover.
- `BiomeFertilityCatchUpTests.Degrade_OnForestGrasslandCrossing_SnapsToGrasslandBaseline`
  — fertility lands at 50 (the snap target), NOT at 74 (threshold-1).
- `BiomeFertilityCatchUpTests.Degrade_OnGrasslandDesertCrossing_SnapsToDesertBaseline`
  — fertility lands at 10, NOT at 24.
- `BiomeFertilityCatchUpTests.Degrade_AcrossBothCrossings_InOnePass_AppliesBothSnaps`
  — both snaps fire when a single catch-up traverses Forest → Grassland → Desert.
- `BiomeFertilityCatchUpTests.Degrade_SnapMath_IsObservationIndependent_AcrossCrossings`
  — single catch-up at T vs many intermediate catch-ups land at the same state.
- `BiomeFertilityCatchUpTests.Recovery_AcrossGrasslandForestThreshold_DoesNotSnapUp`
  — the asymmetry contract: recovery climbs smoothly, no upward bonus snap.
- `BiomeFertilityCatchUpTests.Snap_RecoveryFromSnapBaseline_TakesManyTicks_TheHeadline`
  — the gameplay headline: recovery from post-snap Grassland to Forest
  takes 750 ticks (25× the pre-snap recovery cost).
- `BiomeDegradationTests.TwoOverlappingLumberCamps_DegradeAtMax_NotSum` —
  the MAX (not sum) contract.
- `BiomeDegradationTests.LumberCamp_OnForest_DegradesOwnTile_ThenStops_TheHeadline`
  — **THE headline behaviour: no infinite single-tile extraction.**
- `BiomeDegradationTests.Farm_OnGrassland_DegradesToDesert_PermanentlyDead`
  — farms drive permanent Desert via the latch.
- `BiomeDegradationTests.LumberCamp_CannotBePlaced_OnDegradedGrassland` —
  `PlaceSiteIntent` validates against derived biome.
- `BiomeDegradationTests.Degradation_TwinRun_HashesMatch` — twin-run
  determinism end-to-end through M9.
- `BiomeDegradationTests.Degradation_SnapshotMidRun_RoundTrips` — mid-
  degradation snapshot round-trip.
- `BiomeDegradationFogTests.RememberedBiome_Stale_BehindFog` — fog
  preserves last-seen biome.
- `BiomeDegradationFogTests.ReScout_RefreshesRememberedBiome_ToCurrent` —
  re-scouting updates the cache.

## References

- `docs/architecture.md` — §2.5 lazy catch-up math; §4 rule 3 (no global
  tick); §4 rule 8 (no condition-dependent decay rates, which we honour by
  using band-stepped rates).
- `docs/extraction-model.md` — the structure-gated extraction model M9
  extends.
- `persistent-rts-design.md` §11 — the "trade as escape from collapse"
  loop M9 finally makes meaningful.
