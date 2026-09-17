# Art direction

**Source:** the owner's art board, ten images at `Art Of War/ArtBoard/` (2026-09-16).
This doc reads them as a brief. It settles the *look*; `docs/atmosphere-rig.md`
covers how the atmosphere that carries it is built.

## The decision

The game is **painterly stylised realism at a diorama camera, lit at golden hour,
with layered mist** — the Manor Lords / Age of Empires IV family rather than either
photoreal simulation or flat low-poly. Chrome is dark translucent glass with thin
gold and parchment accents, and it stays out of the way.

**This closes the art-style A/B in all but detail.** Voxel is out. Flat low-poly is
out. What remains open is how much geometric detail a building carries and how
stylised the silhouettes are — a question two swatches can still settle, but inside
this family rather than across families.

## What the board asks for, term by term

| | The board | What it means for us |
|---|---|---|
| **Camera** | Every image is an oblique view, roughly 35–45°, at a distance where a house, a cart and a person all read. Never top-down. | This band is the game's face. It is what to tune, screenshot and judge against — not the full-map view. |
| **Atmosphere** | Mist as LAYERS: ribbons in valleys, banks in the middle distance, a cloud sea at the horizon and past the frontier. The played land is clear. | See the atmosphere-rig addendum: coverage must fall off toward the player, not blanket the map at a fixed height. |
| **Light** | A low golden sun in most images, strong and directional, with cool blue-grey shadows and real contrast. Visible shafts. | Golden hour is the default tuning condition. High contrast between key and shadow, not an overcast wash. |
| **Night** | Deep blue land, warm pools of torchlight, lit windows, campfires. | Night is readable by contrast, not by brightness. Point lights carry it, which is why the shared lighting include handles additional lights. |
| **Colour** | Saturated greens and gold in the lit foreground; distance desaturates into blue-grey mist; ruins and dead land go grey-brown. | The grade (C3.3) does the desaturation with distance and knowledge, not the textures. |
| **Structures** | Timber, thatch, stone. Warm wood, grey stone, gold thatch. Banners and heraldry everywhere, at gates, towers and camps. | Heraldry is a first-class prop, not decoration: it is how a player reads whose land they are looking at. |
| **People** | Small but present — carts, herds, workers in fields, soldiers in formed blocks. | The cohort/figure scale work already assumes this. |
| **UI** | Dark translucent panels, thin gold rules, parchment text. Resources top-left, log right, actions bottom-left, minimap bottom-right, context panels only on selection. | The current HUD's shape already matches. What changes is the skin. |

## Content the board takes for granted

Every one of these is either built or on the roadmap, which is a good sign the
board and the design agree:

- The **realm log** as narration ("Scouts have mapped new terrain to the east",
  "Bandit activity reported in the eastern region") — C5's chronicler.
- A **day and time clock** with a sun/moon marker — C3.1's world clock.
- **Ruins to rebuild** over a dead kingdom's bones — the dead-kingdoms work.
- **Canal construction** with a progress bar and haulers feeding the site — M27.
- **Army formations** on a battlefield with hold/advance/flank/retreat — C6.
- A **scout on a ridge** overlooking an enemy camp — the scouting spec.
- The **king and his heir** on the wall, named, with ages — M31 dynasty.

## Why this, and what it rules out

The board is consistent across ten images, which makes it a real brief rather than
a mood. It rules out:

- **Voxel.** Considered and set aside earlier; nothing on the board supports it.
- **Flat low-poly.** The board's silhouettes are simplified, but surfaces carry
  material and wear.
- **Photoreal.** Colour is pushed, contrast is composed, and scale is honest to
  gameplay rather than to life.
- **A bright, even, overcast presentation.** Every image is lit by a low, strong
  key with deep shadow.

## Future expansion

- **Swatches inside the family.** The remaining A/B is detail level, not style
  family: two dock-town swatches, both painterly, one leaner in geometry.
- **Season and weather** change the palette without changing the style: the board's
  golden hour and its grey ruined-land image are the same world.
- **The UI skin** can be built now against the current HUD's structure, since the
  board confirms the layout rather than replacing it.

## Acceptance

- A Look Lab bookmark at the board's camera band (oblique, buildings legible) is the
  first thing every atmosphere change is judged at.
- At golden hour, lit ground reads warm and shadows read cool, with visible contrast
  between them.
- At night, the land reads by torchlight pools rather than by an overall brightness.
- Distance desaturates into mist; the foreground keeps its colour.

## Update 2026-09-16 — colour space, and why the palette kept reading as mint

**Terrain colours are authored in sRGB and MUST be converted to linear before they
become vertex colours.** `TerrainColorizer.Paint` does this once, at its last line,
and it is the only place that should.

The project renders in linear space (`m_ActiveColorSpace: 1`), and `mesh.SetColors`
performs **no** conversion — vertex colours arrive at the shader as raw floats and are
used directly as linear albedo. So grassland's `(0.42, 0.58, 0.29)`, which reads as
honest grass in a colour picker, was consumed as linear: equivalent to sRGB
`(0.68, 0.79, 0.57)`, a pale mint about twice as bright as intended.

This is the real cause of a symptom chased for a long time — open country reading as
flat, washed mint no matter how the palette was retuned. **The palette was never
wrong; the missing conversion was.** Retuning the palette to compensate would have
baked the error in permanently and broken every other layer.

Two rules follow:

- **Blend before converting.** Lerps between authored colours (road wear, staleness)
  belong in the authored space, where mixing behaves as the eye expects. Conversion is
  the final step.
- **Do not "fix" brightness by darkening the palette.** If ground reads too bright,
  check the conversion and the lighting stack first. A palette darkened to cancel a
  colour-space bug looks correct in exactly one lighting condition and wrong in all
  the others.

A related trap, same session: the terrain shader normalises its detail texture by the
texture's own **per-channel** mean (`_DetailMean`, measured from the asset, not typed
in). A single scalar there leaves the detail texture's own hue multiplied onto every
tile, which re-greened desert and snow alike — a second, independent source of mint.

**Never blend a terrain detail layer by distance from the camera.** A camera-distance
fade draws a disc of detail centred under the camera that slides along with it, and it
reads unmistakably as a spotlight on the ground. Shortening the radius only tightens
the ring. The terrain shader had exactly this and the close-detail layer was removed
rather than tuned — at the zooms this game is played at, a ~17 m repeat is below a
pixel, so it bought nothing and cost a texture sample per fragment. If near-ground
detail is wanted later, blend by **projected texel density** (screen-space derivatives),
which follows perspective and has no rim.
