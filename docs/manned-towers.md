# Manned Towers — eyes or teeth, one body per tower

**Status:** decided 2026-09-17; not yet built. Proposed milestone **M32**.
**Depends on:** M3 towers + `Sight`, M14 roles, M24 structure health, M26
adjacency sieges, M30 goal-shaped assignment, the automation substrate's
`Staff` recipe.

## The decision

A Tower gains **one garrison slot**. Who stands in it decides what the tower
does:

- **A Scout** widens the tower's vision radius (today's unmanned 7 becomes a
  larger, tuned value). Eyes.
- **An Archer** makes the tower a combatant: it adds the archer's power, with
  distance falloff, to any combat that opens within its **fire radius**, as a
  defender that takes no damage in that fight. Teeth.
- **Anyone else** may stand the post and does nothing. The slot is still
  useful as a place to keep a body, and the rule "role decides the effect"
  stays uniform.

Manning is the ordinary assign verb, goal-shaped like every other assignment
since M30: the player names the unit, the unit walks, the post is taken on
arrival. Keeping the tower manned is the substrate's `Staff` recipe pointed at
a Tower instead of an extractor. Nothing new is invented for automation.

## Why

### What it fixes

- **The Scout has no peacetime job.** A scout runs a mission (M20) and then
  stands idle. A watch post is a standing role, and it turns "kill the
  watchman" into a bandit move the morning report can name. That is law 4 of
  `docs/automation-as-core-game.md` (automation embodied in mortal crews)
  applied to vision.
- **The Archer has no ranged behaviour.** M14 shipped Archer as a stat row and
  deferred ranged combat because it touches the combat trigger, the engagement
  pin and the force-gather seam all at once (`docs/combat-model.md`,
  `docs/military-training.md`). A tower-anchored archer is the narrowest
  stationary case of that seam: no trigger change, no pin change, one
  gather-near-tile read.
- **Walls and gates shape where enemies walk but never make walking there
  cost anything.** An archer tower makes the chokepoints M26 built into
  killing ground. The border scenario in the vision doc ("wall-and-gate the
  pinch point, set soldiers to patrol it") gains its third tool.

### Alternatives that lost

- **Two slots (scout and archer together).** Lost: the choice between eyes and
  teeth is the interesting decision, and the composition doctrine says a
  second tower is the answer, not a bigger building. One slot also keeps the
  Tower's snapshot shape trivial.
- **A separate Watchtower / Archer Tower building pair.** Lost: two structure
  kinds, two catalogue rows, two build costs, and the player has to decide the
  tower's purpose at placement instead of re-manning it as the border moves.
  Re-manning is the frictionless-redesign verb the vision asks for.
- **Vision that depends on the tower alone (no scout bonus).** Lost: it leaves
  the scout idle, and it makes the tower unkillable as an intelligence source
  short of a siege. With a scout, bandits can blind a border by killing one
  person, which is the kind of pressure that forces redesign.
- **Volleys at anyone walking past, with no combat open** (a periodic
  tower-fire event). Deferred, not lost: it needs a new scheduled event, its
  own casualty rule, and a balance pass, and it is unclear it is needed once
  the tower joins nearby fights. Ship the combat-join form first and see.
- **Full ranged combat for archers in the field.** Out of scope. This doc
  deliberately uses the ranged seam at radius 1 in the narrowest possible
  way so that the field version, when it comes, has a working precedent.

### Trade-offs accepted

- **Unmanned vision stays 7.** Every existing test, both AI ladders and the
  M17 balance curve rely on towers seeing 7 tiles. Dropping the unmanned
  radius would make manning matter more, but it is a retune of everything
  that exists; it is recorded as a knob, not a change.
- **The archer in a tower is invulnerable inside the fight it joins.** It can
  only die when the tower itself is besieged from adjacent tiles (M26
  `FortSiege`) and razed. This is the same asymmetry a wall has and it is what
  makes the tower worth building. The counter is the existing one: come
  adjacent and pull it down, or route around its radius.
- **The Rival must learn towers or it will suicide into them.** The conquer
  rung's overmatch gate estimates enemy power from what the brain has seen;
  it must add manned-tower power along the march and at the target, and its
  siege should target the tower before the castle when both are in reach.
  Without this the balance lab lies. That AI slice ships with the feature.

## Rules that pin the design

1. **Bandits and declared enemies only.** A tower never fires on a neutral or
   an ally, so a tower cannot start a war. Same rule as patrols.
2. **The tower joins fights, it does not open them.** Combat still opens on
   same-tile contact or on adjacency to a fortification, exactly as today. A
   manned archer tower contributes to a `CombatRoundEvent` whose tile lies
   within its fire radius and whose sides include someone hostile to the
   tower's owner.
3. **Falloff by distance.** Contribution = archer `EffectivePower` scaled by a
   per-ring multiplier (full at the tower's tile, decreasing to the radius
   edge). One table, catalogue-owned, config-derived in tests.
4. **The garrison is a worker.** `Unit.Activity` is `Working`, the unit is
   not dormant to selectors, it is not idle to other intents, and it eats
   from its house like everyone else (bread is electricity).
5. **Raze drops the garrison like any death.** The unit dies with the tower,
   its equipment drops to the tile, the grave tracker mints a marker if loot
   fell, and the alert log names it.
6. **Unassign is the ordinary unassign.** Leaving the post is
   `UnassignWorkersIntent` against a Tower; the unit goes Idle on the tile.
7. **Retraining a garrison strips the post.** A scout retrained to a farmer
   while standing the post leaves the post; the tower reads its garrison's
   role live, so nothing else needs to notice.

## Mechanics (all existing seams)

| Piece | Where | Change |
|---|---|---|
| Garrison slot | `Tower` in `src/Sim.Core/World/Structure.cs` | `int? GarrisonId`, serialized; format version bump |
| Manning | `WorkAssignment` | generalize `TryAssignWorker` from `Extractor` to a "workable" structure, or add `TryAssignGarrison` beside it; `AssignWorkersIntent` accepts a Tower target; M30 goal dispatch unchanged |
| Vision | `Sight.RadiusFor` | takes the structure, not the kind: Tower with a Scout garrison returns the manned radius; `View` already unions structure discs |
| Reveal | `BuildCompleteEvent`, `MoveArrivalEvent` | arrival at a tower post is a new reveal trigger (the disc grew); departure needs nothing (visibility is a pure read, explored never shrinks) |
| Combat | `CombatRules.GatherForcesOnTile` → `GatherForcesNearTile(world, tile, radius)` | the seam `docs/combat-model.md` already names; only manned archer towers within radius are added, tagged as non-casualty contributors |
| Damage | `CombatRoundEvent.Apply` | tower contribution enters the defender power sum; the lowest-health-first casualty rule skips tower contributors |
| Siege | `FortSiege` / `SiegeDamage.RazeStructure` | on raze, kill the garrison via the ordinary death path |
| Automation | `SubstrateDriver.RunStaff`, `SetOrderIntent` validation | Tower is a legal `Staff` subject; `WorkersBelow(tower, 1)` is the trigger; selector role Scout or Archer as the player chooses |
| AI | `ConquerRung`, `EnemyIntel`, `DefendRung` / `FortifyRung` | overmatch counts manned towers; Fortify may man its own towers with an archer once it owns one (small, optional) |
| Wire | `StructureDto` | `GarrisonId`, `GarrisonRole`; the client draws the fire radius and the widened vision ring on hover |

Determinism: no new anchors. The garrison id is plain state written by
intent resolution; the fight contribution is a pure read at round time; the
reveal is the existing inverted pure-read wall. Recovery is free.

## Knobs (tests derive from these, never hard-code)

- `Sight.TowerMannedRadius` (scout in the post). Start at 11.
- `TowerFireRadius`. Start at 2 (a 5×5 killing ground around the tower).
- Falloff table by ring: `{1.0, 0.75, 0.5}`.
- Unmanned tower radius stays `7`.

## Acceptance tests

- **Vision.** A scout assigned to a tower reveals tiles at the manned radius
  and no further; an archer or farmer in the same post reveals 7; the scout
  leaving the post does not shrink `Explored`; a snapshot mid-walk to the
  post restores and completes the reveal.
- **Fight-join.** A bandit party opens combat on a tile two rings from a
  manned archer tower: the defender side gains the falloff-scaled archer
  power; the archer is never a casualty; the same fight three rings out gains
  nothing.
- **No war by tower.** A neutral faction's unit fighting bandits within the
  radius gains nothing and loses nothing; the tower fires only when a side is
  hostile to its owner.
- **Siege.** Attackers adjacent to the tower besiege it as M26 says; the
  archer keeps contributing to fights within radius while the siege runs;
  raze kills the garrison and drops its bow.
- **Staff order.** A `Staff` order on a tower with `WorkersBelow(tower, 1)`
  pulls a dormant scout, walks it, and reports `Waiting` once manned; kill the
  scout and the order re-pulls next think.
- **Rival.** The conquer rung's estimate for a target inside a manned tower's
  radius includes the tower; a Rival with an overmatch gate at the shipped
  default does not launch into a tower it cannot beat. Sparta pin holds: a
  peacetime Rival's curve is byte-identical with the feature present and no
  towers manned.
- **Headline.** Twin-run hash equality across build → man → bandit fight →
  siege → raze, with the substrate driver live and then replayed driverless.

## What gets built now vs later

**Now (M32):** the slot, manning via the ordinary assign path, scout vision,
archer fight-join with falloff, raze kills the garrison, `Staff` on towers,
Rival overmatch awareness, wire fields, tests above.

**Later, each on the seam this leaves:**

- **Volleys at passers-by** with no open combat: a scheduled tower-fire event
  reading the same `GatherForcesNearTile` and falloff table.
- **Arrows as ammunition**: a `Resource.Arrow` stock on the tower, drained per
  round, restocked by a supply line. Turns a tower into a logistics consumer
  and gives the border its own wage bill.
- **Field ranged combat** for archers: the same gather-near-tile read at
  radius 1 for units, the M14 deferral proper.
- **Gate and wall garrisons**: the slot generalizes to any fortification;
  "man the wall" is the same recipe.
- **Unmanned-vision retune**: lowering the empty tower's radius once the AI
  ladders can man their own towers.
- **Stances**: fire radius as a per-tower knob (hold fire, short, long) is a
  posture dial, not new machinery.

## References

- `docs/automation-as-core-game.md` — laws 3, 4 and 5; the border scenario.
- `docs/automation-substrate.md` — the `Staff` recipe and selectors.
- `docs/combat-model.md`, `docs/military-training.md` — the ranged seam and
  why it was deferred.
- `docs/walls-and-gates.md`, `docs/sieges-and-conquest.md` — adjacency sieges,
  structure health, raze.
- `docs/patrols.md` — the "never start a war" rule and party-local aggro.
- `docs/goal-shaped-intents.md` — manning as a walk-then-assign goal.
