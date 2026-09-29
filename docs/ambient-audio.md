# Ambient audio: cue-driven beds under the drawn sun

## The decision

Ambient sound in the production client is a set of **always-playing looping beds
whose volumes follow cues measured from the land around the camera pivot**, plus a
small pool of **3D point sources parked on the night fires**. Day and night are read
from the **sun as the atmosphere director draws it** (`SunElevation`), never from the
sim tick. Only asserted land makes sound. Zoom is a mix dial. Built 2026-09-18 in
`Art Of War(prod)/Assets/Scripts/Client/Audio` (`AmbienceSet`, `AmbienceDirector`)
with `Editor/SetupAmbience`.

## Why

**Cues, not triggers.** The obvious design is "camera enters forest → play birds".
It was rejected because tiles are 1 km and the camera pans across dozens a second;
enter/exit events would stutter, and every new sound would need its own trigger code.
Instead the director takes a distance-weighted census of the tiles in a zoom-scaled
radius (forest fraction, river fraction, sea, settlement, warm/open/mountain) and each
layer declares which cue it listens to and how (threshold, full, gain, attack,
release, groundedness). Crossfades fall out of the census changing. Adding a sound is
adding a row to the asset; new code only when the *cue* is new.

**The sun as drawn, not the game clock.** The atmosphere director can pin time
(`PinTime`), and the Look Lab freezes it. If sound read the sim tick, a pinned noon
would hear crickets. The fires already key off `SunElevation` with dusk and full-dark
thresholds; the ambience uses the same two numbers so sound and light agree by
construction.

**Only asserted land.** The terrain renderer gives unknown tiles no geometry. The
census gives them no sound, and they still count in the total, so a fog edge damps
the mix rather than being skipped. Remembered tiles hum at a reduced weight. This is
the "presentation amplifies truth, never invents it" rule from the client build plan,
applied to audio. It is also why the pack's rain and cave loops are not wired: rain
would need a weather fact on the wire, and there is none.

**Fires share one census.** The hearth crackle rides `NightFires.CollectHearths`
rather than re-deriving fire positions, so a fire can never sound where it does not
burn. Torches carry no sound; a marching column is light, not noise.

**Beds are 2D, fires are 3D.** A stereo forest loop panned to a point sounds wrong
and the pack's stereo clips cannot be spatialised anyway. The mono fire loops can.
Point sources use logarithmic rolloff with near/far dials in world units, doppler
off because the camera moves fast and a fire does not whine.

**An asset, not constants.** Same pattern as `NightFireSettings` and
`NatureScatterSet`: code drives *which* layer is heard, the asset decides *how it
sounds*, and tuning in Play persists. The setup menu is additive by layer name, so
re-running it after a new clip lands never resets a tuned layer.

Losing alternatives: an event-driven trigger system (stutter, per-sound code); a
per-tile emitter grid of 3D sources (thousands of voices, and stereo beds cannot be
placed); reading the sim tick for time (disagrees with a pinned sun).

## Future expansion

- **New sounds** are rows in `Ambience.asset` via the setup table in
  `SetupAmbience.Rows`, or by hand in the inspector. A farm-dog, a village bustle, a
  smithy hammer are each one row on the `Settlement` cue once the clips exist.
  Structure-kind cues (farm vs smelter) would be a new `AmbienceCue` and a few lines
  in `Measure`.
- **Weather loops** (rain, strong wind) wait on a weather fact on the wire. The layer
  system needs no change; a `Weather` cue does.
- **One-shots** (combat, construction, arrivals) are deliberately out of scope here.
  They are event-shaped and belong to a separate emitter that reads view deltas, on
  the `PointGroup` mixer bus.
- **Mixer routing.** `AmbienceSet` carries optional `AudioMixerGroup` slots for beds
  and points. Creating the AudioMixer asset is an editor step; once it exists, music
  (`MusicPlayer`) and ambience get one options slider each through it.
- **Battlefield band.** When the tactical view lands, the same director with a
  different set (or the census radius forced to a few tiles) serves it.
