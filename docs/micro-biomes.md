# Micro-biomes: scatter and grass placed by a shared site classifier

**Status: BUILT 2026-09-18 (same day), compile-checked, not yet seen in Play.** User's
calls: presentation-only bog/alpine accepted; farm hedgerows and orchards belong to tile
dressing ("human built things"), not here; tree line at the proposed band.

Files: `Client/World/Site.cs` (classifier), `TerrainSampler` (`DepositAt`, `CurvatureAt`,
`AltitudeAt`; the field normalization moved into the bake so `TerrainField` and `Site`
share it), `NatureScatterSet` (`Community`, `Shares`/`Pick`, `Tuning.Shelter`/`OpenBase`),
`ScatterRenderer` (per-community candidates, classifier-picked community, shelter in the
clump field, tree line), `GrassCarpet` + `GrassCarpetSettings.DryCards` (carpet by
community, dry cards), `Editor/BuildNatureScatterSet` (the communities),
`Editor/SetupGrassCarpet` (dry cards). The Look Lab shader/C# agreement probe is NOT
built; the formulas are mirrored by hand with a comment each way.

## The decision

Props and grass stop being placed per biome with independent per-candidate affinities,
and are placed by **communities** chosen per spot by **one site classifier** that reads
the same baked terrain data the ground shader reads. A community is an authored list
of props with its own density and grass fraction; the classifier says which community a
spot belongs to (wet fold, dry rise, scarp, alpine, wood edge, riverside…) and
everything at that spot is drawn from it. The sim's six biomes remain the only truth;
communities are presentation, chosen from terrain the sim already published.

## What exists and why it cannot make micro-biomes

`ScatterRenderer` + `NatureScatterSet` + `Editor/BuildNatureScatterSet`:

- Per tile, a fixed candidate count per biome; each candidate lands at a hashed spot
  and is kept with probability = product of six affinities (clump field, valley, slope,
  bank, waterline, midstream). Deterministic, cached per 16-tile chunk, drawn with
  instancing. This part is sound and stays.
- **Every candidate decides alone.** A fold gets more pines, but the ferns, stumps and
  rocks beside them are independent rolls. No co-occurrence, so no community.
- **Density is per biome**, so a fold cannot be denser than a ridge without burning
  rejected candidates against a per-tile cap.
- **The clump field is pure noise** (7-tile value noise), unrelated to terrain: woods
  sit anywhere, not in sheltered ground.
- **Biome is hard per tile.** Forest ends on a tile line; screenshots 2026-09-18 show a
  straight treeline with a staircase at the fog edge and no outliers in the pasture.
- **Not read at all:** deposit, curvature, altitude (no tree line, no alpine band, the
  snow zone carries the same mix as the flanks), aspect, coast distance, the ground
  kind underfoot, and the shader's own dryness/wetness. The ground can paint a dry
  shoulder while scatter plants a willow on it: two definitions of one land, the trap
  the meadow tint avoided by sharing `AowMeadow.hlsl`.
- `GrassCarpet` is a constant fraction per biome (Grassland 1, Hills 0.35) with a slope
  cap. It does not thin on scree, thicken in the wet, or change card by dryness.

## The measured bake (2026-09-18, seed of the current world)

| signal (land only) | p10 | p50 | p90 |
|---|---|---|---|
| valley (log flow, 0..1) | 0.13 | 0.25 | 0.63 |
| slope (rise/run) | 0.04 | 0.21 | 0.77 |

Deposit is centred on its median with ±59 units to the 5th/95th percentiles;
curvature ±0.12. Altitude is normalized elevation: the mountain band starts at
`HillsTop` = 0.72, snow at 0.85 (`_SnowStart` 2050 of 2400).

Budget baseline from the stats overlay at the forest/grassland view: 7.5 ms, 3,342
batches, 17.7 M triangles, 96,617 saved by batching, 2,164 shadow casters, 132 fps.
The triangles are the forest (pack trees are ~440 tris each, no real LOD below the
proxy); communities must not raise the per-tile tree count in dense wood.

## The classifier: `Site`

One C# function, `Site.At(world, x, z)`, over the baked arrays (all O(1) lookups):

| field | source | bands (from the table above) |
|---|---|---|
| slope | fine grid, rise/run | flat < 0.10 · gentle 0.10–0.30 · steep 0.30–0.60 · scarp > 0.60 |
| valley | flow map | crest < 0.18 · mid · fold > 0.45 |
| wetness | valley, +curvature (hollow), −slope | same formula as `AowTerrain.shader` |
| dryness | −curvature (shoulder), −valley, +slope | same formula as the shader |
| deposit | erosion delta, centred | fan > +0.3 · cut < −0.3 |
| altitude | normalized elevation | lowland < 0.55 · upland 0.55–0.72 · montane 0.72–0.80 · alpine 0.80–0.87 · nival > 0.87 |
| river | `RiverDistanceAt` (half-widths) | water < 1 · shore < 1.45 · bank < 3 |
| biome blend | believed biome of the 3×3 tiles, weighted by distance to the tile lines (fade ~0.6 tile) | gives an ecotone weight per neighbouring biome |
| ground kind | `GroundMap` byte | yard/plot → suppressed (already), scree/rock → no grass |

The wetness and dryness formulas are duplicated from the shader on purpose (one in
HLSL, one in C#) with a comment naming the other; a Look Lab probe compares the two at
a hundred points per bake and warns if they diverge. Deferred: aspect (needs one more
channel) and coast distance (computed in `ShapeCoast`, currently discarded).

Membership is soft: each community has a score from the bands; the spot's community is
picked from the scores by the placement hash (so a boundary is a mixture, not a line),
and the biome blend mixes the neighbouring biome's communities in at the edge.

## Communities (first cut, pack assets only)

Density is candidates per tile inside the community; the biome's old single density
goes away. Names in brackets are prefabs already imported.

**Grassland** — meadow (default: grass carpet 1.0, lone round trees, very sparse);
wet fold (valley > 0.45 & slope < 0.2: willows, bushes, reeds where wetness is high,
carpet 1.2 with the lush card); dry rise (dryness > 0.6: small rocks, dry-card carpet
0.6, no trees); scarp (slope > 0.6: rocks, carpet 0); wood edge (Forest blend > 0.2:
scattered pines and birch at a third of forest density); riverside (as now).

**Forest** — deep wood (fold, altitude < 0.72: PolyPine dense, ferns, undergrowth,
mushrooms [SM_Plant_Mushrooms], stumps and logs [SM_Tree_Stump, SM_Tree_Log]); ridge
wood (crest: birch and broadleaf, thinner, twig litter [SM_Tree_Twig]); clearing (clump
field low: grass carpet, a bush, a stump); bog (wetness > 0.7 & slope < 0.08 & deposit
fan: swamp trees, roots and growth [SM_Tree_Swamp, SM_Swamp_Root,
SM_Terrain_Swamp_Growth], lily pads on slow water [SM_Plant_Lillypad]); scree (slope >
0.6: rock clusters, no trees); meadow edge (Grassland blend > 0.2: outlier trees).

**Hills** — moor (default: undergrowth, bush, gorse-stand-in, carpet 0.35 dry card);
sheltered fold (valley > 0.45: small pines, birch, carpet 0.6); crest (valley < 0.18 &
convex: boulders, rock piles, carpet 0.2); scarp (rocks only); tree line (altitude
0.72–0.80: dead pines, sparse).

**Mountain** — montane (0.72–0.80: dead and sparse pines in folds only); alpine
(0.80–0.87: boulder fields, rubble [SM_Terrain_Rubble_Pebbles], nothing green);
nival (> 0.87: near-empty, a boulder per few tiles); scree (slope > 0.6 anywhere).

**Desert** — waste (default: almost nothing); dry wash (valley > 0.45: dead trees,
dead shrub [PT_Generic_Shrub_01_dead]); outcrop (crest & steep: rock clusters
[SM_Rock_Cluster_Large]); oasis line (river bank: reeds, one green shrub).

**Near farms** (any biome, `TileDressingRegistry` neighbour): hedgerow and orchard
outliers from the Polytope fruit trees, at very low density. Deferred to the tile
dressing work; noted so the community table has a slot for it.

Gap in the packs: heather or gorse for the moor. Bush_Leaves stands in.

## Why this shape

- **One definition of the land.** The ground and the props must agree; a shared
  classifier is the only way that survives tuning. Same reasoning as `AowMeadow.hlsl`.
- **Communities give co-occurrence**, which is what the eye reads as a place. Affinity
  products cannot: independent rolls never make a bog.
- **Per-community density** is cheaper, not dearer: the same look with fewer rejected
  candidates, and dense wood keeps its current tree count by construction.
- **Ecotones from the biome blend** cost four extra tile lookups per candidate and
  remove the tile-line treeline without touching the sim's tile truth.
- Ruled out: a second, finer biome on the wire (the sim would be lying about what it
  simulates); per-prop affinities extended with more channels (still independent
  rolls); a painted community map (not deterministic from the seed).

## Costs and acceptance

Per candidate: ~8 array reads more than today, at chunk build only. Instance and batch
counts bounded by the community densities, which are authored to match today's totals
per biome. Grass carpet cost unchanged (it is radius-bounded).

Acceptance: a fold in grassland reads as a damp hollow with willows and reeds and a
lusher carpet; a forest edge is ragged over ~1 km with outliers; a forest interior has
clearings and a bog where a fan meets a flat fold; hills have bare crests and wooded
folds; a mountain shows a tree line, a boulder band, then bare snow; frame time at the
baseline view within 1 ms of 7.5 ms and triangles not above 18 M.

## Open for the user

- Bog and alpine meadow are presentation only; the sim will not treat them differently.
- Tree line altitude (0.78–0.80 proposed) is a taste call against the exaggerated
  heights.
- Whether the Polytope fruit trees near farms belong here or in tile dressing.
