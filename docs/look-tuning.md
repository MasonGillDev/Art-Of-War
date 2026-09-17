# Look tuning: assets are the dials, the fixture is the sandbox

## The decision

Every dial that shapes the look of the land lives on a **ScriptableObject** —
`TerrainReliefSettings`, `NatureScatterSet` (its `Tuning` block and biome mixes),
`GrassCarpetSettings` — or on a material. The renderers read those assets and watch a
version number on each; when it moves, they re-apply. A single editor window
(**Window → Aow → Look Tuning**) draws the assets' Inspectors inline with the rebuild
buttons. The sandbox is a **fixture session** in Play: a frozen real world, no server.

## Why

The debug client had edit-mode previews (`FeaturePreview`, `ErosionPreview`): drop a
component in a scene, tune values, watch a synthetic patch rebuild. Two things about
that did not survive contact with a real world:

- **The patch lied.** A flat square of one biome has no valleys for the flow map, no
  ridge for the boulders, no road, no neighbour biome to meet. Values tuned there did
  not hold on the map. The production client already has the honest equivalent — the
  Look Lab's fixture session — so the sandbox is the game, frozen.
- **The values evaporated.** They lived on components. Unity discards component edits
  made in Play, so an hour of tuning was gone at the first Stop. Asset edits are kept.
  That one rule decides where every dial goes: **look dials on assets, cost dials on
  components.** Draw distances, chunk sizes and shadow ranges stay on the renderers
  because they are not the look and should not be tuned by eye.

Alternatives ruled out:

- **Edit-mode preview of the real pipeline.** Possible — the samplers and builders are
  pure — but it means running the renderers outside Play, which they were not designed
  for, for a benefit the fixture session already delivers. Deferred, not rejected.
- **Constants with a recompile.** How relief was tuned before today. A minute per try.
- **Runtime-only tweak UI in the HUD.** Nowhere to save to; duplicates the Inspector.

## How each system re-applies

| System | Trigger | Cost | Notes |
|---|---|---|---|
| Terrain relief | `TerrainRenderer.Rebake()` — button, or `AutoRebake` ½ s after the last change | 1–2 s | Re-bakes relief, erosion, flow; forces every chunk's geometry; rebuilds roads; bumps `TerrainSampler.Version` |
| Scatter | `NatureScatterSet.Version` or `TerrainSampler.Version` moved | ~100 ms–1 s | `Invalidate()` rebuilds models, re-measures chunk bounds, forces every known chunk |
| Grass carpet | `GrassCarpetSettings.Version` or `TerrainSampler.Version` moved | negligible | Drops its tile cache; re-grows near the camera |
| Entity scale | `EntityScaleSettings.Version` moved | negligible | Districts rebuild next frame (not next tick — a fixture never ticks); figures follow the camera's next `SetZoom`. Pick volumes scale with the drawing because the dial is applied inside `StructureDistricts.For` and `PresentationScale.SetZoom`, the two definitions both sides share |
| Night fires | `NightFireSettings.Version` moved | negligible | Pools destroyed and rebuilt, since colour/range/prefab are set at build |
| Ground material | Material edit | none | Standard Unity |
| Atmosphere | Preset asset edit | none | Already live via `AtmosphereDirector` |

`BuildNatureScatterSet` still writes the biome mixes from code — the authored intent —
and leaves the asset's `Tuning` block alone, so the two can be used together.

## Invariants

- **A version bump is the only signal.** Renderers never watch fields; they compare one
  int. Anything that edits an asset from code bumps it.
- **`TerrainSampler.Version` is the ground's version.** Everything that cached a height
  compares to it. A re-bake that forgets to bump it leaves scatter floating.
- **`TerrainSubdivisions` is not a dial.** It is mesh topology that every sampler
  assumes; changing it is a code change.

## Acceptance

- In a fixture session, dragging `PlainsSwell` and pressing *Re-bake* changes the ground,
  and trees, roads and grass follow it without a restart.
- Stopping Play keeps every value set in the Look Tuning window.
- Editing a biome weight in the window re-places that biome within a tick.
