# Environmental Fertility (M35)

**Status:** decided 2026-09-20 at the systems level; building. Progress in
`docs/m35-status.md`. Extends `docs/biome-degradation.md` (M9),
`docs/canals.md` (M21/M27), `docs/rivers.md`, `docs/extraction-claims.md`.

## Decision

A tile's fertility **baseline** stops being a flat per-biome constant and
becomes a pure derived function of the tile's environment:

```
Baseline(tile) = BandBaseline(worldgenBiome) + EnvOffset(tile)
EnvOffset(tile) = WaterBonus(distance-to-water) + DepthBonus(forest-depth)
```

clamped so that a tile at deviation 0 can never sit outside its worldgen band.
Five sub-decisions, each made against a real alternative:

1. **The bonus lives in the soil, not only in the harvest.** Rich land starts
   higher on the ladder and has further to fall; dry land yields less and dies
   sooner. Deserts appear on the dry margins first, river valleys hold out
   longest. (Losing option: a separate output-only yield multiplier with
   uniform degradation. Simpler and safer, but the desert spreads the same way
   whether you farmed the valley or the badlands, and a canal only raises
   output, it never improves land.)
2. **Output reads live fertility, per claimed tile.** The M15 taper
   `ceil(rate * inBand / ClaimCount)` becomes
   `ceil(rate * sum_{in-band claims} fert_t / (ClaimCount * BandBaseline))`.
   A field visibly tires before its band flips. (Losing option: weight by the
   static baseline only, keeping today's binary wear. Fewer knock-on changes,
   less feedback for the player. Rejected because soil care should be a daily
   thing, not a threshold panic.)
3. **Rivers are water.** A tile with any river edge is at water-distance 0 for
   the baseline AND for the M21 latch lift and the M27 boosted recovery.
   (Losing option: baseline only. A riverside farm would look like the best
   land on the map and then latch to permanent desert exactly like a dry one.
   A broken promise; not shipped in that state.)
4. **Canals lift the baseline instantly.** On completion, after the old-rate
   catch-up, the stored deviation is left untouched, so every tile in range
   reads its fertility higher by the baseline delta on the same tick the water
   lands. (Losing option: keep absolute fertility continuous and let boosted
   recovery climb to the new ceiling. Rejected by the user: canals already
   cost time, resources and tiles; the payoff should be immediate.)
5. **The per-tile baseline goes on the wire.** Brains and the client read it
   like every other fact. (Losing option: a shared pure helper run client-side
   on visible terrain. Rejected because forest depth must read the WORLDGEN
   biome while the view carries the CURRENT biome, so a client-side derivation
   drifts exactly around worked land.)

Degradation, recovery, the implicit latch, the step penalty and the sparse
deviation storage are all unchanged. They run against an uneven floor.

## Why

**Micro-siting becomes a decision.** Within a biome, placement was
aesthetic: every grassland tile started at 5000. Now *this* riverside tile vs
*that* dry one is a visible choice with a visible payoff, and the strategic
map deepens one level down: river valleys and old-growth hearts are premium
land inside biomes, the waterfront premium extended fractally.

**It composes with what exists.** Rivers get the gameplay payoff their doc
promised. Canals become fertility *engineering*, not just latch insurance.
Degradation gets richer ruin geography for free. The Grand World halo pattern
holds because both derivations are bounded box scans over seeded terrain.

**Why the baseline and not a new variable.** `Fertility` stores sparse
deviation from a derived baseline; `BaselineFertility` is a switch on the
worldgen biome. Making that switch tile-aware touches nothing about storage,
catch-up, or the latch. Zero new per-tile state.

### Why the offset is clamped inside the band

The baseline is load-bearing in four places beyond "the floor": `Band()`
compares absolute fertility to fixed thresholds; the degrade step penalty
snaps to the next band's baseline; the deviation clamp is `[-baseline, 0]`;
and the Homesteader's rotation thresholds are absolute. An unbounded offset
would flip a rich grassland to Forest at deviation 0 and green raw desert near
water (explicitly deferred in `docs/canals.md`). Clamping keeps every band
identity exactly what worldgen said, and the snap target becomes
`BandBaseline(nextBand) + EnvOffset(tile)`, so a rich riverside forest
degrades to a rich riverside grassland.

### Why forest depth reads the worldgen grid, and why the feedback loop cannot exist

The lazy field (`architecture.md` section 2.5) is exact only if rate and
baseline change at events. Derived biome (`BiomeAt`) changes at arbitrary
ticks with no event, so a depth read from it would move this tile's baseline
when a neighbour crosses a band, with no catch-up anywhere. Depth therefore
reads `TileGrid.BiomeAt` (worldgen, mutated only by canals). Consequence: the
"deep camps erode the gradient that attracted them" spiral the addendum
feared is designed away by the invariant. What survives is static: deep camps
have more fertility to burn, and longer haul lines.

### Why strength 0 must be byte-identical to today

Every knob defaults such that `EnvOffset == 0` reproduces M9/M21/M27 numbers
exactly (the same discipline as `WaterRecoveryAmount == RecoveryAmount`).
All existing suites and lab curves stay green through Phases A-D; the sweep is
a config edit; and the balance decision (where the temptation zone sits) is
made from lab output, not baked into code.

## Architecture

- `Sim.Core/Biomes/WaterProximity.cs`: `DistanceToWater(world, tile, maxR)`,
  Chebyshev distance to the nearest `Biome.Water` tile OR tile with a river
  edge, capped at `maxR + 1`. `IsNearWater` becomes `DistanceToWater <= R`
  (river-aware, decision 3).
- `Sim.Core/Biomes/ForestDepth.cs`: rings to the nearest non-Forest worldgen
  tile, capped. Bounded box scan; same shape as WaterProximity.
- `Sim.Core/Biomes/EnvironmentalFertility.cs`: `Offset(world, tile, config)`,
  clamped inside the band. PURE.
- `BiomeDegradation.BaselineFertility(world, tile, config)` overload; the
  biome-only overload stays for off-ladder callers. `FertilityAt`, `CatchUp`,
  `DeriveRate`, `ApplyMath` snap targets all take the tile-aware baseline.
- `BiomeDegradationConfig` gains the knobs (serialized: FormatVersion 32 -> 33).
- `BuildCompleteEvent` canal branch: affected set = union of
  `WaterRecoveryRadius` and the fertility radii (water AND forest-edge, since a
  canal through woods moves the edge). Catch-up under the old rate, anchor,
  mutate. No deviation adjust (decision 4).
- `ProductionTickEvent`: taper formula swap (decision 2). Quarry/Mine
  untouched (off-ladder).
- Wire (as built, Phase E): `StructDto.ClaimBaseline` beside `ClaimFertility`
  (own-only, parallel); v1 `TileDto.Baseline` on every visible and remembered
  tile (what the brains read; emitted live, since the genesis terrain it
  derives from is already public on v2, so the only possible leak is an
  unseen canal lift); v2 `WorldDto.Baseline[]` at genesis (memoised per
  config in the projector from the frozen map) plus `ViewV2Dto.BaselineOverrides`
  for LIVE tiles a canal has moved. Prod client mirrors all three;
  `KnownWorld.BelievedBaseline` = genesis, overridden where seen, -1 under
  fog or on a pre-M35 server; `SoilGrade.Worn` reads wear against the tile's
  own baseline while the absolute grade stays.
- Brain: `ThinkContext.NearestPocketTile` now scores each candidate pocket by
  the summed baseline of the claim the server would auto-select there; ties
  fall to the old (distance, y, x) order, so strength 0 sites exactly as
  before. `AiConfig.RestSoilBelow` stays absolute (it guards the absolute
  latch); a resting farm resumes at
  `max(RestSoilBelow, min(ResumeSoilAbove, worstBaseline - ResumeSoilWornWithin))`,
  which is 4500 at a flat 5000 and so unchanged at strength 0.

Perf note: `FertilityAt` gains two box scans per call and `ViewProjector`
calls it per own claim tile per projection. Start pure; all call sites go
through one function, so a cached environmental field rebuilt at canal
completion (the `docs/canals.md` near-water index) is a drop-in if the F9
harness shows it.

## Knobs (all on `BiomeDegradationConfig`, defaults = strength 0 until Phase F)

| Knob | Meaning |
|---|---|
| `WaterFertilityRadius` | rings within which water raises the baseline |
| `WaterFertilityBonus` | offset at distance 0, falling linearly to 0 at radius+1 |
| `ForestDepthRings` | depth cap |
| `ForestDepthBonusPerRing` | offset per ring of depth (Forest tiles only) |
| `DryEdgePenalty` | negative offset for ladder tiles beyond water reach (the "ordinary site is survivable" floor is the clamp) |

Cautions carried from the addendum: the gradient must be noticeable, never
mandatory. If riverside doubles output, every kingdom is a riverbank strip.
Target the temptation zone in the Phase F sweep.

## Future expansion

- Scatter density reflecting the gradient (prod client, the micro-biomes
  per-community density hook). Visual only.
- Start picker preferring riverside grassland.
- Cached environmental field (see perf note).
- Canal draining = the reverse baseline transition through the same hook.
- Greening raw desert: still deferred; the clamp is the guard.
- Hills/Mountain ladder extension would give Quarry/Mine a gradient.

## Acceptance tests

- Strength 0: every existing biome/water/canal/claim/lab test unchanged.
- `Offset` is a pure read (100x no mutation) and box-bounded (chunk-local ==
  global on the same seed).
- Riverside farm: never latches; recovers at the boosted rate.
- Observation independence across a canal completion with a nonzero offset.
- Snap keeps the offset: a +d forest degrades to `GrasslandBaseline + d`.
- Taper: output monotone in the fertility sum; >= 1 while any claim tile lives.
- Twin-run + snapshot round-trip on a generated world with rivers and
  strength > 0; FormatVersion 33 restores the knobs.

## References

- The source addendum (pasted 2026-09-20, "Environmental Fertility, Decision
  Addendum"); verification against code recorded in `docs/m35-status.md`.
- `docs/biome-degradation.md`, `docs/canals.md`, `docs/rivers.md`,
  `docs/extraction-claims.md`, `docs/architecture.md` section 2.5.

## Update 2026-09-20 - grading against the band, and the placement preview

**Decision (user):** the soil outline is graded against the BAND a structure
works, not the whole ladder, and the same outline appears while plotting a
build.

Why: the absolute grade (desert edge = 0, forest edge = 1) put a fresh farm at
0.5 and a fresh lumber camp at 1, so farmland read as bad land and forest as
good land by construction. Now `FertilityRulesDto` carries the flat band
baselines, and the prod client's `SoilGrade.OfBand` grades a claim tile from
the floor where its structure would LOSE the tile (DesertThreshold for
Grassland kinds, ForestThreshold for Forest kinds) up to the tile's own
baseline: fresh = green everywhere, red = about to lose it. The bubble's
alarm uses the same grade.

Placement: with a claiming kind in hand, `SelectionRenderer.DrawClaimPreview`
mirrors `Claims.AutoSelect` over the believed map (rings out to claimRange in
(distance, y, x) order, first claimCount free tiles of the required biome)
and outlines each predicted claim tile by `SoilGrade.SiteQuality`: the
board's glow for typical land for that band, toward green above it, toward
amber below, never red (no unbuilt land is "gone"). It reads
`KnownWorld.BelievedBaseline`, so on a pre-M35 server it degrades to the
plain outline. Compile-checked, not yet seen in Play.
