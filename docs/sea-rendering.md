# Sea rendering: a written shader over a baked shore field, no mirror camera

## The decision

The production client draws the ocean with a purpose-written URP shader, `Aow/Sea`, on the existing flat sea plane. It reads two things: the depth buffer, and a **shore field** baked with the relief (per relief node, the signed distance to the waterline and the sea's depth). The sky in the water comes from the ambient probe by fresnel. There is **no planar reflection camera** and no bought water package. The terrain shader paints the beach and the seabed from the same field, so the water and the ground agree on where the shore is.

Client reference: `Art Of War(prod)/docs/water.md` §11.

## Why

**The placeholder.** From 2026-09-22 the sea was a bare plane with Synty's `Water_01` material. That material is one saturated royal blue. It has no depth, no shore and no sky in it, and the fog turned it lavender in the distance. The user's verdict on 2026-09-30 was "the ocean water looks really bad".

**What lost, and on what grounds:**

- **A bought water shader (IgniteCoders Simple Water Shader), retuned.** Tried 2026-09-18 to 09-22. It knows nothing of this world's coast, so its foam and depth came from generic depth tricks. Every attempt to make it fit added a seam: generated-GUID materials, and a vertex shader that rewrites Y. It was stripped.
- **A planar reflection camera.** Tried 2026-09-19 to 09-22.
  - It cost a second render of the scene.
  - It needed its own depth texture before Enviro's clouds would appear in it.
  - At the strategy camera's angles, a mirror shows little that the sky probe does not.
  - It was stripped with the bought shader, and is not revived here.
- **Colour from depth alone.** From straight above, a shelf ten units deep gives the depth buffer too little to ramp on. The colour has to change with distance from the shore, which only a baked field can give cheaply and identically on every client.
- **Opacity from depth under the surface.** That was the first written `Aow/Sea`, and it was also stripped. The viewing angle changed nothing, and a low camera saw the whole seabed slope. This version uses the view ray's **path length** through the water instead.
- **A mesh or a simulation (displaced waves, FFT ocean).** The sea is seen from a strategy camera, kilometres up, most of the time. Displacement would cost vertices across a whole-map plane for waves nobody sees from that height. Shore waves are the one motion that reads at that height, and a field plus a phase gives them for free.

**Trade-offs accepted:**

- The shore field is baked once. A harbour stamped afterwards is not in it, and the shader treats it as the very shore. That is right for a harbour, and it is the only stamp that digs below sea level.
- The shader composes itself over the opaque texture. A transparent object drawn before the sea would not show through it; nothing is queued there today.
- The sky reflection is the ambient probe: low-frequency, with no clouds and no coastline in it. From above, fresnel keeps it to about 2% of the colour, so this matters only at low angles.

## Harbours reach into the water tile (same day)

A dock's ground stamp now digs past its own tile into the **water** tile across its slip side, out to the real waterline. Before, the stamp stayed inside its tile (the ground-stamp rule: neighbours untouched). The coast keeps its waterline two or three relief nodes inside the water tile (`ShapeCoast`), so every harbour basin was a pool cut off from the sea by a sand bar.

- **Losing options:**
  - Move the coast's waterline onto the tile boundary. That is the staircase-of-cliffs coast that `ShapeCoast` exists to avoid.
  - Leave the gap and draw water over the bar. The sea is a flat plane showing whatever ground is below it, so there is nothing to draw it with.
- **Why the exception is safe:** a water tile carries no structures and no scatter. A land neighbour is still never dug.
- **The cost:** a stamp can now write outside its own tile. Stamps are therefore restored per dirty tile and re-applied by footprint (`StampStructures`), and every harbour write is a `min`.

The shore field is re-measured around a stamp whenever a node crosses the waterline, so the sea shader and the beach paint follow the dug basin.

## Future expansion

- **Coast wander.** The coast is still shaped along tile edges (`ShapeCoast`), so a bay can be drawn as a rectangle. Moving the waterline off the grid is a relief-bake change. The field and the shader follow it automatically, because both read the ground as drawn.
- **A reflection of the scene** (screen-space, or a probe baked per coast) can be layered into step 5 of the shader without touching anything else.
- **Wave normals on the breaking crests, foam around boats and piers.** Both would be additions to the shore term; boats would need a small wake buffer.
- **Lakes at their own level.** The field would need a per-basin level. Today lakes are sea-level water, as the sim classifies them.
- **Tuning** lives in the shader's defaults. `Window > Aow > Set Up Sea Material` resets the asset to them, as the river menu does.
