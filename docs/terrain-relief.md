# Terrain relief: a baked sub-tile surface, eroded

## The decision

The client draws terrain from a **fine grid at 4 nodes per tile edge**, baked once per
world: the shaped tile-corner heights upsampled with a Catmull-Rom bicubic, a small
fractal detail relief added on land, then **hydraulic erosion** (Lague droplet model)
run over the whole grid, seeded from `WorldDto.mapSeed`. The mesh's vertices *are* that
grid's nodes; `TerrainSampler.HeightAt` interpolates across the exact triangles the mesh
draws. Nothing about this touches the server or the wire.

## Why

**A tile is a kilometre, and a heightmap sample per kilometre has no shape below that.**
Drawn as flat quads between corners, every facet of ground was a square kilometre — the
sun had nothing to rake across, and open country read as a flat wash however it was
textured or scattered. The art board's hills have form at the scale of a field.

Alternatives considered and ruled out:

- **Higher-resolution elevation on the wire.** 16× the genesis payload for data the
  client can derive deterministically from what it already has. The seed is already on
  the wire; the bake is reproducible from it.
- **Noise-only detail.** Cheap and what shipped first, but noise is bumps, not
  landscape: it has no valleys, ridgelines or deltas, and it reads as bumps. Erosion
  produces the drainage structure a real slope has. The noise term stays only as the
  roughness the water needs to start a channel.
- **Shader-only relief (normal/parallax mapping).** Changes shading, not silhouette or
  where things stand. Roads, units and props would still sit on flat kilometre planes.
- **Re-eroding on reveal, or eroding only explored land.** Erosion is global; either
  seams the frontier or pops already-seen terrain. The bake covers the whole map once
  and fog hides what is unknown — terrain topology is non-secret by an earlier decision.

## The invariants this creates

- **One definition of the ground.** `HeightAt` is the drawn surface, to the vertex:
  same sub-cell, same diagonal split (`i00→i11`) as `TerrainMeshBuilder`. Change one,
  change both. Everything that stands on terrain samples through it.
- **Geometry is built once per chunk.** Fog moves only rewrite vertex colours. A chunk's
  16k vertices must never be re-placed per tick.
- **The bake is deterministic** from `(mapSeed, genesis elevation, settings)`. Every
  client sees the same ground; a road bends around the same rise everywhere.
- **The erosion brush is stored once**, not per node (the reference stored per-node
  jagged arrays — ~230 MB transient at this map size).
- **The waterline is the sim's; the drawn shoreline lives inside the water tiles.**
  The sim's coast is quantized to kilometre tiles, and any faithful rendering of that —
  land corners up, water corners down — is a staircase of cliffs along tile edges. Two
  versions of the coast did exactly that: a fixed-shelf clamp (a pancake with
  tile-shaped pools), then a step in the curve with land-only corner averaging (a
  plinth). The rule "land tiles dry, water tiles wet" leaves the room that fixes it: a
  water tile's *edge* may be dry beach so long as its interior is wet.
  `TerrainSampler.ShapeCoast` computes a signed distance to the coast on the relief
  grid (chamfer transform — rounded corners for free) and applies a profile over it:
  beach just above the sea at the boundary, descending to the baked seabed over
  `ShoreWidth` tiles into the water, blending back into the eroded relief over
  `ShoreInset` tiles into the land. Further inland nothing is touched. The waterline is
  the smooth contour where that profile crosses the water level — always on the water
  side, never on a land tile. `ShoreStep` in the curve is now small and only keeps raw
  land from starting exactly at the surface. All three are dials on the relief asset.

## Future expansion

- **Worker-thread bake.** The engine is pure float math; only the mesh upload needs the
  main thread. Do this if the ~1 s synchronous bake at load ever hitches.
- **Rock and snow from the fine surface.** The slope blends in `AowTerrain.shader`
  already use the per-vertex normal, so they follow eroded channels for free. Snow
  height is world Y, also free.
- **Subdivision 8** for close-camera form, ~8 M triangles for the whole map.
- **Rivers.** Erosion's flow field is the natural seed for where water should run; a
  later pass could accumulate droplet paths into a flow map.

## Acceptance

- Open ground shows light and shadow from a low sun at the scale of a field, with no
  visible kilometre-wide facets.
- No hairline of light along chunk borders.
- A unit, a road and a hut all sit on the surface with no sink or float.
- Reloading the same seed produces byte-identical terrain.

## Update 2026-09-17: the faceted look, as a switch

The board's mountains are planes with straight edges; the smooth look hides that the
ground already is. Every triangle of the terrain mesh is a plane and `HeightAt` is
triangle-exact on it, so faceting is not a second terrain — it is a lighting switch
plus a choice of how coarsely the same bake is cut:

- **`TerrainReliefSettings.Faceted`** turns it on. The shader takes each face's normal
  from the screen-space slope of the world position (`ddx`/`ddy` of `positionWS`),
  so faces are flat and edges are straight with no change to geometry. Smooth mode is
  untouched; the switch flips instantly for A/B.
- **`FacetsPerTile`** (1, 2 or 4) is the cut: how many of the 4 baked nodes per tile
  edge become facet corners. `WorldGeometry.RenderSubdivisions` reports the drawn cut;
  the mesh builder AND `TerrainSampler.HeightAt` read it, so props, units, roads and
  grass stand on the drawn plane whatever the cut. Changing it re-bakes (the mesh
  topology changes); the bake itself is identical.
- **`FacetRelief`** is how much normal-map relief a facet keeps; 0 is a true plane.

Not offered: finer than the bake (more than 4 facets per tile). That would need a
finer erosion grid — four times the nodes per doubling — and is a separate decision.
`TerrainSubdivisions` remains the bake's resolution and is still not a dial.
