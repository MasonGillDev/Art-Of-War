# River rendering: a carved channel and a flow-aligned ribbon

Client-side decision (`Art Of War(prod)`), companion to `docs/rivers.md`, which owns
the sim model. Nothing here touches the wire or the server.

## The decision

Rivers are **baked with the terrain relief, not draped over it**. Inside
`TerrainSampler.BakeRelief`, after erosion and the coast and before the flow map,
`RiverCourse` turns the genesis edge mask into directed, widthed, levelled
centrelines and **carves a channel into the relief grid** under each one. The water
is then a flat ribbon on that baked level with **flow-aligned UVs** (u across the
river, v = distance to the sea), drawn by a purpose-built `Aow/River` shader that
scrolls ripples and foam along v, tints by scene depth, and is lit by the shared
`AowLighting.hlsl`. Stream width comes from **stream order** and is floored by the
relief grid's node spacing.

## Why

### The draped ribbon could not be fixed in place

The first renderer built a fixed 6-unit ribbon over the finished ground with vertex
colour only. It read as a painted line for three reasons with one cause: the ground
had no channel, the ribbon had no flow direction or width, and it needed a 4-unit
lift so eroded ground did not poke through between samples. All three follow from
the ribbon being computed *after* the terrain, with no power to change it. Moving
the course into the bake removes the cause: the channel is in the mesh, so every
consumer of `TerrainSampler.HeightAt` (units, props, roads, the grass carpet, the
ribbon's own banks) rides down into the valley with no special cases, and the water
level is a number the ground was cut below, so no lift is needed.

### The ordering trap, and why the course is a bake product

The centreline bends toward the lowest ground, and carving changes the ground. So the
course is built against the *un-carved* relief and carved afterwards, once, in a
fixed order: erode → coast → course → carve → flow. Building it per fog change (as
the old `RiverNetwork` did) would have had to either re-read carved ground (a
feedback loop) or store the un-carved grid forever. Fog now gates only what is
*drawn* (`RiverMeshBuilder` checks either bank is known); topology is public on the
wire anyway, so baking the whole course is not a leak.

### Width is set by the mesh, not by taste

Relief nodes are `TileSize / TerrainSubdivisions` = 25 units (250 m) apart. A channel
narrower than a node cannot be drawn: no vertex falls inside it, and the straight
mesh between two nodes cuts through the water. So the smallest stream is about a node
wide (`RiverMinHalfWidth` 12) and a main stem two or three (`RiverMaxHalfWidth` 22),
with a `BankLip` that holds the bed below the surface a little past the ribbon's
edge so the ground/water crossing always lands outside the drawn water. On
kilometre tiles from the strategy camera that reads as a river valley, which is the
game's view. The alternative — locally refining the terrain mesh along rivers to
allow 60 m streams — is real work (chunk topology, HeightAt, every sampler) and is
deferred until the look asks for it.

### Direction and level from the graph, not from height

The wire mask has no direction. Height alone is unreliable: the client's erosion and
coast passes are decorative and can leave a local rise along a reach the server
carved downhill. So direction comes from **breadth-first distance to an outlet**
(a corner touching sea or the map edge, which the server guarantees every river
reaches), with height only breaking ties. The water level is the corner's ground
followed by a **running minimum downstream**, relaxed to a fixed point, so the
surface never climbs along the flow and meets the sea plane flush. Stream order
(edges draining in) gives width on a log scale, and a corner takes the widest edge
touching it so a tributary flares into the stem it joins instead of stepping.

### One shader, not a pack material

Synty's water graphs pan in a fixed UV direction and have depth fade, which would
have worked on the ribbon, but they lack bank foam by u, rapids by baked turbulence,
and the project's shared lighting. A hand-written URP shader beside `Aow/Terrain`
keeps one lighting path, needs no editor authoring to iterate, and takes only a
tiling normal map from the packs (`PT_Water_NM_01`, bound by *Window > Aow > Set Up
River Material*). Without any material assigned the renderer makes a bare
`Aow/River` material at runtime, so the world never shows a white river.

## What gets built

- `RiverCourse` — graph direction, order, width, level, centreline samples
  (`Position` on the surface, downstream `Tangent`, `HalfWidth`, `Flow`,
  `Turbulence`), and `Carve()` returning a river-distance field in half-widths.
- `TerrainSampler.CarveRivers` in the bake; `Rivers(world)` and
  `RiverDistanceAt(world, x, z)` accessors; `Invalidate` clears both.
- `TerrainReliefSettings` river knobs: min/max half-width, order for max width,
  depth ratio, bank width ratio, rapids drop. Re-bake applies them.
- `RiverMeshBuilder` — ribbon per known reach with uv0 (u across, v flow in channel
  widths), vertex colour rgb tone + a = turbulence, up normals.
- `Shaders/AowRiver.shader` — ripples (two normal layers scrolling along v), depth
  tint and opacity from the camera depth texture, shore/shallow/rapids foam from
  value noise stretched along the flow, sun specular + sparkle, a little sky.
- `Editor/SetupRiverMaterial.cs` — builds `Assets/Materials/RiverWater.mat`.
- Placement: `NatureScatterSet.Entry.Bank` affinity (+1 hug banks, -1 avoid),
  nothing scattered inside the water; grassland gains riverside willows, scrub and
  stones; the grass carpet skips the riverbed.
- Removed: `RiverNetwork` and `PresentationScale.RiverLift`.

## Future expansion

- **Units in the water.** `HeightAt` in the channel is the bed, so a crossing cohort
  wades up to its waist. Right for a ford; a `SurfaceAt` = max(ground, water level)
  clamp for figures is a one-line follow-up if it reads wrong.
- **Bridges and fords** (`docs/bridges-and-fords.md`): a road crossing now dips
  through the channel under transparent water, which is the ford look for free. A
  bridge is a deck mesh between the two bank samples of the crossed reach.
- **Rocks in the stream.** The scatter exclusion is a constant (`riverDistance < 1`);
  relaxing it for a "midstream" affinity would put boulders in the rapids, with the
  shader's depth foam breaking around them automatically.
- **Sub-node river detail.** If narrow streams are wanted, refine the terrain mesh
  along the course; the course, level and shader do not change.
- **River irrigation and moisture** are sim-side (`docs/rivers.md`); the client's
  `RiverDistanceAt` is the same signal, should biome colour ever want to green the
  banks.

## Update 2026-09-17: the river as a place — wet band, riverside, features

Three additions on the same bake, none touching the water or the wire:

- **The river fields go to the GPU.** `RiverCourse.Carve` now fills `Fields`
  (distance in half-widths, size 0..1, turbulence 0..1 per relief node) and
  `RiverField.Sync` uploads them once per bake as one RGBA32 global texture, the
  same shape as the fog and ground maps. `Aow/Terrain`'s `RiverBank` paints the
  **wet band** from it at node resolution: a shore strip at the waterline (pebbles
  for small rivers, sand for big ones, from the `Sand`/`Cobble` layers of the ground
  kinds array), mud and moss up the bank (`Field`/`Moss` layers), edges broken by
  value noise, damp-darkened toward the water. The per-tile ground overlay map could
  never do this: a bank is a few hundred metres, a tile a kilometre. Layer ids come
  from `GroundMap.Sync` as `_AowRiverKinds`; a missing kind keeps the biome ground.
- **Riverside placement.** `NatureScatterSet.Entry` gained `Waterline` (the wet
  strip, a quarter half-width past the edge), alongside `Bank`, and `Midstream`
  (0..1: may stand IN the river where turbulence is high, lifted so its top breaks
  the surface via `TerrainSampler.RiverSurfaceAt`). The builder's vocabulary:
  `Where.Shore(w)`, `Where.Banks(b)`, `Where.Rapids(m)`. Each biome has a riverside
  mix: grassland reeds/willows/scrub, forest ferns/reeds/mossy stone, hills boulders
  and torrent stone, mountain bare stone, desert a single green line of reeds and
  dead trees. The grass carpet keeps off the wet shore.
- **Features.** Read off the corner graph: a **mouth** (outlet corner on the sea)
  stamps a sand fan into the fields; a **confluence** (two or more reaches in)
  stamps a gravel bar. Fords wait for the bridges slice, because roads change at
  runtime and the field is baked.

Deferred: sub-node mesh refinement for narrow streams; stepping stones and fords
(need road data at draw time); a wet-sand normal map on the shore.

## Update 2026-09-20: the relief grid dial, the bake cache, and refinement along rivers

- **Nodes per tile is a dial (4 or 8), not a constant.** `WorldGeometry.TerrainSubdivisions`
  now reads `TerrainSampler.Subdivisions`, the value the CURRENT BAKE used, so a dial moved
  in the Inspector cannot re-index a grid baked at another density; it takes effect on
  Re-bake. 8 is the default after an A/B: at 125 m nodes erosion connects its drainage
  into branching networks and the coast becomes a cliff line instead of tile steps.
  Cost accepted: ~4x bake, ~4x terrain vertices, ~4x fog recolour.
- **The eroded grid is cached on disk** (`ReliefCache`, `persistentDataPath/ReliefCache`,
  key = genesis grids + seed + size + nodes + height-curve dials + erosion and thermal
  settings; river and coast dials excluded so they re-bake from the cached grid in a
  second). Bump `FormatVersion` when the erosion code changes.
- **Refinement along rivers.** The carve profile is a formula (`RiverCourse.Target`), so
  the mesh can evaluate it BETWEEN nodes: cells within `RefineReach` (3.6 half-widths)
  of a river split `RiverRefinement` (default 4) times per side, vertices on
  `RiverCourse.ProfileAt` over the un-carved ground (kept as `_uncarved`). At 8 nodes
  that is ~30 m along banks. Exact at nodes, so the surface is continuous; a refined
  cell's border with an unrefined neighbour is linear between the shared corners, so no
  cracks. `HeightAt` reads `SubVertexHeight`, the same function the builder emits, so
  everything standing near a river stands on the drawn surface. Only cells near rivers
  pay. The coast is NOT refined: its shape is a distance transform over nodes, not a
  formula, and at 8 nodes it reads well enough.

## Update 2026-09-22: the water as a heightfield, and the longitudinal profile

Four Play screenshots from the user (a Valheim reference beside them) showed what the
strip ribbon could not do: two strips overlapping at a shared corner drew a seam on the
depth tie or, where their end lines diverged, a wedge of dry ground inside the river; a
tight bend folded; the mouth ended in a straight cut over the sea plane; up close the
shoreline was a sawtooth along the terrain's triangles, because the flat sheet was cut
by the depth buffer one triangle at a time. And every hill river ran as a tilted sheet —
the level was the land's own slope, 5 to 15 units per boundary — which read as a water
slide with ripples scrolling down it.

**Decision 1: the water is a heightfield layer over the terrain grid, not a strip per
reach.** `RiverCourse.Carve` now bakes the water surface per relief node — `Level`,
`Flow` (the shader's v) and `Across` (its u), bank-weighted means over every reach
touching the node, `NoLevel` beyond every bank — and `RiverMeshBuilder` walks the same
refined cells the terrain splits, reads the DRAWN ground (`SubVertexHeight`) and the
surface (`TerrainSampler.TryWaterAt`) at each sub-vertex, and emits water where the
level is above the ground, clipping each sub-cell to the shoreline by interpolating
`level - ground` to zero along its edges. One mesh per terrain chunk under a `Rivers`
root, rebuilt only when fog moves over a river in or beside the chunk.

- Why this and not "chain the strips into one per river": chaining removes the corner
  seams but keeps the depth-cut shoreline (the sawtooth), the confluence join, and the
  fold clamp. The heightfield has no reach ends at all, so every one of the photographed
  faults is structurally absent rather than patched; the shoreline is a mesh edge lying
  on the terrain's own surface at any angle and distance; and a lake or a pool draws the
  same way the moment the fields carry it.
- Cost accepted: about one vertex per wet sub-cell (3.1 units at 8 nodes, refinement 4)
  instead of two per 4-unit sample — a big river through a chunk is tens of thousands of
  vertices, culled per chunk. `RibbonOverlap` and the inner-bend clamp are gone.
- `RiverSurfaceAt` returns the baked level, so rocks in the stream and anything else that
  asks stand on the drawn water.

**Decision 2: the water surface has a longitudinal profile of its own.** After the
running minimum, every upstream corner is pulled down to within `RiverMaxGrade` (0.01,
one unit per boundary) of the corner below it, relaxed downstream-first to a fixed point,
floored at `RiverMaxIncision` (40 units) below the level the bank gave it. The carve is
unchanged: a lower level cuts deeper, so flat water and a gorge are one operation, and the
dramatic gorge the user wanted to keep is now a dial rather than an accident of the land.
Where the floor binds, the reach keeps its excess drop as ONE knickpoint (a smoothstep
over 14% of the reach, placed by position noise), so the profile is pools and falls, not a
slide. Turbulence became per sample from the surface's own slope, so only the fall froths.

- Why not fix it in worldgen (lower sources, lake outlets): a lake-to-sea river is flat
  today only because lakes sit at sea level, which is itself a flaw; give lakes a level
  and the outlet descends again. The profile is needed either way, and hill sources are
  where the gorges come from. Lake outlets and lake levels stay on the list as the next
  step if the result still does not read (the user's call, 2026-09-22).
- `RiverMaxIncision = 0` is the old behaviour.

**Deferred from the same screenshots:** fading the last reach's tint and alpha into the
sea at the mouth; wandering the coast boundary before its distance transform so the
sea's edge is not tile-square where a river meets it; the tile-border overlay showing
through the water. Compile-checked, not yet seen in Play.

### Same day, after the first Play run of the above

Two faults survived: the shoreline was still a staircase, and every reach fell. Refinement
was on, so the staircase was not resolution: it was the flat lip shelf with a tall gorge bank
rising straight off it, which made the water mesh's `level - ground` interpolation land on
the lattice. The shore is now a ramp that crosses the level inside the lip (`LipRise`). And
clamping each corner to the incision floor put the water at the floor under every corner,
so every reach dropped its share and the river read as one long incline; the profile is now
pool-and-step: a corner that cannot hold the grade within the incision stays at its bank
level and the whole difference is one fall, with the incised pool below it. `RiverMaxIncision`
(25) is therefore both the deepest gorge and the least a fall is tall.

### Third Play round: the floating sheet

Two more shots: a triangle of water floating across a gorge at a fall, and a pool spilling
sideways over its bank to a straight edge. Both are the heightfield's honesty: it draws water
wherever the level is above the drawn ground, and two things let ground inside a reach's band
sit below the level. The level came from a ring around the corner, so mid-reach ground beside
the channel could be lower; it now comes from the lowest ground across the reach's whole bank
at every centreline point (`BandMin`, which is why the centrelines are now built before the
levels). And a reach's influence was a round cap of bank radius past its ends, so a deep pool
carved and levelled the ground under the pool above its own fall; reaches now have flat ends
(`CarveOverlap` 8 for the bed, `WaterOverlap` 4 for the surface), and the surface is evaluated
per point (`RiverCourse.WaterAt`) rather than baked per node, so a fall is a step over a sample
and not a smear over a node spacing plus a bank width. The per-node level field is gone.

## Update 2026-09-24: the refinement along rivers is removed

**Decision: rivers are drawn at the relief grid's own resolution.** The refinement added
on 2026-09-20 split every terrain cell within 3.6 half-widths of a river up to four times
per side and put the new vertices on the exact carve formula. That refinement is removed
from the production client, along with everything that existed only for it:
`TerrainReliefSettings.RiverRefinement`, `TerrainSampler.IsRefinedCell / SubVertexHeight /
RefinedHeight / RefinedNormal` and the `_uncarved` copy of the relief, and
`RiverCourse.ProfileAt`. The channel is still carved INTO the relief nodes
(`RiverCourse.Carve`), so the bed, the banks, the wet band, the water heightfield and
everything standing on `HeightAt` keep working. They now all read the same 8-nodes-per-tile
(12.5-unit) grid as the rest of the ground. The water mesh walks the same cells as before
(`CellNearRiver`), one cell at a time, with the ground at the cell's corner nodes.

**Why.**
- **It did not buy the look.** The user's verdict after living with it: "the refined river
  beds really haven't worked for what I wanted. the rivers still are shit." The rivers'
  problems are not a matter of mesh resolution (the staircase shoreline and the falls
  above were profile and level faults that refinement could not fix).
- **It was nearly the whole load.** The load breakdown added the same day measured the
  terrain's first build of its 64 chunks at 33.1 s of a 39.2 s READY, with 58,990
  river-refined cells and 4.8 s for the worst chunk. Every vertex of a refined cell ran
  four or five scans of the nearby reaches (the vertex height plus a four-sample normal).
- **It had a runtime cost too.** `HeightAt` inside a refined cell ran four
  `SubVertexHeight` evaluations for every unit, prop and road sample standing near a
  river, and the refinement kept a 16 MB copy of the un-carved relief.

**What was considered instead.**
- Setting `RiverRefinement` to 1: rejected. It kept the dial, the code and the 16 MB copy
  alive for a feature that failed on look.
- Keeping it and making it fast (precompute the refined lattice on the bake worker, or
  build chunk geometry in parallel): rejected for the same reason. Fast machinery for the
  wrong look is still the wrong look.

**Future expansion.** Better rivers are open work, and the grid-resolution carve leaves
room for any of these:
- a river surface drawn from the reaches (a ribbon or decal over the carved bed, which the
  2026-09-22 update moved away from for its seams, now without the refinement's cost to
  justify);
- a finer relief grid everywhere (`NodesPerTile` beyond 8, paid across the map);
- shader-side bank detail: the wet band, foam and a normal-perturbed bank read in
  `AowTerrain`, which costs no geometry.

The carve formula (`RiverCourse.Target`) is unchanged, so a refinement could be rebuilt
later from the history if a new approach needs geometry between the nodes again.
