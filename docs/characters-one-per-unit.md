# Characters: one rigged person per unit, animated procedurally on the bones

**Decision (2026-09-17).** Units are drawn as ONE rigged character each — the
Polytope free peasants already in the project — placed by the same between-tick
interpolation the cohorts used, and animated procedurally on the Humanoid bones
from the sim clock and the unit's activity. No animation clips are used, because the
project has none. Roles without a character entry keep the instanced cohort.

## Why

- **The user's constraints:** use only assets already in the project; one character
  per unit for now; the stand-ins must be easy to strip when hand-made assets arrive.
- **The rigs are Humanoid**, with generated avatars, which makes them clip-agnostic:
  any Humanoid animation retargets later, and until then the bones can be driven
  directly. Driving bones about the character's WORLD axes (right/forward/up),
  parent before child, after restoring the bind pose each frame, needs no knowledge
  of the rig's bone-axis conventions — so the same code will animate the hand-made
  rigs too.
- **Rates come from `ActivityMotion`** (radians per sim tick), the same beat the
  placeholder figures bobbed to, so a stride freezes on pause and quickens with the
  pace dial like everything else.
- **Skinned meshes cannot go through the instanced batcher**, so this is a pooled
  GameObject per unit (`UnitFigures`), not an instance list. Hundreds are fine; if
  the count ever hurts, the fix is a near-camera cap, not boxes everywhere.
- **The pack's material is a built-in-pipeline shader** (magenta under URP). On
  spawn, any renderer whose shader is not Universal gets a shared URP Lit material
  carrying the pack's base texture — the building models' fix. Importing the pack's
  own URP package makes the swap a no-op. The garment renderer takes the faction
  colour as a tint; skin is left alone.

Alternatives ruled out for now: an Animator per figure with imported clips (no
clips in the project, and the user wants no new packs); vertex-animation textures
(right end state for crowds, premature before the look is proven); Synty's
characters (rigged the same, equally clipless, and modern rather than medieval).

## How to strip it

`CharacterSet` (Window > Aow > Set Up Characters creates `Assets/Settings/Atmosphere/
Characters.asset`) is the ONLY place that names a pack asset. `UnitFigures` asks for
"a prefab with a Humanoid avatar" and `CharacterFigure` asks the avatar for bones by
`HumanBodyBones`. Replacing the peasants is editing entries; deleting the pack folder
leaves empty entries and the roles fall back to cohorts. Nothing is copied into
Resources or referenced by GUID from code.

## Future expansion

- Real clips: add an Animator Controller and let `CharacterFigure` step aside for
  roles whose prefab carries one (the Humanoid retarget makes Mixamo-style clips
  drop in).
- Kit in the pack's weapon/shield/accessory slots per role.
- A near-camera cap with instanced static figures beyond it, or a vertex-animation
  bake, if the pooled count ever shows on the frame budget.
- Role dress via the pack's mask colour slots once its URP package is imported.

## Update 2026-09-17 (later) — one size, markers, click the body

First play was "truly bad": people swelled and slid as the camera zoomed, were
hard to click, and stood in a T pose. The user's call: strip the inflation.

- **Zoom inflation is gone.** `PresentationScale.Inflation` is a constant 1 and
  `SetZoom` no longer exists. A person is `EntityScaleSettings.PersonHeight` world
  units tall at every zoom (default 2.5 — an exaggeration, since a true 1.8 m person
  is three pixels at this camera's closest zoom, but a CONSTANT one). Boats scale off
  the same number (`HullScale`).
- **Legibility at altitude is a marker, not a bigger body.** Past `MarkerDistance`
  (default 900 units) the character's renderers switch off and a faction-coloured pin
  of constant screen height (`MarkerPixels`) is drawn through the instanced batcher at
  head height. This is the "overview markers" slot the HUD had reserved.
- **One stand point, three readers.** `UnitMotion.StandPoint` is where a person
  stands: the interpolated position, pushed deterministically off any building on the
  tile (from the model's own placement footprint). The renderer draws there, the
  picker tests there, the selection ring and torches sit there — so the click volume
  cannot drift from the body again.
- **Picking is a cylinder around the person** with a least radius in PIXELS
  (`PickPixels`, default 12), computed from the camera's field of view, so a distant
  figure or its pin is still clickable. The cohort cylinder, cohort radius, ranks and
  scatter are deleted from the renderer and picker; a role without a character draws
  one placeholder figure, not twelve.
- **Bind pose:** arms are dropped to the sides in every pose, with the drop direction
  found per arm at bind time. `CharacterSet.DebugForceWalk` forces the walk cycle so
  the bone driver can be judged in one look; if it proves inert on this rig, the next
  step is runtime-authored legacy clips played by Unity's own animation system.

## Update 2026-09-17 (evening) — choreography: what work looks like, without clips

**The decision.** Animation is split into two layers so production assets can land
later without redoing the game side:

- **`WorkChoreography`** decides WHEN and WHERE: a pure function of (unit, activity,
  the building on its tile, the sim clock, the unit's hash) to a `Beat` — an offset
  inside the tile, a facing, a `FigurePose`, and visibility. Farmers walk furrows up
  and down their own lane; lumberjacks pick a spot in the tile's outer band, walk to
  it, chop for a session, move on; miners walk into the shaft, vanish for half the
  trip and walk out carrying; builders hammer; idle people in the wilderness sit, and
  two or more sit in a ring around a campfire drawn at the tile centre. Nothing leaves
  the tile the sim put the person on; nothing reads back into the sim; everything is
  deterministic in id and time, so it pauses with the world and agrees across clients.
- **`CharacterFigure`** decides what a pose LOOKS like: procedural bone turns today
  (Walk, Carry, Hoe, Chop, Pick, Hammer, Sit, Wait, Idle), with a shared stroke shape
  (slow lift, fast fall, rest). This is the layer clips replace, pose for pose.

**When to go to production animation.** When the look is the bottleneck, not before.
The mechanics above are the product; the clips are the paint. What to keep when the
clips come: the `FigurePose` names (a clip per name), the `Beat` contract, and the
Humanoid rig requirement. What goes: the bone-turn bodies in `CharacterFigure`, one
case at a time. Real props (an axe in the weapon slot, a real fire) slot into the
same beats.
