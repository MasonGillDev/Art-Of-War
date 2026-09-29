# Terrain surface data: the ground is textured by what the bake knows

## The decision (2026-09-18)

The production client's terrain shader picks its ground materials from **two baked
global maps** rather than from the vertex colour and a slope alone:

- **`_AowTerrainField`** (`Client/World/TerrainField.cs`) — one RGBA texel per relief
  node (250 m): **slope**, **valley** (log flow accumulation), **erosion deposit**
  (signed: what the droplets laid down or cut), **curvature** (hollow or shoulder).
  Derived in `TerrainSampler.BakeSurface` from the same bake that shapes the mesh,
  uploaded once per bake, zero per-tick cost.
- **`_AowGroundMap`** (`Client/Dressing/GroundMap.cs`) — two bytes per tile: the
  recipe's ground kind (as before) and the **believed biome** (none where unknown).

From those the shader (`Shaders/AowTerrain.shader`) composes, per fragment: the biome's
**base** ground, height-blended toward its **dry** kind on convex, well-drained shoulders
and toward its **wet** kind in the folds; **scree** where it is steep short of a cliff;
**sediment** where the deposit is positive on gentle ground; then the river bank, cliff
rock by slope and snow by height as before. The ground-kinds asset (`GroundKindSet`)
gained a **per-biome surface table** (base / dry / wet by kind name), two **role**
kinds (scree, sediment), a per-row **Replace** dial, a **normal-map sibling array**,
and **height in the albedo alpha** so layers meet by height instead of crossfading.

## Why

The screenshots that prompted this (2026-09-18) showed the same thing at every zoom:
one texture pattern on every surface, biomes as kilometre-wide colour ramps, hillsides
one flat tan under superb lighting. The cause was not the textures. The shader had
almost nothing to decide with: a vertex colour bilinear across a 1 km tile, a geometric
normal, world height, and a single 830 m macro sample. Meanwhile the bake already
computed flow accumulation, slope and erosion and either threw it away (deposit) or used
it only on the CPU for scatter placement (valley, slope). The river bank — the one place
the ground looked like material — was exactly the place that read a baked node-resolution
field. This generalises that.

Alternatives considered:

- **More geometry (subdivision 8).** Four times the triangles for silhouette nobody sees
  from a strategy camera; the missing variation is in the surface, not the mesh.
  Micro relief goes into normals.
- **Vertex colour channels for the new data.** Vertex data lives on tile corners and
  interpolates over a kilometre; the field is 16× finer and free to upgrade
  independently of the mesh. The same argument that put the ground kinds on a map.
- **Deriving slope/curvature in the shader from `ddx`/`ddy` of the normal.** Cheap but
  cannot give flow accumulation or deposit, which are the two signals that actually
  sort a landscape (a fold is wet because water gathers there, not because it is
  concave right here).
- **A fixed splat map painted per biome.** Would need authoring per world; the field
  is deterministic from the seed, like everything else on the client.
- **Fading detail by camera distance.** Rejected again, for the reason recorded in the
  shader: it draws a disc under the camera. Every close-only term (mid macro octave,
  normal maps) is gated by **texel footprint** (`fwidth` of world position), which
  follows perspective.

Trade-offs accepted:

- Texture reads per fragment rise from ~8 to ~10–12 on open ground at street level,
  and fall back to ~8 from strategic zoom because the footprint gate turns the close
  terms off. Tile-boundary fragments cost up to 4× the surface (unchanged in kind
  from the previous 4-corner fade); those are a thin band.
- The field is 4 MB of texture (1009² RGBA32) plus the 8-layer normal array
  (512² × 13 layers RGBA32 with mips ≈ 17 MB; compress if it matters).
- Biome edges are now a 0.2-tile fade on the texture while the vertex tint still ramps
  over the whole tile. That is deliberate: the tint carries knowledge and staleness
  and must stay soft; the material change is what makes the edge read.

## Invariants

- **Knowledge is respected.** An unknown tile carries biome 0 in the ground map; the
  field itself is terrain topology, public on the wire by an earlier decision.
- **The field is a pure function of the bake.** `TerrainSampler.Invalidate` clears it;
  `TerrainField.Sync` re-uploads on the sampler's version.
- **Array build reads the PNG from disk**, not the imported asset, so import settings
  (normal-map swizzle, compression, read/write) cannot corrupt a layer.
- **Ids are append-only.** New rows go after the five the recipes were authored against.

## Future expansion

- **Textures.** The Synty ground set is stylized and shallow (11 tileables, 5 normals,
  no heights). Rows are data: drop CC0 sets (Poly Haven / ambientCG: albedo + normal +
  height) into the rows, re-run *Set Up Ground Kinds*, no code change.
- **Per-biome dryness/wetness thresholds** if one set of bands proves wrong for
  desert vs forest; the table has room for a fourth column.
- **Aspect** (sun-facing slopes drier) is one more channel; the field has none spare,
  so it would be a second texture or a repack.
- **Snow and scree from the field** rather than the geometric slope, once the field is
  trusted; today both are decided on the normal so the faceted look still works.
- **Compressed array formats** (BC7) if memory matters on a target platform.
- **Grass carpet colour** already shares `AowMeadow.hlsl`; it could read the same
  field to thin on scree and thicken in the wet.

## Acceptance

- Open grassland shows patches of dry and lush ground that follow the terrain's
  shoulders and folds, not a noise pattern and not a contour line.
- A forest tile has a floor, a hillside above the scree line reads as loose stone, a
  valley floor below an eroded slope shows sediment.
- From the boot Overview zoom, frame time is within noise of before.
- The data layers (Tab) are unchanged: flat colour, no texture.
