# Camera: zoom-out limited by what you have explored

**Status: BUILT 2026-09-17** — see the update at the bottom. The original sketch follows; the numbers and the
feel are not. Built last of the current look work (see *Order* below).

## The idea

How far a player may pull the camera out is **bounded by how much of the world they
have explored**. A new kingdom can only zoom out far enough to see its own few tiles.
As the fog retreats, the ceiling rises, until a well-scouted realm can pull back to
the whole map.

A player who has not explored cannot simply lift off and survey. They fly low through
the world instead, and that takes time.

## Why this is worth building

The design already says knowledge is the resource: fog is three-state, memory is
stale, scouting is a real act. But the CAMERA has been exempt from that the whole
time — a brand new player could pull back to a 14,000-unit view and take in a
continent they know nothing about. Fog hid what was on the land, while the shape of
the coastline, the mountains and the scale of the world were free.

Tying zoom to exploration makes the map itself something you earn, and it gives
scouting a second, immediately felt payoff beyond revealing tiles: **the view gets
bigger.** It also puts early play at the altitude the art board is drawn at — the
oblique diorama band, where the game looks best — instead of at map altitude, where a
new player would otherwise sit.

## Sketch of the mechanism

- **Input:** explored tile count, which the client already knows per tile
  (`KnownWorld.FogCounts`, including remembered). No new wire data.
- **Output:** `CameraRig.MaxDistance` becomes dynamic rather than a constant 14,000.
- **Curve:** a floor generous enough to see your own settlement and its surroundings
  from day one, rising with the square root of explored area — area grows quadratically
  with what a scout walks, so a linear map would feel dead early and jump late.
- **Movement:** the ceiling eases toward its new value rather than snapping, so a
  revealed frontier is a slow widening rather than a lurch.

## Questions to settle before building

1. **Does the ceiling ever fall?** Knowledge never decays to unknown — remembered is
   still knowledge — so the honest answer is probably no, and the ceiling is a ratchet.
2. **What about the strategic view and the minimap?** Both currently show the whole
   map. If the camera is bounded by knowledge then so are they, or the rule leaks.
3. **Floor value.** Too low and the opening is claustrophobic; too high and the
   mechanic does nothing for the first hour.
4. **Does it apply to the dev reveal-all switch?** Yes, or fixture captures and Look
   Lab bookmarks at full zoom stop working.
5. **Is it presentation or rule?** It changes what a player can see and therefore how
   they play, so it is a RULE expressed in the client. If the sim ever needs it (an AI
   fairness question, say), it moves server-side — same commitment the light cycle made.

## Risks

- **Frustration.** A player who wants to look around and cannot may read it as the
  camera being broken rather than as a mechanic. It needs to be legible: the ceiling
  should be visibly tied to the fog, ideally with a cue when it rises.
- **It fights the Look Lab.** Bookmarks are absolute camera poses; a knowledge-bounded
  ceiling could make one unreachable. Fixtures record knowledge too, so the fix is to
  honour the fixture's own explored area.

## Order

Camera work comes LAST of the current look pass, because both preceding tasks change
what the camera is looking at:

1. **True scatter** with a real nature asset pack — the biggest single look win, and
   the thing the rescale was blocking.
2. **Real materials**: kill the floating placeholder geometry, give terrain, water and
   districts real surfaces.
3. **This.**

## Update 2026-09-17 — built, with map mode and a solid ground

Built in the production client (`Art Of War(prod)/Assets/Scripts/Client`): `CameraRig`,
`ZoomHorizon`, `Hud/MapPainter`, `Hud/Minimap`, `Hud/WorldMap`, `Hud`, `GameBootstrap`.
Three decisions were made on the way that the sketch above left open.

### 1. The ceiling is a ratchet on a square-root curve, and pitch follows ABSOLUTE distance

- `ceiling = lerp(CeilingFloor, MaxDistance, (explored / (CeilingFullAtExplored × tiles)) ^ CeilingCurve)`
  with explored = live + remembered. First defaults (2000 / 14,000 / 20% / square root)
  were judged far too generous the same day: "I can zoom out too much" and "drastically
  reduce the amount of zoom out and the rate you gain it". Current defaults: floor
  **1,200**, absolute max **7,000** (the whole-map view is the full-screen map's job, one
  notch past the ceiling, so the 3D camera no longer needs to reach it), full at **60%**
  of the map's tiles, curve **1** (linear in explored area — the root front-loaded the
  gain). On a 252² map: fresh castle ~1,230; 4,000 tiles ~1,800; 10,000 ~2,700;
  20,000 ~4,250; 38,000 the max. All inspector knobs, retuned by feel.
- **It never falls.** Remembered land is knowledge, so the target only ever rises
  (`Mathf.Max` on the previous target); it is reset only when a new world is framed. The
  eased ceiling glides up over ~1.5 s so a revealed frontier is a widening, not a lurch.
- **Pitch is a function of absolute distance**, `InverseLerp(MinDistance, MaxDistance)`,
  *not* of the fraction of the current ceiling. This is what makes the early game
  intimate: at a 3,400 ceiling the camera is at ~33°, oblique, the diorama band; the
  steep 68° map-reading angle only arrives with a scouted realm. Had pitch tracked the
  ceiling instead, a new player would get top-down over their five tiles. `Zoom01`
  (figure scaling, atmosphere thinning) is absolute for the same reason: how far away
  the world is does not change with what you know.
- **Reveal-all is covered for free**: it marks every tile Live in `KnownWorld`, so the
  count is the full map and the ceiling is `MaxDistance`. Fixtures carry their own
  knowledge, so a fixture's ceiling is that fixture's. `LimitZoomByKnowledge` is a dev
  switch to turn the rule off.
- **The Look Lab bypasses it.** `SetPose` clamps to the absolute range, not the ceiling:
  a bookmark is a tool's instruction, not play. The next scroll-in clamps back.
- **The minimap and the strategic view do not leak.** The minimap paints only known
  tiles; the Tab strategic view is the 3D board seen through the same rig, so the ceiling
  applies to it too; unknown tiles are blank in every layer.

### 2. One more notch at the ceiling is MAP MODE

The sketch stopped at "the wheel stops". Now the ceiling is a door: with the zoom
resting on the ceiling (for at least `MapEntryDwell`, 0.25 s, so a hard flick cannot
skip through), one more scroll-out puts the rig in map mode. The rig raises
`MapModeChanged`; the HUD swaps the world for `WorldMap`, a full-screen chart of what the
player knows, and hides the corner minimap. Scroll in, Esc, or click a tile (which flies
there) returns. While in map mode the rig takes no pan or orbit input; leaving puts the
zoom one notch inside the ceiling so re-entry is deliberate.

**One texture, two windows.** Minimap painting was extracted into `MapPainter`; the
minimap and the full map both bind the same `Texture2D`, painted once per tick, so they
can never disagree, and the full map inherits the rule that unknown is dark. `WorldMap`
is a first cut and is meant to grow (markers, banners, orders from above): it is a layer
stack over one texture whose eye and click already speak in tiles.

**Legibility.** For four seconds after the wheel stops at the ceiling, the action-hint
line under the dock says why and what the next notch does. The wheel must never read as
broken.

Alternatives ruled out: a hotkey-only map (the whole point is that the map is where the
zoom *goes*); making the full map the Tab strategic view (that is the 3D board, useful at
any zoom, and it keeps the weather-off data lens job); a second camera for the map (the
texture is cheaper and cannot over-reveal).

### 3. The ground is solid: the pivot rides the surface, the eye clears the relief

The old rig set the pivot's height once, at frame or jump, and never again. Pan onto a
mountain and the camera went under it; pan into a valley and it floated; and the near
limit was set against flat ground, so a canyon floor could never be reached at a low angle.

- **The pivot follows the drawn surface** (`TerrainSampler.HeightAt`, floored at the sea
  surface so the camera floats on water rather than diving to the seabed), eased over
  ~0.12 s so relief is a glide and not a stair. Jumps set it outright.
- **Ground avoidance steepens PITCH, not position.** Eight samples along the pivot→eye
  ray; wherever terrain plus clearance rises above the ray, the pitch is raised to
  `atan2(needed, reach)`, three passes to converge as the footprint shrinks. Pushing the
  eye straight up would break the orbit (the camera would stop looking at its pivot);
  steepening keeps the pivot centred and is what a real camera does backing into a
  hillside. Manual tilt is overridden only for the frames the ground demands.
- Clearance is `GroundClearance` (20) plus 4% of distance so a far camera never skims a
  ridge the near plane would cut. `MinDistance` dropped 120 → 70 now that the pivot is on
  the floor, so you can get down onto a canyon floor at a low angle.

### Still open

- The numbers. Floor, full-at fraction, dwell, clearance and the new min distance are
  first guesses to be tuned by feel in the inspector.
- The map itself is a chart and an eye. Everything the design wants drawn on it
  (settlements, armies, orders from above, routes) is the next piece of work, and it
  lands in `WorldMap` on top of the shared texture.
- Whether the ceiling should ever be a *server* rule (AI fairness) — same commitment as
  the light cycle: the client expresses it today, and it moves if the sim ever needs it.
