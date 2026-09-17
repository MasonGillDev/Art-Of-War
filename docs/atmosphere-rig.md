# Atmosphere Rig — one lighting, fog and grade stack, built before the art style

**Status:** decided 2026-09-15; not yet built. Client milestone **C3** (renumbered —
the living kingdom moves to C4). Plan addendum: `Client_build_plan.md` in the
client project, `## Update 2026-09-15`.

## The decision

Build the lighting, fog and post-processing rig **once, style-agnostic, before any
art style is chosen**: sky and sun, a time-of-day clock, colour grading, bloom,
soft shadows, height and distance fog, and the three-state fog of war restyled as
one volumetric substance. The rig is the *judging instrument* for two things that
come after it — the art-style A/B test (voxel versus a Valheim-register look) and
the C4 ambient layer — so it has to exist before either can be judged fairly.

Seven rules shape it:

1. **The Look Lab is built second, not last.** Saved cameras, repeatable
   screenshots and side-by-side presets land right after there is a sky worth
   photographing, and every later step is evaluated inside it as it lands.
2. **The A/B gate sits after the mood core.** The style verdict needs sky,
   grading, zoom curves, height/distance fog and at least the stand-in fog of war.
   It does not need god rays or weather, so those continue *after* the gate, in
   parallel with asset authoring.
3. **Build, don't buy — timeboxed, with a fallback.** The raymarched fog-of-war
   pass is timeboxed, and the existing stacked-plane renderer is the explicit
   fallback, so a hard stretch in Render Graph cannot block the style decision.
4. **Two channels in the director, one of them inviolable.** Atmosphere (haze,
   occlusion, god rays, weather thickness) fades with zoom. Knowledge (unknown /
   remembered / live) never does — the three states hold at every height, time
   and weather.
5. **Remembered land has content and mood, and both are required.** Stale-data
   rendering is the *content* of memory; a desaturating grade is its *mood*. A
   grade alone would dim the current truth and leak it.
6. **One clock and one weather source, owned server-side, with two consumers.**
   The screen and the narrator read the same mapping, so the world never
   contradicts its own historian.
7. **Shadows are solved for scale, not by a checkbox.** Zoom-driven cascades near
   the camera, horizon-mapped terrain shadows everywhere else.

## Why

### What exists today (audited, not assumed)

| Piece | State found |
|---|---|
| Engine | Unity 6.3, URP 17.3, Forward+ |
| Camera post-processing | **Off** (`m_RenderPostProcessing: 0` in `C1_World.unity`) — nothing grades or blooms |
| HDR | On, **32-bit** (R11G11B10): bands on smooth fog gradients; volumetrics want 64-bit |
| Shadows | Soft, 4 cascades, **distance 50 units** — half a 100-unit tile, against a camera that zooms 120 → 14,000 |
| SSAO | Renderer feature present |
| Scene lighting | One white directional light at intensity 1, default skybox, scene fog off |
| Default volume profile | The URP template's, still carrying test components (`CopyPasteTestComponent2`, `TestAnimationCurveVolumeComponent`, `VolumeComponentSupportedEverywhere`, …) |
| Entities, water | URP Lit — already receive shadows, SSAO and additional lights |
| Terrain | Custom `Aow/Terrain`: main-light shadows only — **no SSAO, no additional lights, no ambient probes** |
| Fog of war | `FogField` (R8, one texel per tile, bilinear, smoothed) — explicitly designed as a raymarcher's input. `FogRenderer`'s stacked planes are the stand-in drawing it. |

### The Look Lab before the experiments

The first version of this plan put the Look Lab at the end, after everything it
exists to judge. That inverts instrument and experiment: grading, shadows and fog
would each be assessed from memory and impression, and compared against a
baseline nobody captured. Built right after the first sky and sun, the Lab makes
every subsequent step land against fixed cameras and a saved "before".

It is also the **regression harness**. The night-storm readability check —
remembered land must stay distinguishable from live land at night, in a storm —
is a measured test that runs there, not a judgement made once and trusted after.

Bookmarks it should carry from day one: street-level forest, dusk over water,
strategic mid-zoom, full map zoom, and a fog frontier with all three knowledge
states in frame.

### The A/B gate, and why polish sits behind it

The verdict that unblocks C4 asset authoring is the style choice, and the style
choice depends on the *mood core*: sky, grading, zoom behaviour, height and
distance fog, and a fog of war of any quality. Nobody chooses an art direction by
its weather states or crepuscular rays. Gating the A/B behind those would make C4
wait on the polish that matters least to the decision.

So the gate is placed explicitly (see *Build order*): two dock-town swatches go
through the Look Lab, the verdict and art bible are issued, and god rays and
weather continue in parallel with asset work.

### Build, don't buy — with a fallback

URP has no built-in volumetric fog. An asset-store package could supply
atmospheric fog, but the fog of war is custom no matter what — no package knows
about `FogField`. Buying one and building the other reintroduces exactly the
two-substances split this rig exists to prevent: atmospheric haze and knowledge
fog lit and composited by different systems, reading as two different materials.

But build-don't-buy with no fallback is how a two-weekend task becomes six. The
raymarched pass is the deepest technical water in the plan, so it is **timeboxed
to two focused weekends**, and `FogRenderer`'s stacked planes remain the shipping
fallback until the raymarch beats them in the Look Lab. The `FogField` contract
means swapping one for the other touches nothing above it.

### Two channels in the director, one of them inviolable

A strategy camera and Valheim-thick haze conflict: at 14,000 units, full haze
makes the board unreadable. The resolution splits what the director controls:

- **Atmosphere channel — fades with zoom.** Haze density, occluding fog
  thickness and god rays thin toward the top of the zoom range. The **colour
  grade and palette stay**, so the full-zoom board reads clearly while still
  looking like this game rather than a debug view.
- **Knowledge channel — never fades.** The three fog states hold at every zoom,
  every time of day and every weather state. Only haze and occlusion thin; the
  distinction between unknown, remembered and live is never allowed to.

The legibility toggle remains the nuclear option: it kills atmosphere *and* grade
— and still shows knowledge, through its own fog-state overlay.

### Memory has content and mood

A post-process grade can only dim and desaturate what was rendered. If what was
rendered were current-truth terrain, the grade would show a player the desert
their remembered forest secretly became, just in a moodier colour — a knowledge
leak with gameplay consequences, since degradation behind fog is meant to stay
invisible until re-scouted (`persistent-rts-design.md` §4.2: *"re-fog means stale
memory, not amnesia"*).

So remembered land needs both layers:

- **Content — stale data. This already exists and was verified end to end.**
  The server's v2 projector sends a remembered tile's *believed* biome (commented
  *"what the player LAST SAW, not what is true"*), and only as an override where it
  differs from genesis. The client's `BelievedBiome` is built from those overrides,
  and `TerrainColorizer`, `TerrainRenderer`'s chunk cache and `ScatterRenderer`
  all read `BelievedBiome` — never truth. Units, combats and piles are
  visible-only on the wire.
- **Mood — the grade.** A post pass reconstructs world position from depth,
  samples `FogField`, and desaturates and dims by the remembered amount. It works
  identically for every art style.

**One leak of this family was found while verifying, and it is not fixed here:**
roads. Both projectors ship every *explored* road tile with its **live** decayed
condition. §8.6 calls a road *terrain memory*, but condition is fed by traffic, so
a well-kept road behind fog reveals that someone is using it, and a road built
after your last look appears in your memory. See *Known issues*.

### One clock, one weather source, two consumers

The narration layer (the chronicler, and scout reports already) speaks about the
world in time and weather: the scouting spec's appendix explicitly wants a
per-claim *biome, season, weather* flavour field so that "the atmosphere is OUR
atmosphere". If the narration says "they struck at dawn" while the screen shows
dusk, or reports clear skies over a storm, the world contradicts its own
historian. So:

- **One mapping, owned server-side.** Time of day and weather are deterministic
  functions of world seed and tick (and region, for weather). The server computes
  them for the narrator and ships the same values or parameters to the client.
  The client never invents its own sky.
- **Tuning pins the clock.** While tuning, the director holds a chosen time of day
  — reproducible light is a prerequisite for tuning against it.
- **Playtests run a slower light cycle, expressed in ticks.** A sim day
  (`Time.Day` = 1,440 ticks) is 6 real minutes at the intended 4 tps; the playtest
  light cycle targets 30–45 minutes — one `Time.Week` (10,080 ticks) is about 42.
  It is defined in *ticks*, not wall-clock time, so pausing freezes the sun and a
  lab fast-forward speeds it: the sky stays locked to the world whatever the pace.
- **The commitment:** if night ever becomes sim-mechanical, it reads this same
  mapping — the constant moves from presentation-tuned to sim-authoritative, with
  no grandfathering. Note that **no such mechanic exists today**; every "night" in
  the current code and docs means the *player's* real night of sleep ("a night's
  sleep = ~80 game-days", "the morning report").

### Shadows at a 14,000-unit camera

No cascaded shadow map covers a camera that spans 120 to 14,000 units at useful
resolution, and the current 50-unit distance covers half a tile. Two layers:

- **Near:** cascaded shadow maps whose distance and splits follow the zoom value
  `PresentationScale` already tracks, rather than a fixed 50.
- **Far:** terrain shadows from **horizon mapping** — for each tile, the elevation
  angle of the horizon in 8–16 directions; a point is shadowed when the sun sits
  below the horizon in its direction. Per-frame cost is near zero as the sun moves,
  and because it derives from sim elevation, every art style inherits it. Mountains
  throwing long shadows across the plains at sunset, at strategic zoom, is the most
  distinctive image this map can produce.

**Recompute note:** nothing writes elevation after worldgen (verified by search —
the projector only reads it), so the horizon map is computed **once per session**
from genesis elevation and never invalidated. Canals change biome, not elevation.
Any future mechanic that changes elevation must invalidate it by region; an art
style that adds sub-tile relief does not, since near-field relief is covered by the
cascades.

### The shared lighting include

Every art style has to opt into the same lighting, or the A/B test compares
lighting implementations rather than styles. One `AowLighting.hlsl` provides soft
main-light shadows, Forward+ additional lights, SSAO, ambient probes and the far
terrain-shadow term; every Aow shader includes it, and style swatches use either
it or URP Lit. `Aow/Terrain` migrates first — today a torch beside the terrain
lights the units and leaves the ground dark.

## Build order

| Step | Work |
|---|---|
| **C3.0** | Housekeeping: camera post on, a clean `AowAtmosphere` volume profile replacing the template's, 64-bit HDR, an anti-aliasing choice |
| **C3.1** | Director, sun, sky, server-owned clock (pinned for tuning), fog colour matched to the sky horizon |
| **C3.2** | **Look Lab** — bookmarks, per-preset capture, side-by-side, the night-storm readability regression |
| **C3.3** | Post stack and grading; the two director channels; zoom and biome blending; the remembered grade |
| **C3.4** | Shadows at scale: zoom-driven cascades, horizon-map terrain shadows, `AowLighting.hlsl`, terrain shader migration |
| **C3.5** | Screen-space height and distance fog with sun in-scattering |
| **▶ A/B gate** | After C3.5 — or after C3.6 if it is going smoothly. Two dock-town swatches through the Look Lab → style verdict and art bible → **unblocks C4 asset authoring** |
| **C3.6** | Raymarched fog of war replacing the stacked planes. **Timeboxed to two weekends; stacked planes are the fallback.** |
| **C3.7** | God rays, with temporal reprojection and upsampling — in parallel with C4 assets |
| **C3.8** | Weather states from the server-owned weather source — in parallel with C4 assets |

## Future expansion

- **Night as a mechanic.** Binds to the clock mapping above; the director needs no
  change beyond reading an authoritative constant.
- **Weather as a mechanic** (visibility, movement). Same binding: the server-owned
  weather function becomes authoritative.
- **Local fog volumes** — swamp mist, river haze — are extra density terms in the
  same raymarch, not a second system.
- **Remembered structures** — enemy buildings shown as last seen. Today enemy
  structures in remembered tiles are simply absent, which is no leak but also no
  memory. It is content-of-memory work of the same kind as the road fix below.
- **Froxel volumetrics** replace the raymarch if performance demands it; the
  `FogField` contract is unchanged.

## Acceptance checks

- Night + storm preset, fog-frontier bookmark: remembered and live land stay
  distinguishable by a measured contrast threshold (Look Lab regression).
- At maximum zoom all three knowledge states are present and distinguishable,
  with atmosphere fully thinned.
- Pausing the sim freezes the sun; a fast-forwarded lab world advances it
  proportionally.
- A remembered tile whose true biome has drifted renders its believed biome, with
  the grade applied on top.
- The server's time-of-day and weather values match what the client displays for
  the same tick.

## Known issues found while writing this (not fixed here)

1. **Road memory leak.** `ViewProjector` (both v1 and v2) ships explored road tiles
   with *live* condition. Fix is server-side: a per-player remembered road
   condition captured at `Sight.Reveal`, alongside `RememberedBiome` — a Sim.Core
   state and snapshot change, so it needs its own slice.
2. **`CLAUDE.md` path.** It points to `persistent-rts-design.md` "at the repo root";
   the file lives at `docs/persistent-rts-design.md`.
3. **Unresolved vocabulary.** With a light cycle of one `Time.Week`, "day" means two
   things: the sim's `Time.Day` duration unit and the sunrise players see. Player-
   and narrator-facing text needs one meaning before the chronicler lands.

## Update 2026-09-15 — volumetric fog is a ship requirement; the planes are not a fallback

**The decision.** High-quality volumetric fog is required at ship, for both ambience
and the fog of war. The stacked-plane `FogRenderer` is no longer a shipping fallback.
It stays only as a temporary stand-in so the A/B gate does not wait on the raymarch.

**Why.** The owner looked at C3.1 in Play and said the current fog cannot look the
way it does when the milestone is done. Seen from the side, the planes read as flat
sheets. At a low camera over unexplored land they look like a dark stormy sky rather
than a bank of fog. The original fallback protected the schedule by allowing a
quality outcome the owner has now ruled out. The risk it guarded against is still
real: the raymarch is the deepest technical water in the plan. So the guard changes
shape instead of disappearing.

**What changes.**

- **The timebox becomes a checkpoint, not an exit.** After two focused weekends, the
  raymarch is judged in the Look Lab. If it isn't there, we re-plan the technique:
  froxel volumetrics, lower resolution with temporal upsampling, or a different
  density representation. We don't settle for planes.
- **Ambience and fog of war become one volumetric system, not two passes.** The old
  order had screen-space height fog (C3.5) followed by a separate raymarched fog of
  war (C3.6). That risks exactly what the build-don't-buy section warns about: haze
  and knowledge fog lit by different code and reading as different materials. The
  merged pass puts three density terms in one volume:
  - ambient height and distance haze from the preset;
  - knowledge density from `FogField`;
  - later, local volumes such as swamp mist and weather.
  All three share sun in-scattering, the main-light shadow map and the sky's
  horizon colour.
- **C3.4 must come first.** Light shafts through volumetric fog sample the
  main-light shadow map and the shared lighting include. Building the volume before
  shadows reach map scale would mean lighting it twice.

**Revised order (replaces C3.4–C3.6 in the build-order table).**

| Step | Work |
|---|---|
| **C3.4** | Shadows at scale, horizon-map terrain shadows, `AowLighting.hlsl`, terrain shader migration (unchanged) |
| **C3.5** | **The volume.** Raymarched fog at reduced resolution. Density = height/distance haze + `FogField` knowledge + noise. Sun in-scattering with shadow-map occlusion, horizon-colour ambient, and a depth-aware composite over the scene. The zoom channel thins haze but never knowledge. *Checkpoint after two weekends.* |
| **C3.6** | **Volume quality.** Temporal reprojection and blue-noise jitter, bilateral upsampling, 3D detail noise, wind drift on `_AowAnimTime`. Unknown land reads as a thick bank; remembered land as a thin veil. Then `FogRenderer`'s planes are deleted. |
| **▶ A/B gate** | After C3.5, with the volume at any quality. Style swatches don't depend on fog polish. |
| **C3.3** | Grading, the two director channels and the remembered grade. It can run before or after the volume; grading is tuned last against the final fog, so its values are revisited after C3.6. |
| **C3.7 / C3.8** | God rays (now mostly a by-product of in-scattering in the volume) and weather, in parallel with C4 |

**Acceptance additions.**

- No flat-sheet artefacts at any pitch the camera rig allows, including the
  lowest street-level pitch over unexplored land.
- Unknown land reads as a volume you could fly into, with height and depth, from
  street level; and as a clearly bounded mass from strategic zoom.
- Frame cost of the volume is budgeted and measured in the Look Lab at full-map zoom
  and at street level. The target is set when C3.5 starts, against the current frame
  time.

## Update 2026-09-15 (later) — the target look: a sea of clouds, mist at the frontier

**The decision.** Unknown land lies under a **sunlit sea of clouds**. Where it meets
known land it sinks and breaks into **mist**. That replaces "dark smoke" as the
fog-of-war image. The owner supplied a reference painting: a golden-hour village seen
from high above, with cumulus filling everything beyond it.

**What the reference asks for, term by term:**

| Where | Look | Channel |
|---|---|---|
| Unknown land | Bright cumulus sea. Billowing tops are warm-lit; gaps and undersides are cool blue-grey; sunward edges get a silver lining; peaks stand clear. | Knowledge, never thinned |
| Frontier | Clouds sink and erode into puffs, then wisps of mist spill a tile or two into known land and thread through trees | Knowledge |
| Remembered land | **Covered in fog, not cloud**: the land and its ruins stay visible through it, softened, greyed and cooler. The owner named this as the key distinction: remembered is *fogged*, undiscovered is *clouded*. | Knowledge (plus the C3.3 grade) |
| Live land | Clear, warm, crisp | — |
| Everywhere | Valley mist, and aerial haze glowing warm toward the sun | Atmosphere, thinned by zoom |

**Why the first volume missed it.** The first volume integrated density correctly but
had none of what makes clouds read as clouds:
- **Lighting was flat.** Nothing shadowed itself, so tops and hollows were lit the same.
- **Noise was cheap.** Live value noise gives neither billows nor wisps.
- **Coverage was blocky.** The knowledge field came straight off the tile grid, so
  edges followed tile boundaries.
- **Sampling was coarse.** Forty steps across 40 km missed shapes and left stripes.

**What the volume becomes** (the established real-time volumetric-cloud approach, built
in-house per build-don't-buy):

- **Shape.** Tiling 3D noise is built once at load: a 64³ Perlin-Worley shape volume
  and a 32³ Worley detail volume.
  - A height profile makes the layer solid below and billowing above.
  - Cloud tops roll across the map and sink toward the frontier.
  - Coverage comes from the knowledge field, blurred about two tiles and sampled
    bicubically, so the sea erodes into puffs at the frontier with round edges.
- **Light.** The light model has five parts:
  - a short light march toward the sun, for self-shadowing;
  - a dual-lobe Henyey-Greenstein phase, for the silver lining;
  - three-octave multiple scattering;
  - a powder term, darkening edges that face away from the sun;
  - cascaded and horizon-map terrain shadows, so mountains shadow the sea.

  Ambient light goes from the sky's colour at the tops to the ground's at the bottoms.
- **Mist.** Mist has three terms:
  - valley mist, which is atmosphere;
  - a remembered veil, which is knowledge;
  - frontier wisps, which are knowledge.
- **Next (C3.6).** Temporal accumulation with a fixed jitter sequence, which keeps Look
  Lab captures deterministic. Density-aware stepping. Softening the terrain mesh edge
  at the frontier.

**Budget.** About 4 ms for the fog at 1080p, measured in the Look Lab. The frame ran at
3.7 ms total before the volume landed, so there is headroom to spend on quality.

**What this does not decide.** The art style. The reference is painterly and
high-detail, but the atmosphere target holds for any style that comes out of the A/B.

## Update 2026-09-16 — the art board corrects the fog's shape

**Source:** ten images at `Art Of War/ArtBoard/`, read in `docs/art-direction.md`.
They change three things about the fog, all of which the first implementation got
wrong.

**1. Fog is LAYERS, not a lid.** The board never shows an even blanket at one height.
It shows ribbons of mist in valleys, banks standing in the middle distance, and a
cloud sea at the horizon and beyond the frontier — with the played land clear and
sunlit between them. The current volume covers all unknown land at
`smoothed ground + thickness`, which reads as snow draped over terrain.

The fix is that coverage falls off toward the player rather than stopping at the
frontier:

- **Near the frontier** (roughly the first 2–4 tiles of unknown): ground mist and
  wisps only, no ceiling. You can see a little way into the dark.
- **Middle distance:** the sea begins — broken cloud, the tops rising.
- **Deep unknown** (beyond roughly 8–12 tiles): the full sea, opaque.

That gradient is the fog of war doing its job — vision fades with distance from
what you know — and it is also the composition the board keeps using.

**2. The tuning camera is the diorama band, not full-map zoom.** Every board image
is oblique, close enough to read a house. Atmosphere is tuned and judged there
first; the strategic zoom is checked afterward for legibility, not for beauty.

**3. Golden hour with real contrast is the default condition.** Lit ground warm, shadows
cool and deep. The current pastel wash comes from ambient light being too close to
the sun's strength — a grading and light-ratio problem, addressed in C3.3 against the
board rather than by eye.

**Unchanged:** remembered land is fogged and still readable; unknown is clouded;
knowledge never thins with zoom.

### The land is continuous; knowledge is a colour (reverses a C1 rule)

**The decision.** Every in-bounds tile now gets terrain geometry, built from the
genesis heightmap. Unexplored ground is painted a neutral slate and covered by the
fog volume. This reverses C1's *"unknown tiles get no geometry"*.

**Why.** The old rule made the world end in tile-sized steps wherever the player had
not explored, and sat every live area at the bottom of a crater. The owner called the
jagged frontier jarring and said it does not read as continuous land — and it does
not: no reference on the board shows land that stops existing. Holes also let the
water plane show through from above, which is what the "rivers through the clouds"
turned out to be.

**Why it leaks nothing.** Terrain topology is already public on the wire, and always
was — `WireV2.cs` states it outright ("fog hides it VISUALLY"), and the genesis
payload ships the full elevation grid. What the slate colour withholds is what the
land *is*: biome, water, forest. Everything a player could act on stays gated on
knowledge — believed biome, roads, props, structures, units, combats.

**What the fog now carries.** Hiding unexplored land is the fog's job alone. That is
only a fair trade because the volume exists; under the old stacked planes, geometry
holes were doing work the fog could not.

**Consequences.**
- Chunk meshes are built for the whole map rather than the explored part, so terrain
  memory and per-rebuild cost no longer grow with exploration — they start at the
  fully-explored figure. Measured cost is unchanged at 1405x856 (~3 ms frames).
- `TerrainSampler.CornerHeight` averages all in-bounds tiles rather than known ones,
  so entities stand on the same ground the mesh draws at the frontier.
- The Look Lab's knowledge-contrast probe still classifies by fog state, not by the
  presence of geometry, so its readability measure is unaffected.

### Mountains: peaks instead of plateaus

**The bug, in the data.** `ContinentShaper` wrote
`elevation = Math.Min(1.0, elevation + dome) * mask * ramp`. The interior dome adds
0.15, so every tile whose noise exceeded 0.85 was **clipped to exactly 1.0** — the
highest ground came out as wide plateaus at maximum height. The flat-topped mesas
were baked into the elevation field, not into the rendering, and no amount of render
tuning could have carved a peak out of a plateau. It now **compresses** instead:
`(elevation + dome) / (1 + dome)`, which stays inside [0,1] while preserving the
order of the summits.

**The render curve, separately.** `WorldGeometry.MountainSharpness` 2 → 1.15 (at 2,
the top tenth of the elevation range mapped to half the world's height, which is what
made mountains read as sheer walls), `HeightScale` 30 → 24, `HillsTop` 0.55 → 0.60.

**`MountainMin` stays at 0.85.** Raising it to 0.90 for fewer, more landmark-like
mountains was tried and reverted: combined with the dome no longer clipping, the
highest ground stopped reaching 0.90 on some seeds, `StartPicker` could not find a
Grassland start with Mountain in range, and generation threw
(`WorldGenTests` seed 1151, plus the Scavenge lab). **Mountains are a start
requirement, not scenery** — that threshold cannot move without re-tuning
`ContinentDome` and sweeping seeds, which is its own task. All 1,057 + 49 sim tests
are green with the compression fix alone.


## Update 2026-09-16: the valley bank, and the phase cap

Two changes to the fog volume, both toward the board's "sea in the valley with the
mountain rising through it":

- **Valley fog pools to a level.** Mist used to be an offset above the smoothed
  ground, so it followed every hummock and read as a sheet draped over the land. A
  new term fills the low ground to `valley floor + depth`, where the floor is the
  lowest smoothed ground within `ValleyRadiusTiles` (a separable min filter, softened,
  stored in the ground texture's second channel). The top is a noise surface
  (`ValleyFogRoll`) with a soft edge, so ridges stand out of it and the bank rolls.
  Dials on `VolumetricFog`: `ValleyFog`, `ValleyFogDepth`, `ValleyFogRoll`,
  `ValleyFogEdge`, `ValleyRadiusTiles`. The old terrain-following mist remains for
  wisps.
- **The phase function is capped.** Henyey–Greenstein at g = 0.75 peaks at ~28×
  isotropic straight toward the sun, so any cloud between the eye and a low sun
  clipped to white however the sun was set. `PhaseHG` now holds the peak at 2.5: the
  silver lining becomes a broad glow rather than a spike. The fog's sun feed is also
  normalised by the sun's intensity above 1, so a brighter sun (the preset's golden
  hour is now 2.2 at noon, for ACES) no longer brightens the haze with it.
