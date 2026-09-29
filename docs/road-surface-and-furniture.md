# Roads wear in: surface and furniture by condition

## The decision

A road's appearance in the production client is a **function of its arc's condition**,
read through one client-side ladder (`RoadWear`: Trace → Track → Trail → Paved), and
that ladder drives three things at once:

1. **The surface**, painted by the **terrain shader itself** from a **distance field of
   the road network's segments** (`RoadField`, uploaded to the GPU and bucketed by a
   25-unit cell grid). Per terrain pixel: distance to the nearest road, an across
   coordinate, and the wear there; from those, layers of the ground kind array the
   terrain already wears — bare dirt through grass, a full dirt track with wheel ruts
   and a grass crown, pebbles worked into the dirt, then cobbles with a kerb line —
   height-blended into the ground. There is **no road mesh**.
2. **The furniture**, placed by rules (`RoadDressingSet` + `RoadDresser`), the way a
   yard is dressed by a `TileRecipe`: stones kicked to the verge of a path, a stump by
   a track, a cairn at the tile line of a trail, a signpost at a paved junction, a
   stone wall between a highway and the fields.
3. **What keeps off it**: the grass carpet and the nature scatter ask
   `RoadProximity` (the same segments, on the CPU) and clear the carriageway and a
   verge; a trace only thins the grass.

Nothing changes on the server or the wire. The arc condition (`RoadDto.condition`,
0..1000) was already there; this is presentation reading it properly.

## Why

**The road used to be one look at every condition.** A draped ribbon of Mud_01 with a
tone, drawn at condition/100 (a leftover from the tile-era 0..100 scale; the sim's max
is 1000, so every road was at its narrowest). The sim spends real effort on condition
— diminishing-returns gain per crossing, constant decay — and the player could not
see any of it. Roads are the one piece of infrastructure the player builds *by
walking*, so showing them wear in is showing the player their own kingdom's traffic.

### The ladder is client-side, and one place

`RoadWear` holds the three thresholds and the width and edge curves; the terrain
material's Roads dials default to the same numbers; the dresser gates entries by
stage; the grass and scatter layers read stage from the same class. The sim knows
nothing of stages — it has a number — and that is right: how much traffic makes a
road *look* paved is an art decision, retuned by eye.

### A distance field in the terrain shader, not a mesh — the decision that took two tries

The first build kept the ribbon and gave it a wear shader, drawn transparent so a
trace could be dirt through grass. In Play (2026-09-18) the user's verdict: the trace
"looks good and blends", the defined stages "go back to ribbons", and the junction
fans left holes at nodes. The user had expected the road to be *painted onto the
terrain with blends*. Both faults are what a mesh over the terrain **is**:

- an opaque strip has an edge of its own, so it reads as laid on the land rather
  than worn into it, whatever its texture;
- every junction is a place where two pieces of mesh must be made to meet, and
  drawn transparent they must not overlap either (a double blend is a dark blot).
  The fan built to satisfy both did not cover the disc in practice.

A distance field has neither problem. The terrain pixel asks "how far am I from the
nearest road, and how worn is it" and paints accordingly; the distance to the *union*
of the segments is the union, so two roads crossing merge with no junction code at
all. Wear is averaged between the roads within reach, so a track leaving a paved
crossroads fades out of the cobbles (the user asked for exactly this softness: the
ground bleeding into the road and two road types blending at their meeting).

Alternatives:

- **A baked road texture** at relief-node resolution (`RiverField`'s shape): 25 units
  a texel against a 16 to 40 unit road — the "world-space splat" problem
  `roads-as-draped-ribbons.md` already rejected. Fine enough texels would be a
  600-million-texel map.
- **Fixing the fan**: even a perfect fan is still an opaque strip with an edge.
- **Stencil "first fragment wins" or lifted depth-tested patches** for the overlap:
  worked through on paper for the mesh version; moot now.

Cost: a pixel on a road tile tests the dozen or two segments in its cell; a pixel
anywhere else reads one buffer entry and stops. The forward pass needs shader model
4.5 for structured buffers, which URP on PC has.

### The surface comes from the ground kind array, not from road textures

The terrain shader already carries a `Texture2DArray` of ground kinds with per-row
tiling, tint, mean, Replace and a sibling normal array. The road samples the same
array by three layer ids (`_AowRoadKinds` = dirt, trail, paving, bound by
`GroundMap.Sync`). Two rows were added (Trail = Poly Haven `rocky_trail_02`, Paving =
`cobblestone_01`); the dirt is the yard's Soil row. Its dirt is the yard's dirt.

### Furniture by rules, and the verge knows what it is beside

`RoadDresser` mirrors `TileDresser`: deterministic from the arc's hash, a stage window
per entry, roles instead of positions. The rule that makes it *intentional*: **every
arc knows its left tile and its right tile**, each Wild, a Field (a worked plot) or a
Yard (a structure's tile). A yard side gets nothing from the road — the yard's recipe
owns that ground. A wall is laid only between the road and a field. A wagon parks only
where something is settled. A signpost stands in the freest quadrant of a node with
three or more arms. The waymark stands at the arc's midpoint, which is the tile line —
milestones mark kilometres by construction. `PropLaying` was extracted from
`TileDresser` so both dressers put a prefab down the same way.

### Clearing the road, exactly and per tile

`RoadProximity` buckets the same segments by tile on the CPU and answers distance to
the nearest road's solid edge by point-segment tests. Its **per-tile signature is by
stage, not wear**: the scatter chunk hash and the grass tile cache fold it in, so a
road wearing a band inside a stage regrows nothing, and crossing into the next stage
regrows only the tiles it touches. A global version was tried first and would have
re-placed every scatter chunk on the map every time any road gained twenty points.

## Future expansion

- **Bridges and fords** (`bridges-and-fords.md`): the dresser's per-arc `Lane` knows
  both endpoints; a Lining-like role that lays bridge segments where the lane crosses
  the river field is the shape. The surface needs nothing — the field paints under
  the water and the river draws over it.
- **Paving as a built thing**: if a "build road" intent ever exists, the Paved stage
  is where it lands — nothing here assumes wear is the only way up the ladder.
- **Snow on roads**: the road is blended before rock and snow, so a pass over a
  summit is snowed under like the ground beside it. If a cleared road through snow
  is wanted, that is one gate on the snow weight.
- **Furniture by faction or biome**: `Furnishing` has no biome or owner filter yet;
  both are one field and one check in `DressArc`.
- **`RoadNetwork.Sample.HalfWidth` and `RoadWear.MeshHalfWidth`** are the ribbon's
  leftovers; harmless, removable when nothing else wants a mesh half-width.

## Acceptance

- A road nobody walks after its first crossings is faint bare patches through the
  grass with grass still standing in it; a busy road is solid dirt with two ruts and a
  green crown; a busier one is pebbled and wider; the busiest is cobbled with a kerb.
- No road shows an edge of its own: the ground's texture shows through the verge,
  the edge is ragged on a track and a line on a paved road, and there is no lift,
  seam or step anywhere, at any zoom.
- Two roads meeting show one surface; a trail leaving a paved crossroads fades out of
  the cobbles over its first third.
- No natural prop or grass card stands in a track's carriageway; a trace keeps about
  half its grass.
- A field beside a paved road has a wall along it; a yard beside any road has nothing
  placed by the road; a milestone or cairn stands at the tile line of trails.
- Re-running only *Set Up Ground Kinds* and *Set Up Road Dressing* is all the editor
  needs; nothing is hand-placed. The road dials live on the terrain material.

## Editor handover (2026-09-18, second build)

Compile-checked (`_AowTypeCheck.csproj`); the distance-field version has not been
seen in Play. The Trail/Paving rows and the dressing set from the first build carry
over. `Assets/Materials/Road.mat` and the Aow/Road shader were deleted; the
TerrainRenderer's Road Material slot is gone (the scene may log a missing-field
warning once). Tune the road on the terrain material's *Roads* header via Look Tuning.
