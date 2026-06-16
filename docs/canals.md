# Canals & Water-Restored Land (M21)

## Decision

Two coupled changes that soften M9 desertification and add a new investment loop.

1. **Water restores degraded land.** The M9 desert latch — once a tile's stored
   fertility drops below `DesertThreshold` it can never recover — becomes
   **conditional on distance to water**. A tile within
   `BiomeDegradationConfig.WaterRecoveryRadius` (Chebyshev, default 2) of any
   `Biome.Water` tile is no longer latched: it recovers normally toward its
   original Grassland/Forest. The softening is **degraded-only** — naturally
   barren worldgen Desert (deviation 0) does *not* green near water; only land
   that was degraded by farming/logging climbs back. Implemented as a single
   extra condition on the latch branch of `BiomeDegradation.DeriveRate`; the
   "degraded only" property falls out for free from the pre-existing
   `storedDev < 0` recovery guard.

2. **Canals.** A new player build job (`PlaceCanalIntent`, `StructureKind.Canal`)
   that converts an ordered **path** of land tiles into `Biome.Water`. It is the
   designed follow-on flagged in `docs/boats.md` ("a canal is just more water to
   sail through"). One intent carries the whole route; one `ConstructionSite`
   (anchored at `path[0]`) tracks it, with cost and build time **scaled by path
   length**. On completion the path floods and the surrounding land is irrigated;
   **no resulting structure remains** — the tiles are simply Water.

The two share one mechanism: **water proximity lifts the latch**, and **canal
completion is the event that changes water proximity**. The whole design is the
M9/M15 spatial-lazy-field discipline (`architecture.md` §2.5) applied to a new
rate-changing event.

### Locked rules

- **Canals must extend from water.** `path[0]` is 4-adjacent to an existing
  `Biome.Water` tile; each `path[i>0]` is 4-adjacent to `path[i-1]`. The finished
  canal is therefore a connected, boat-navigable waterway rooted at a real water
  source — you *redirect* water, you don't conjure isolated puddles.
- **Diggable land only.** Each path tile must be in bounds, not already Water,
  not Mountain (rock — too solid), structure-free, claim-free, and not reserved
  by another in-flight canal (`CanalReservation.IsReserved`, a scan of in-flight
  canal sites mirroring `Claims.ClaimantAt`).
- **Expensive, per tile.** Catalog cost is **per dug tile** (Stone-heavy + Wood,
  the "real investment"); `PlaceCanalIntent` multiplies `BuildCost` and
  `BuildDurationTicks` by `path.Count`. A long canal is a proportionally huge
  commitment that materials must be physically hauled to fund.
- **Recovery uses the existing rate.** Near water lifts the latch and recovers at
  the normal `RecoveryAmount`/`RecoveryPeriod` — no separate boosted rate (a
  `WaterRecoveryAmount` knob is a trivial future extension, deliberately not built).

## Why

### Why water restores land at all — and why degraded-only

M9's permanent desert latch is the punishment that makes "where you farm" matter,
but a *fully* permanent latch with no escape makes the early map a field of
landmines: one over-farmed tile is dead forever, full stop. Tying recovery to
water proximity keeps the pressure (inland land still has a hard desert floor)
while making lakeside/coastal land **renewable** — a real strategic asymmetry in
where you site farms. Canals then let you *manufacture* that safety inland, which
is the whole point of the feature pairing.

Restoring **only degraded** land (not blooming raw desert) was a deliberate
design call: irrigation un-sticks land *you* exhausted, but it does not terraform
naturally barren desert into farmland — that would weaken the "desert is dead
land" rule and require raising tile baselines (a bigger sim change). The
implementation needs no special case: raw desert sits at deviation 0, so the
existing "recovery applies only when `storedDev < 0`" guard returns rate 0 for
it. Degraded Grassland/Forest carry a negative deviation and climb back.

### Why a whole-path build job — not per-tile chained

The "extend from water" rule needs ordering: a tile can only be validated as
water-rooted if the tiles before it in the chain are (or will be) water. Modeling
a canal as N independent per-tile builds would force tiles to flood one at a time
and re-validate mid-construction — awkward and leaky. A single whole-path job
validates the entire connected chain up front against the *planned* canal, prices
it as one investment, and floods atomically on completion. It reuses the existing
construction/haul/build-complete machinery wholesale; the only new state is the
`CanalPath` list on `ConstructionSite` (following the `DockSlip`/`ClaimTiles`
precedent of "an optional field only some `TargetKind`s use") and the
length-scaling in its constructor.

### Why no resulting structure

A canal "is just more water to sail through" (`docs/boats.md`). Leaving a marker
structure on every water tile would invite structures-on-water invariants and buy
nothing — the tile being `Biome.Water` is the entire effect, and the full biome
grid is already snapshotted byte-per-tile, so the mutation persists for free. The
`ConstructionSite` is the build vehicle only; `BuildCompleteEvent`'s canal branch
floods the path and returns without `AddStructure`.

### Why catch-up-then-mutate ordering is load-bearing

This is the one genuinely determinism-sensitive piece. When a canal floods,
nearby latched land gains a recovery path. Water proximity is read *live* from the
grid (so canal water counts automatically and the field stays lazy/local). The
M9 invariant is "the rate at a tile is constant between transition catch-ups." So
canal completion is a transition that **must** catch up every affected tile under
the OLD (pre-water) rate and anchor `lastUpdate = now`, *before* the grid mutates.
If the grid changed first, a later read would re-interpret the entire pre-canal
elapsed time under the new recovery rate — the anchor-discipline trap
(`architecture.md` §2.5, biome-degradation "Why anchor lastUpdateTick"). The
canal branch does exactly: `OnWaterProximityChanged` (old-rate catch-up) → then
`SetBiome(Water)` per path tile (and drop each tile's now-off-ladder Fertility
entry + any road). Pinned by
`WaterRestorationTests.OnWaterProximityChanged_AnchorsRecoveryAtTransition_NotRetroactively`.

### Why it composes with boats for free

Canal water *is* `Biome.Water`, the same enum value worldgen lakes use. So with
**zero new boat code**: existing `MoveBoatIntent`/boat pathfinding
(`BoatMovementCost.CostFor(grid.BiomeAt(tile))`) sails canal tiles; a Dock places
its slip on a canal tile (slip validation reads the derived/grid biome). That
delivers the design's headline uses — supply lines to the heart of a kingdom,
inland shipyards — as pure composition. Phase C of the milestone was tests only.

### Why a per-tile cost scaled by length, not a flat canal price

The user's framing is "canal building takes a lot of resources... a real
investment." A flat price would make a 2-tile spur and a 30-tile trunk cost the
same — nonsense. Per-tile pricing makes a long canal genuinely expensive and an
incremental decision (dig as far as you can fund), and keeps the catalog number a
single tunable knob.

## Future expansion

- **Boosted recovery near water.** A `WaterRecoveryAmount` (> `RecoveryAmount`)
  so canals visibly rejuvenate faster than rainfall. One config field + one
  branch in `DeriveRate`. Deliberately not in M21.
- **Greening raw desert / irrigation baseline-raising.** Let irrigation lift even
  deviation-0 desert into Grassland (terraforming). Bigger — needs a raised
  effective baseline near water, not just a lifted latch. A separate decision.
- **Canal removal / draining.** Water → land is a second water-proximity
  transition; it would run the same `OnWaterProximityChanged` discipline before
  restoring the tile's land biome. Not needed for the MVP (canals are permanent).
- **Near-water index.** `IsNearWater` and `OnWaterProximityChanged` do bounded
  Chebyshev scans on a hot-ish read path (flagged like `BuildersPresent`). If
  profiling demands it, a cached near-water set rebuilt at canal completion (the
  one event that changes water proximity) is the drop-in index.
- **Aqueducts / locks / elevation.** If terrain gains elevation, canals could
  require level ground or locks to climb — a placement-rule extension, not a
  change to the flood/irrigation core.

## Acceptance tests

`tests/Sim.Tests/WaterRestorationTests.cs` (Phase A) and
`tests/Sim.Tests/CanalsTests.cs` (Phases B–D):

- Degraded Grassland/Forest near water recovers across `DesertThreshold`; the
  Forest climb is slow and smooth (no upward snap — the asymmetry).
- Raw worldgen Desert near water does **not** bloom (the degraded-only guarantee).
- Degraded land outside `WaterRecoveryRadius` stays permanently latched.
- `OnWaterProximityChanged` anchors recovery at the transition, not retroactively.
- `FertilityAt`/`BiomeAt` stay pure reads near water (100× no-mutation).
- Canal placement rejects: not-extending-from-water, disconnected path,
  on-structure, on-claim, on-mountain, out-of-bounds, on-existing-water, empty.
- Cost and build time scale with path length.
- Completion floods every path tile and frees builders.
- Canal-reserved tiles block structure placement; `PlaceSiteIntent` rejects
  `Kind == Canal`.
- A degraded field beside a fresh canal recovers (the A×B irrigation composition).
- A boat sails through a fresh canal; a Dock places its slip on a canal tile.
- Mid-build snapshot round-trips `CanalPath` + progress; recovery-after-crash
  mid-canal-build replays identical.

## Headline determinism test

> **`CanalsTests.Canals_TwinRun_HashesMatch`** — two identical scenarios (build a
> multi-tile canal from a coast → sail a boat through it → a degraded field beside
> the canal recovers) produce `Snapshot.Hash` equality. The M21 contract per
> `architecture.md` §1.

## References

- `docs/architecture.md` §2.5 (lazy catch-up + the spatial rate-transition
  discipline canal completion reuses), §1 (headline contract), §8 (roadmap).
- `docs/biome-degradation.md` (the M9/M15 latch this softens; M21 addendum).
- `docs/boats.md` ("Future expansion → Canals"; the boat system canals compose
  with unchanged).
- `docs/world-generation.md` (the post-worldgen terrain-freeze rule canals are
  the sanctioned exception to; M21 addendum).
- `docs/extraction-claims.md` (`Claims.ClaimantAt` — the reservation-by-scan
  pattern `CanalReservation` mirrors).
