# C3 Atmosphere Rig — Part 1 spec (C3.0–C3.2)

**Decision doc:** `docs/atmosphere-rig.md` (the *what* and *why* of the whole
milestone). **Plan addendum:** `Client_build_plan.md` in the client project,
`## Update 2026-09-15`. This file is the *how* for the first three steps.

---

## Scope and exit criteria

Part 1 builds the rig's foundation and the instrument that judges everything
after it:

| Step | What | Who |
|---|---|---|
| **C3.0** | Housekeeping: post-processing on, 64-bit HDR, a scene volume, a sky material | Editor (you) |
| **C3.1** | Server-owned world clock; atmosphere director driving sun, moon, sky and ambient; terrain ambient fix | Code, then editor wiring |
| **C3.2** | Look Lab: frozen-world fixtures, saved cameras, repeatable captures, contact sheet, the readability probe | Code, then editor use |

**Part 1 is done when:** a fixture-backed Look Lab run captures at least five
bookmarks × four sun positions × one preset; running the same matrix twice gives
images within 1/255 mean absolute difference; and the readability probe reports a
number for the fog-frontier bookmark at midnight and records it as a baseline.

**Checkpoint between C3.1 and C3.2:** press Play and confirm the sky, sun and
night look sane *before* building the instrument that photographs them. Nothing in
C3.2 is worth building on a sun that points the wrong way.

---

## C3.0 — Housekeeping (editor steps)

These are project configuration, not code, so they are done in the editor. Each
row says where and why.

| # | Where | Do | Why |
|---|---|---|---|
| 1 | Scene `C1_World` → **Camera** → Inspector → *Rendering* | Tick **Post Processing**. Set **Anti-aliasing** to **SMAA, High**. | Post-processing is currently off, so no grade or bloom can render at all. SMAA now; TAA waits for the volumetric pass, which is where its temporal history pays for itself. |
| 2 | Project → `Assets/Settings/PC_RPAsset` → *Quality* | Set **HDR Precision** to **64 Bits**. | 32-bit (R11G11B10) bands visibly on smooth fog and sky gradients — exactly the surfaces this milestone is about. |
| 3 | *Edit → Project Settings → Quality* and *→ Graphics* | Confirm the active render pipeline asset is **PC_RPAsset** for the quality level you play in. | Step 2 only matters if it is the asset actually in use. |
| 4 | Project → create folder `Assets/Settings/Atmosphere`, then *Create → Volume Profile* named **AowAtmosphere** | Select it, *Add Override → Post-processing → Tonemapping*, tick *Mode*, choose **Neutral**. Nothing else yet. | HDR sunlight above 1.0 clips to white without a tonemapper. Neutral keeps hues honest until the grade is designed in C3.3. |
| 5 | Scene → *GameObject → Create Empty* named **Atmosphere** → *Add Component → Volume* | Mode **Global**, Profile **AowAtmosphere**. | The look lives in a scene volume the director can later blend, not in the project-wide default. |
| 6 | Project → *Create → Material* named **AowSky** in the Atmosphere folder | Shader **Skybox/Procedural**. | The director drives a runtime copy of this each frame. It never edits the asset itself. |
| 7 | *Window → Rendering → Lighting* → *Environment* | **Skybox Material** = AowSky. **Sun Source** = the scene's Directional Light. | The procedural sky reads its sun position from the Sun Source. |
| 8 | *Project Settings → Graphics → Default Volume Profile* | **Leave it alone.** | Correction to the audit: its unusual components (`CopyPasteTestComponent2` and others) are not defined anywhere in `Assets` or the URP package sources, so they are inert references, not something to clean. The look lives in the scene volume from step 5. |

**C3.0 exit:** Play looks the same as before, except bright highlights now roll
off instead of clipping.

---

## C3.1 — Director, sun, sky, clock

### Server: the world clock

The rule from the decision doc is one mapping from tick to time of day, owned by
the server and shared by the screen and the narrator.

- **`Sim.Server/Atmosphere/WorldClock.cs`**
  - `LightCycleConfig(TicksPerCycle, PhaseOffsetTicks)`. The default is one
    `Time.Week` (10,080 ticks, about 42 real minutes at 4 tps, and 5¼ minutes at a
    32 tps lab pace), with an offset of a third of a cycle so tick 0 opens at about
    08:00 rather than midnight. A world that opens in darkness at its fog frontier
    is the wrong first impression.
  - `WorldClock.Phase(tick, config)` = `((tick + offset) mod cycle) / cycle`, in
    integer arithmetic until the final division, so every consumer gets the same
    double. **0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset.**
  - An unset (zero) config normalizes to the default. A zero cycle would otherwise
    freeze the world at midnight forever, silently.
- **`--light-cycle <ticks>`** server option, defaulting to one week.
- **Wire:** genesis gains `lightCycle { ticksPerCycle, phaseOffsetTicks }`; each v2
  view gains `lightPhase`, the server's own evaluation for that tick.
- **Tests:** phase stays in [0,1) and repeats every cycle; the named markers fall
  where their names say; an unset config is never a frozen midnight; genesis ships
  the projector's config; a view's `lightPhase` equals `WorldClock.Phase` for its
  tick, which is the function narration will call.

### Client

- **`WorldClockView`.** Evaluates the same formula from the genesis parameters at
  `SimClock.Now`, so the sun moves smoothly between the four-per-second ticks.
  Each new view is checked against the server's `lightPhase`; on any disagreement
  it logs one error, because a sky that disagrees with the server is a sky that
  will contradict the chronicle. A server too old to send a cycle holds noon with a
  warning rather than failing.
- **`AtmospherePreset`** (*Create → Aow → Atmosphere Preset*). One look, as data.
  - Sun path: noon elevation and sunrise azimuth. Moon: elevation, colour,
    intensity, shadow strength, disc size.
  - **Every colour and curve is keyed on sun height**, `h = InverseLerp(−18°,
    noonElevation, sunElevation)`, not on phase. Golden hour is a property of how
    high the sun is, so retuning the cycle length never moves it.
  - Curves: sun colour, intensity and shadow strength; ambient sky, equator and
    ground colours plus intensity; sky tint, exposure and atmosphere thickness;
    horizon colour.
  - Ships sane "meadows" defaults so a fresh asset looks reasonable before any
    tuning.
- **`AtmosphereDirector`** (a `LateUpdate` component, so it runs after
  `GameBootstrap` has advanced the clock).
  - **Phase:** pinned (the default while tuning, with a slider) or the world's.
  - **Sun:** rises at the sunrise azimuth, peaks at the noon elevation, sets
    opposite. **Moon:** the opposite arc.
  - **One directional light, two roles.** It becomes the moon when the sun drops
    below −4°. The sun's intensity curve reaches zero near the horizon and the moon
    fades in from −2° to −10°, so the handover happens in near-darkness with no
    visible pop. Two shadow-casting directional lights would double the shadow cost
    for a light that is never bright at the same time as the other.
  - **Ambient as a custom spherical-harmonics probe** built from the preset's
    three ambient colours and assigned to `RenderSettings.ambientProbe`. It's
    explicit, it doesn't depend on Unity recomputing a probe when colours change,
    and it's the same probe URP Lit units and the terrain shader both read.
  - **Sky:** a runtime copy of the sky material gets tint, exposure, atmosphere
    thickness, ground colour and disc size.
  - **Global shader values:** `_AowHorizonColor` (the fog colour that C3.5
    consumes), `_AowSunHeight`, and `_AowAnimTime`, which is the shader animation
    clock the Look Lab can freeze.
  - **Legibility mode:** flat, high, white light and neutral ambient while TAB is up.
- **Terrain shader ambient fix.** `Aow/Terrain` computed
  `albedo * (floor + lit) * main.color`, so its ambient floor was multiplied by the
  sun's colour: at night the ground goes black while URP Lit units stay
  ambient-lit. It now uses `albedo * (SampleSH(normal) + lit * main.color)`, which is
  the same probe everything else reads. This pulls one small piece of C3.4's shared
  lighting include forward because C3.1 can't be judged at night without it.
- **Wiring:** a `GameBootstrap.Atmosphere` field (initialised on connect and told
  about each tick) and a `LegibilityView.Atmosphere` field (flat light when toggled).

### Editor steps once the code lands

1. Select the **Atmosphere** object from C3.0 → *Add Component →
   AtmosphereDirector*. Assign **Sun** (the Directional Light), **Sky Material**
   (AowSky) and **Preset**.
2. *Create → Aow → Atmosphere Preset* in `Assets/Settings/Atmosphere`, named
   **Meadows**, and assign it to the director.
3. Select **Game** → *GameBootstrap* → **Atmosphere** = the Atmosphere object.
   Do the same on *LegibilityView* → **Atmosphere**.

### C3.1 acceptance

- Server tests green, including the clock and wire tests above.
- **Pinned:** dragging *Pinned Phase* 0 → 1 moves the sun up in the east at 0.25,
  high at 0.5, down in the west at 0.75, and to moonlight after. Night stays
  readable, and terrain and units are lit by the same ambient.
- **Unpinned:** the sun moves at the cycle rate; pausing the server stops it; and
  the console shows no light-phase disagreement.
- **TAB:** light goes flat, then restores when toggled off.

---

## C3.2 — Look Lab

### Why it needs a frozen world

A capture is only a measurement if the same inputs give the same picture. Every
source of drift gets its own control:

| Source of drift | Control |
|---|---|
| The live world changes every tick | **Fixture link**: record genesis and one view to disk once, then replay them offline |
| Time of day | The director's pinned phase; `SimClock` stays paused in fixture mode |
| Fog rolls in over 0.45 s | `FogField.Snap()` jumps the field to its target before capture |
| Fog noise scrolls on `_Time` | The fog shader moves to `_AowAnimTime`, which the director freezes |
| Camera zoom easing | `CameraRig.SetPose(pose, instant: true)` |
| UI overlay | Capture renders the camera to a RenderTexture, which UI Toolkit's overlay never enters |
| First-frame warm-up (later, TAA history) | Wait a fixed number of frames before reading pixels |

### Components

- **Net: `FixtureServerLink : IServerLink`.** Serves `world.json` and `view.json`
  from a fixture folder, reports a paused pace, and rejects intents with a clear
  reason.
- **Editor: fixture recorder.** Fetches `/v2/world` and `/v2/view/{id}` from a
  running server and writes the raw JSON plus a `meta.json` (server URL, player,
  reveal flag, tick, map seed, date).
- **`GameBootstrap`:** a link mode (**Live** or **Fixture**) and a fixture folder.
  In Fixture mode the pace is paused and the clock never advances.
- **`CameraRig`:** a `CameraPose` value (pivot, distance, yaw, tilt offset) with
  `GetPose()` and `SetPose(pose, instant)`.
- **`FogField` / `FogRenderer`:** `Snap()`.
- **Fog shader:** noise driven by `_AowAnimTime` instead of `_Time`.
- **`LookLabBookmarks`** (*Create → Aow → Look Lab Bookmarks*): named camera poses.
  It ships with five slots to fill: *street-level forest*, *dusk over water*,
  *strategic mid-zoom*, *full map zoom*, and *fog frontier* (all three knowledge
  states in frame).
- **Sun positions are defined by target sun elevation, not raw phase:** *Noon*,
  *Evening golden* (+8°, descending), *Blue hour* (−6°), *Midnight*. They're
  converted to phase through the preset, so retuning the noon elevation never
  quietly moves the test conditions (the project's config-derived-tests rule).
- **`LookLabCapture`** (runtime). For each preset × bookmark × sun position: apply
  the preset, pin the phase, set the pose instantly, snap the fog, freeze
  animation, wait N frames, render to a fixed-size RenderTexture and write a PNG.
  Then it assembles a labelled **contact sheet** (presets as columns, bookmark ×
  sun position as rows) and a `manifest.json` recording the fixture, each preset's
  content hash, the sun positions, the resolution and the Unity version. Afterwards
  it restores the camera pose, pin state and animation freeze exactly as it found
  them.
- **`KnowledgeContrastProbe`**: the readability regression.
  - Samples a grid of pixels at the fog-frontier bookmark. Each pixel is classified
    by marching its camera ray to the terrain heightfield (steps of at most a
    quarter tile, refined by bisection) and reading that tile's fog state. Sky and
    unknown pixels are excluded.
  - **Metric:** the contrast ratio `(L_live + 0.05) / (L_remembered + 0.05)` of mean
    relative luminance, plus the lightness difference ΔL*.
  - **Threshold policy:** the first accepted run writes
    `readability-baseline.json`. Later runs fail if the ratio drops below the
    larger of an absolute floor of 1.25 and 85% of the baseline. The storm variant
    is added when weather lands in C3.8.
- **`LookLabWindow`** (*Window → Aow → Look Lab*, usable in Play mode): record a
  fixture; manage bookmarks (add from the current camera, go to, rename, reorder,
  delete); pick presets to compare; pick sun positions; **Run Capture**, **Run
  Readability**, **Open Captures Folder**.

### Where files go

Everything lives under **`LookLab/` at the project root, outside `Assets`**, so
Unity never imports the PNGs as textures:

```
LookLab/
  Fixtures/<name>/world.json, view.json, meta.json
  Captures/<yyyyMMdd-HHmmss>/<preset>/<bookmark>@<sun>.png
  Captures/<yyyyMMdd-HHmmss>/contact-sheet.png, manifest.json
  readability-baseline.json
```

Recommend checking **fixtures and the baseline** into version control and adding
`LookLab/Captures/` to `ignore.conf`. Fixtures are the test inputs; captures are
regenerable output.

### C3.2 acceptance

- Two consecutive runs of the same matrix give images within 1/255 mean absolute
  difference.
- The contact sheet labels every column and row.
- The readability probe prints a number for midnight at the frontier bookmark,
  writes a baseline on the first accepted run, and fails a deliberately darkened
  preset.
- After a run, the camera, the pin and animation are exactly as they were before it.

---

## As built (2026-09-15)

C3.1 passed its Play checkpoint. C3.2 compile-checks; it has not yet been run in Play.
It differs from the plan above in three ways:

- **Bookmarks live in `LookLabSettings`** (*Create → Aow → Look Lab Settings*), one
  asset holding the whole matrix: presets, bookmarks, sun positions, capture size,
  warmup frames and the probe's settings. A separate bookmarks asset would have
  split one test definition across two files.
- **The contact sheet is `contact-sheet.html`,** not a PNG. The labels are real
  text, and every cell links to its full-resolution capture. Unity has no
  practical way to draw text into a texture without a font pipeline.
- **The animation clock is reset to 0** at the start of each run, not just frozen.
  Freezing alone would hold whatever value it had reached, so two runs would
  photograph different moments of the fog noise.
- The fixture recorder writes the raw JSON the live link last received
  (`HttpServerLink.LastWorldJson` / `LastViewJson`). A re-fetch would return a
  later tick than the one on screen.
- **"Compare last two runs"** is the repeatability check: the mean absolute RGB
  difference per image, which must be at most 1/255.

## Risks to confirm in Play, not assumed

1. **`AmbientMode.Custom` with `RenderSettings.ambientProbe`** at runtime in URP 17.3
   is expected to drive both URP Lit and the terrain's `SampleSH`. This is the first
   thing to look at in Play: set a midnight pin and check that units and ground agree.
2. **The `SampleSH` include** in the custom terrain shader. I can't compile shaders
   from the command line, so the Editor's Console is the first real compile.
3. **The procedural skybox draws its disc at the main light,** so at night the moon
   gets the disc. Acceptable for a stand-in sky.
4. **The reflection probe is baked from the skybox at load** and won't follow time
   of day, which slightly affects URP Lit specular. Minor; revisit in C3.3.
5. **Live-server age.** An older server with no light cycle makes the director hold
   noon with a warning. Restart the server after the server slice lands.
6. **Probe accuracy** depends on the heightfield march resolution; bisection keeps it
   within a fraction of a tile.

## Deferred to part 2

C3.3 grading and the two director channels, C3.4 shadows at scale and the shared
lighting include, C3.5 height and distance fog, then the A/B gate. See the build
order in `docs/atmosphere-rig.md`.
