# The grass carpet: a near-camera layer, not scatter

## The decision

Grass is drawn by a dedicated `GrassCarpet` layer, not by the scatter system. It builds
**patches** — one instance is ten crossed upright cards of varied size and lean plus
four low, wide fill cards, ~50 triangles, baked once — **only for tiles within a radius
of the camera**, evicts them as the camera leaves, and the patches shrink to nothing
over the last stretch of that radius in the vertex shader. A patch rather than a card
because instances are what cost (a matrix each, a draw slot) and triangles are nearly
free: the same instance count carries twelve times the blades, and the fill cards put a
floor under the tufts so there is no bare ground between them. Cards are white blade silhouettes
tinted in-shader by the **same meadow noise the terrain uses**, so a blade is the colour
of the ground beneath it. Grassland at full density, hills at a fraction, nothing else.

## Why

A carpet dense enough to read as one is over a thousand cards per square kilometre. The
scatter layer's contract is "place every prop on every known tile at build and keep it"
— right for trees and boulders, which are visible from altitude and must exist
everywhere, and ruinous for grass, which is invisible past a few kilometres and would
cost tens of millions of placements at load and tens of millions of triangles in the far
ring for nothing. The two have different lifetimes, so they are different systems.

Alternatives ruled out:

- **Grass as scatter entries.** Placed everywhere, forever; see above. It also cannot
  fade — scatter is either drawn or not.
- **A grass ground texture.** The pack has none (its ground grasses are flat colour),
  and a texture cannot give blades a silhouette against the light, which is what a
  carpet is.
- **Terrain-shader "fur" or geometry shaders.** Not portable across URP targets, and
  heavier than instanced cards for the same look.

## Invariants

- **Bounded cost.** Cards exist only within `Radius`; `MaxCards` is a hard ceiling per
  frame regardless of density or radius. The far world costs zero.
- **Lit with an up normal**, like the ground. Cards facing away from the sun must not
  darken, or a field flickers as the eye moves.
- **Fade by shrinking toward the root**, in the vertex shader, driven by distance to the
  camera. Distance-fading a *prop* is fine; distance-fading a *surface* draws a disc
  (see `art-direction.md`). The carpet has no edge because cards thin to nothing.
- **Colour is shared with the ground** through `AowMeadow.hlsl`. Change the meadow in one
  place and both follow; never let the carpet and the terrain drift into two greens.
- **Deterministic per tile** from the tile hash, so the same tile grows the same grass
  each time the camera returns.
- **No shadows cast.** A carpet's shadow is its root shade; a shadow pass for tens of
  thousands of cutout cards is the single most expensive thing this could do.

## Future expansion

- **Roads and structures**: cards should not grow on a road ribbon or a district
  footprint. The road arms and claim grids exist; the carpet needs a distance test.
- **Aspect and wetness**: the flow map could thin grass on crests and thicken it in
  folds, as scatter already does.
- **Wind coherence** with the tree shader, once trees sway.
- **Height-based density** for hills and the mountain tree line.

## Acceptance

- From the board's camera height, grassland reads as a continuous carpet with visible
  blades in the near field and no visible boundary where it ends.
- Frame time at 50–60 k cards is within a few milliseconds of the same view without them.
- The carpet's green matches the ground's green in sun and in the folds.
