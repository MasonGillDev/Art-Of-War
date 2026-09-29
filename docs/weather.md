# Weather

## Decision

**Weather is a pure function of the world seed and the tick, evaluated on the client.**
A slow, large-scale noise field over (x, y, time), drifting with a prevailing wind,
gives every tile a *cloudiness* and a *storm* value at any tick. The client reads that
field for the sky sheet, cloud shadows on the land, rain, lightning and thunder. Nothing
crosses the wire and Sim.Core is untouched. Because the function is deterministic, every
client of the same world sees the same sky at the same tick, and the Look Lab can
photograph a named moment of weather.

**Gameplay effects are deferred**, and the door is kept open: the day weather should
slow a march, wet a farm or ground a boat, the sim adopts the same function (one file,
no floating point beyond what the noise already uses in fixed-point form) and the client
keeps reading its own copy, byte-identical, exactly as it reads the day clock today.

## Why

The ask: clear days, a variety of cloud, thunderstorms with storm clouds, and change over
time. Three places the weather could live were weighed.

**Client-only random** was rejected. Two players on the same world would see different
skies, a replay would not match its recording, and the Look Lab, which depends on every
run photographing the same instant, would lose the sky. It is also the option that can
never grow into gameplay.

**Sim-authored weather on the wire** was rejected for now. The sim is closed to
modification outside a decided milestone; a `WeatherDto` on the v2 view is additive and
cheap, but it needs a server-side generator, a snapshot field, a test, and a decision on
what weather *does*. All of that is the gameplay milestone, and none of it is needed to
put storms in the sky. Choosing it now would tie the visual work to a sim decision nobody
has asked for yet.

**A deterministic function of (seed, tick)** wins because it gives the multi-client and
replay guarantees of the wire option with the cost of the client-only option, and because
its migration path is a copy, not a rewrite. This is the same shape as the day clock: the
server owns the mapping from tick to time of day, the client evaluates it locally, and
nobody sends "it is noon" over the wire.

The trade-off accepted: until the sim adopts the function, weather is cosmetic. A storm
over an army changes nothing but the light. That is stated on the HUD by absence: no
weather row appears anywhere a number could be read as a gameplay fact.

## What the field is

Two channels, each a tiling 3D noise (the `CloudNoise` shape volume already built for
the cloud sea, sampled at (x, y, t) with t = tick scaled to weather time) drifting with a
prevailing wind fixed per world from the seed:

| Channel | Meaning | Scale in tiles | Scale in time |
|---|---|---|---|
| Cloudiness 0..1 | Fraction of sky covered over this tile | ~60 tile fronts | fronts cross a 250-tile map in about two days |
| Storm 0..1 | Thunderstorm intensity; non-zero only where cloudiness is high | ~20 tile cells inside a front | a storm cell lasts a few hours and dies |

Seasons, if the demographic clock ever grows them, bias both channels by a slow curve over
the year. Not built now; the hook is one multiply.

Derived per tile: `Overcast = cloudiness > 0.7`, `Raining = storm > 0.35`,
`Lightning = storm > 0.7`. Derived at the camera pivot: the values the sky and the sound
follow.

## What gets built

### Now (this milestone)

1. **`Weather.cs`** (client, `Atmosphere/`): `Sample(x, y, tick)` returning cloudiness and
   storm, plus `AtPivot` sampled where the camera looks. Seed and wind from genesis.
   Deterministic, no allocation, a few noise taps.
2. **Sky sheet follows the weather.** `CloudAmount`, `CloudSoftness`, `CloudDetail` and the
   shade colour are driven from cloudiness and storm at the pivot instead of fixed preset
   values: a clear day is a few high wisps, an overcast day a low grey lid, a storm a
   dark-based deck with a lit rim. The preset keeps the *range* for each (clear value,
   overcast value, storm value) and the weather interpolates.
3. **Cloud shadows on the land.** A global cloud-shadow texture, one texel per tile, filled
   each tick from the field and the sun's direction, read by `AowLighting.hlsl` beside the
   far horizon shadow. The single largest legibility gain at the strategy camera: moving
   shadows say "weather" from any altitude. Only over known land, since unknown land is
   under the cloud sea already.
4. **Light under weather.** Sun intensity and shadow strength scaled by cloudiness at the
   pivot; ambient shifted toward the storm colour. Driven through the director so the sea,
   fires and fog inherit it.
5. **Rain and lightning.** A near-camera rain particle system (the Generic pack's
   `FX_Rain_01`) gated on `Raining` at the pivot, faded by zoom. Lightning as a brief pulse
   on the sun light's intensity and colour, timed by the storm channel and a hash so two
   clients flash together, with the `AmbienceDirector` fading a thunder bed by storm and
   playing a one-shot on each flash after a distance delay.
6. **Weather in the sky's own words.** No HUD row. The Look Lab gains a weather pin
   (clear, overcast, storm) beside its time pin, so the art can be tuned against each.

### Later (gameplay milestone, sim decision)

Movement cost under rain, farm growth under drought or flood, boats grounded by storms,
visibility reduced under overcast for scouting. Each needs the sim to evaluate the same
function, a snapshot version bump only if the generator gains state, and a wire field only
if the *sim's* weather ever diverges from the function (it should not). Fire spread, river
level and snow cover are further out and would each be their own doc.

## Acceptance tests

- Two clients on the same world at the same tick sample identical weather at every tile
  (the function is pure; a test evaluates it on a grid and hashes the result against a
  pinned value, the way the terrain bake is pinned).
- A Look Lab capture at a pinned tick and weather pin is byte-stable across runs.
- Cloud shadows never fall on unknown land, and never lag the sheet: both read the same
  field at the same tick.
- With storm forced to 0 everywhere, the sky is indistinguishable from today's fair-weather
  sheet (the field is additive on the current look, not a replacement).

## Future expansion

- **Sim adoption** is the designed path: `Weather.Sample` moves to `Sim.Core` in fixed
  point, the client's copy becomes a mirror pinned by the same hash test, and gameplay
  rules read it. No wire change unless the sim adds state the function cannot derive.
- **Regional weather on the map band.** The field is per tile, so the hand-drawn map can
  hatch overcast regions and mark storms without any new data.
- **Seasons** bias the field by a yearly curve; **climate zones** (coast wetter, mountains
  stormier) bias it by genesis biome. Both are multiplies on the sample.
- **Volumetric weather.** The sky sheet is a 2D stand-in. The cloud sea already marches a
  real volume; a storm front could one day be drawn by the same shader over known land,
  reading the weather field for coverage instead of the knowledge field. The seam is the
  field, not the shader.

## References

- `Art Of War(prod)/docs/atmosphere.md`, update 2026-09-22: the custom sky and the
  cloud sheet this drives.
- `docs/atmosphere-rig.md`: the day clock, the model for "server-owned mapping,
  client-evaluated".
- `docs/architecture.md`: the determinism contract this keeps.

## Status

- 2026-09-22, step 1 built and compile-checked, not seen in Play: `Client/Atmosphere/Weather.cs`
  (the field; constants, not an asset, so every client agrees), `CloudNoise.TilingPerlin`
  exposed, `AtmospherePreset` gains `ClearSky` / `OvercastSky` / `StormSky` cloud states
  (`SkyDefaultsVersion` 5 re-migrates), `AtmosphereDirector` samples the field at the camera
  pivot each frame (`WeatherAtPivot`) and blends the three states into the sky sheet,
  `PinWeather` on the director is the tuning pin (Follow / Clear / Overcast / Storm) and the
  Look Lab saves and restores it. Steps 2 (cloud shadows, light) and 3 (rain, lightning,
  thunder) not started; thunder audio owed by the user.
- 2026-09-22, step 1 seen in Play and accepted after three rounds (the shade colour had to be
  weathered as a whole product; overcast is grey, storm dark grey). Step 2 built and
  compile-checked, not seen: `CloudShadowMap` (R8 one-texel-per-tile global `_AowCloudShadow`,
  filled on a worker from `Weather.DeckAt` — a finer deck noise, ~7 tiles, thresholded by the
  front's cloudiness — offset and stretched along the sun; multiplied into the shared Aow
  lighting's shadow term beside the far horizon shadow, so terrain, grass, river and scatter
  proxies take it; pack-lit buildings and people do not, by choice). Light under weather: each
  `CloudState` carries `SunScale`, `ShadowScale`, `AmbientScale`, `CloudShadow`; the director
  scales the sun, weathers the ambient probe and the horizon global. `SkyDefaultsVersion` 8.
- 2026-09-22, OPTION B built and compile-checked (C#), shaders unseen: the weather now lives in
  the VOLUMETRIC pass. User decision after seeing the sheet flip whole-dome as a front was panned
  across: "if this was weather that actually lived in 3D you could see a storm in the distance
  and go to it." `CloudShadowMap` now fills an RGBA field texture (`_AowWeatherField`: R deck,
  G cloudiness, B storm, A shadow) read by both the ground lighting (`AowCloudShadow` = .a) and
  the fog shader (`AowWeatherAt`), so cloud and shadow agree. `AowVolumetricFog.shader` gains a
  WEATHER SLAB (`_AowWeather0..2`, `_AowWeatherAlbedo`): base 1900 / thickness 700 world units,
  coverage = deck thresholded by cloudiness, shape + detail from the existing 3D noises, cumulus
  profile towering with coverage and storm, storm darkens albedo, self-shadowed by the same light
  march, and the deck's shadow (field .a) falls on the sea, mist and haze below. Cost dial:
  `WeatherFineStep` (110) on `VolumetricFog`; `WeatherClouds` toggle. Baseline before B: 95 FPS,
  10.5 ms at 4K over a settlement at noon. The sky sheet is demoted to fixed high cirrus
  (ClearSky state, opacity 0.45); the pivot weather sample is an area (5 taps, 6 tiles) eased
  over `WeatherEase` = 2.5 s and drives only the LIGHT. `SkyDefaultsVersion` 9.
- 2026-09-22, PROCEDURAL EVERYWHERE (user: "do we really need tile-based?" + straight shadow
  bands and a straight cloud edge at the map border in Play). The per-tile field texture and
  `CloudShadowMap` are DELETED. `AowLighting.hlsl` now holds THE WEATHER FUNCTION (`AowWx*`):
  cloudiness, storm, deck, tower and `AowCloudShadow` (transmittance through the cloud the
  sun passes, offset along the sun) are reads of the bound cloud shape volume at coordinates
  from position and time; `Weather.cs` evaluates the identical formulas from the same noise
  bytes (`CloudNoise.ShapeBytes`) for the sun under the camera (`AtmosphereDirector.LitUnder`).
  `VolumetricFog.PublishWeatherFunction` sends the coordinates and dials as `_AowWx*` globals
  from the director's weather clock. Time is an axis of the volume, so decks change shape as
  they drift; nothing ends at the map edge; the base undulates with the deck; the layer fades
  before the march's reach. Cumulus is a rounded mound (isotropic noise cut against a rising,
  rolling-over envelope; erosion at the edges only), Clear pin = no cloud (< 0.15 cloudiness
  draws nothing), `WeatherSpeed` on the director runs the weather clock ahead for testing.
  Shares today ≈ clear 25% / scattered-broken ~55% / overcast ~15% / raining ~5%; `Mean` and
  `Contrast` in `Weather.cs` are the knobs. Compile-checked; shaders unseen.
- 2026-09-22, THE INSTANCE PIPELINE (user: "you create a cloud, it's not a cone, you put it in
  the sky"). Clouds are now OBJECTS, not a threshold on noise: `CloudInstances.Generate` rolls,
  per drifting spawn cell (12 tiles, 2 slots) and generation (0.6 days, per-slot phase), whether
  a cloud exists (probability = the region's cloud amount, so clear = none), its place, radius,
  height (storm-tall), and a life envelope (in, full, out). `CloudField` rasterises them on a
  worker into an RGBAHalf texture around the camera (440 tiles, 2 texels/tile): underside and
  crown per texel from a rounded cap profile, neighbours merged by a soft max, plus puff and
  storm; the shader slides the window by the wind between rasters. The volumetric pass marches
  base→top (solid inside, a soft `WeatherShell` under the crown and above the base, detail noise
  biting the shell only); `AowCloudShadow` and the sun under the camera read the same field.
  The old noise-threshold weather block, `AowWx*` deck/tower reads and `Weather.LitAt` are
  superseded (the `AowWx` cloudiness/storm functions remain for the generator's twin).
  Round 1 of the plan; compile-checked, unseen.
- 2026-09-22, rounds 2–4 of the instance pipeline, each seen in Play: drips under rims were
  bilinear filtering between zero texels and cloud texels (empties now carry the layer base
  with zero thickness); a hemisphere crown profile was a wall at the rim (now an S-curve
  mound); the noise carve subtracted a 0.5-mean noise and whittled clouds to cones (now
  centred, pushes out as well as in); undersides get a per-cloud roundness and their own finer
  lumps. The CPU raster (6 MB upload per interval, popping at low rates) is replaced by a GPU
  raster: `Shaders/AowCloudField.shader` draws a 512² field around the camera every frame from
  a ComputeBuffer of clouds; the list regenerates at `CloudGenerateHz` (10); the sun under the
  camera reads the list analytically. User's tuned values baked as defaults (base 3424,
  thickness 173, towers 0.3–9.3, radius 5.3–21 tiles, shape 20 / detail 9.8 tiles, erosion 1,
  shell 300). `WeatherFineStep` 20 → 45: 20 was the frame-rate cost. Lighting round next.
- 2026-09-22, REGIME SCALE (user: "storms, clear skies need to be much bigger — larger than
  the entire map; the blend always reads as overcast; presentation scale, not km scale").
  `FrontTiles` 125 → 700, `CellTiles` 40 → 350, drift 200 tiles/day, lives 4 / 0.8 days;
  cloudiness made BIMODAL (`Contrast` 3.5, `Mean` 0.45) and storm likewise (curve ×5 + 0.4,
  mirrored in `AowWxStorm`). The whole map sits in one regime with a transition passing in
  most of a day. Under evaluation alongside buying Enviro 3 (URP, Unity 6 render graph,
  modular): its weather blends presets over TIME (smooth), not across the screen; regional
  only via biome zones; "storm as a placed object" would be lost, deterministic regime
  selection kept. Decision pending on the cloud renderer; the weather FUNCTION stays either way.

## Update 2026-09-22: the cloud renderer is bought, the weather function stays

**Decision.** Cloud rendering moves to a purchased asset, Enviro 3 Sky and Weather (URP, Unity 6
render graph, modular). The in-house volumetric weather clouds (the slab in
`AowVolumetricFog.shader`, `CloudInstances`, `CloudField`, `AowCloudField.shader`) are
superseded and will be removed once Enviro renders in the scene. The weather FUNCTION
(`Weather.cs`: seed-and-tick fronts and storms, larger than the map, bimodal), the pin, the
testing speed, and the sun-through-cloud lighting remain and become the thing that selects and
blends Enviro's weather presets.

**Why.** After a day of rounds the in-house clouds were still not right (cones from height
rolled independently of radius and from lump noise larger than a cloud; blends reading as
overcast until the regimes were made map-sized), and every round cost a Play check because
the author cannot see shaders. The user's requirement is simply pretty clouds; a shipped
volumetric renderer with temporal reprojection meets it now, at lower frame cost than the
quarter-resolution march, and its weather presets blend smoothly over time.

**What is given up.** Clouds as placed objects with an address (a storm you can walk out from
under); byte-stable Look Lab captures of the sky (reprojection is stochastic); full ownership
of the look. Regional weather only through Enviro's biome zones.

**What the integration must keep.** The atmosphere director owns TIME (Enviro on external
time); the fog-of-war cloud sea stays ours (knowledge, not weather) and must composite with
Enviro's clouds; fires, ambience and the sea keep reading the director's drawn sun; the
weather sample at the pivot (eased) maps to a preset and a blend, so weather is still the
world's and every client agrees.
- 2026-09-22, ENVIRO 3.3.2 INTEGRATED (round 1). Package surveyed: modules Time, Lighting, Sky,
  Fog, VolumetricClouds, FlatClouds, Weather, Lightning, Effects, Audio, Reflections,
  Environment, Quality; URP renderer feature at BeforeRenderingTransparents-1 (our fog pass is
  AfterRenderingTransparents, so the cloud sea composites over Enviro's clouds — confirmed in
  Play); nine shipped weather types (Clear Sky, Cloudy 1–4, Foggy, Rain, Snow, Storm);
  `ChangeWeather(name)` blends over per-category speeds; `Time.Settings.simulate=false` +
  `SetTimeOfDay` for external time; `customWeatherMap` (R coverage, G type) sampled at a
  hard-coded 400 km repeat — the door to regional weather from our function, later.
  Built: `Atmosphere/EnviroBridge.cs` (#if ENVIRO_3; feeds phase → time, eased regime →
  preset via `PresetFor`, disables Enviro fog and auto weather, returns the drawn light's
  direction); the director's `ApplyExternalSky` path (no light/ambient/skybox driving; SunElevation
  and SunHeight from Enviro's sun; horizon and sun-height globals still published). REMOVED: the
  weather slab in `AowVolumetricFog.shader`, `AowWx*` and the ground cloud shadow in
  `AowLighting.hlsl`, `CloudInstances`, `CloudField`, `AowCloudField.shader`, the weather dials on
  `VolumetricFog`. `Aow.Client.asmdef` references `Enviro3.Runtime`; `_AowTypeCheck.csproj`
  references its DLL with ENVIRO_3/ENVIRO_URP. User's editor steps done: prefab in MainScene,
  URP support activated, render feature added, our Directional Light off. Next: Play check of
  the bridge (time, presets, sun readback), then the custom weather map for regional weather,
  then thunder/rain audio through Enviro's Audio module.
- 2026-09-22, Enviro round 2: the LIGHT is ours again (`Lighting.Settings.setDirectLighting/
  setAmbientLighting` off; the director aims our light along Enviro's sun, drives colour,
  intensity, shadow and the ambient probe from the preset; one directional light enforced —
  Enviro's second/moon light disabled, its own light disabled when ours is active). Enviro's
  fog is off at the flags the render pass reads (`Fog.Settings.fog/volumetrics`), not `active`.
  Cloudy 4 (our Overcast) retuned in its asset: coverage 0.92, density 0.6, dilate 0.6. Preset
  dropdown `EnviroPreset` on the director forces any shipped preset. REGIONAL WEATHER:
  `EnviroWeatherMap` paints the function into `customWeatherMap` (R coverage, G type, B/A high
  cloud; 1024², Enviro's fixed 400 km repeat ≈ 4 tiles/texel) on a worker every second; off
  while a preset is forced (`RegionalWeather` on the director). Reflections: the sea mirror
  camera is registered with `AddAdditionalCamera` at the "Reflection" quality so water reflects
  the clouds; Enviro's global reflection probe is independent of it. Audio: rain and thunder
  are baked into Enviro's weather types (user confirmed). Unseen: the regional map and the
  reflection registration.

## Update 2026-09-22: a global schedule, not a field

**Decision.** Regional weather is dropped (user: "I don't need regional weather. I need the same
weather everywhere on the map just with clean transitions"). The noise field of fronts and storm
cells is replaced by a WEIGHTED SCHEDULE: time is cut into periods of `StateDays` (0.6 world
days) and each period's state is drawn from a table by weight, from a hash of the seed and the
period's number. Still a pure function of (seed, tick), so every client draws the same sequence
and replays match; nothing crosses the wire. `EnviroWeatherMap` and the `customWeatherMap` hook
are deleted, as is `WeatherPin` — the `EnviroPreset` dropdown on the director is the single pin.

**The table** (`Weather.States`; weights total 100, so a weight is a percentage of the world's
days): Clear Sky 25, Cloudy 1 20, Cloudy 2 16, Cloudy 3 12, Cloudy 4 (overcast) 15, Rain 8,
Storm 4; Foggy and Snow are in the table at weight 0 so they can be pinned but never come up.
Each row also carries the cloudiness and storm that drive this world's own light through
`AtmospherePreset.CloudState`.

**One transition time.** `TransitionSeconds` on the director (25 s) sets BOTH Enviro's eight
crossfade speeds (it blends at speed × deltaTime, so speed = 1/seconds) AND the ease on the
cloudiness and storm the light reads. The land's brightness can no longer arrive ahead of the
clouds. `WeatherSpeed` still runs the schedule fast for testing.

**What is given up**, again: a storm has no place on the map. The whole sky is one state.

**Dusk fix (same day).** Two snaps at the sun/moon handover, both in `ApplyExternalSky`: the
bridge returned whichever body was DRAWN, so `SunElevation` jumped from the sun's -4° to the
moon's own height the instant the moon took over and the ambient gradients jumped with it; and
the moon arrived at full `MoonIntensity` with no fade. The bridge now always returns the
direction toward the SUN (continuous, negative all night) and exposes `ToMoon` separately; the
director derives `byMoon` from the sun's elevation with the same threshold as its own path and
restores the moon's fade-in between `MoonFadeStartElevation` and `MoonFullElevation`.
