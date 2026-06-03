# Combat Model

## Decision

Combat is **force-vs-force on a tile**, not group-vs-group. Every owner's
units on a contested tile are pooled into one force; a lone unit is a
force of one. **One combat system regardless of size.**

**Trigger**: presence + hostility on arrival. After
`MoveArrivalEvent.Apply` or `GroupArrivalEvent.Apply` finishes its
position/sight updates, `CombatTrigger.MaybeBeginCombatOnTile` checks the
arrived-at tile for hostile-owner co-location. If any two owners with
units on the tile are hostile (`Diplomacy.AreHostile(a, b)`) AND no
combat is already active there, combat begins. Neutral/ally co-occupancy
is a no-op.

**Resolution**: multi-round, linear-proportional, simultaneous, zero
variance. Each `CombatRoundEvent` re-gathers current forces (so
reinforcement and retreat fall out for free), computes each owner's
damage budget as the sum of all *hostile* counterparts' start-of-round
power, and distributes damage to their units **lowest-Health-first,
tiebreak lowest-Id**. Rounds self-reschedule on a per-tile anchor
(`world.CombatStates`) until no hostile pair remains on the tile.

**Per-unit Health**: instance state on `Unit`. Default `BaseHealth = 10`
for every existing civilian role; `BasePower = 1`. Combat units (Soldier,
Archer, …), training, and equipment all land later via new
`UnitCombatCatalog` rows + `Unit.Buffs` entries without touching the
round event.

**Capture on death**: a dying laden unit's cargo drops to
`world.GroundResources[tile]`. `HaulPickupEvent` falls back to ground
piles when no structure sources the resource. Raiding pays — kill the
caravan, haul away the goods.

**Persistence**: an in-progress combat lives entirely in pure state
(`world.CombatStates` per-tile anchors + ground piles + per-unit Health
+ Buffs). `RegenerateQueue.From` reconstructs the `CombatRoundEvent` on
snapshot restore from the anchor's `(NextRoundTick, NextRoundSeq)` — the
same M4 discipline as build/production/war. A mid-fight crash recovers
to the identical battle outcome an uninterrupted run produces — that's
the M7 closure gate, proven by
`RecoveryTests.MidFightCombat_RecoveryResolvesIdentically` and
`CombatResolutionTests.MidFight_SnapshotRoundTrip_Identical`.

**Diplomacy gate**: `Diplomacy.AreHostile(a, b)` (M6) is the only gate
combat consults. Neutral and ally forces share tiles freely. The
relationship state and (for combat purposes) the trigger are
**deterministic and inspectable**.

**Structures are NOT combatants**. Sieges and capturing castles are a
later combat extension. The presence-gated trigger is unit/group-based.

## Why

### Force-vs-force, not group-vs-group

A force is everyone of one owner on the tile, grouped or not. Three
ungrouped units of one owner co-located on a tile fight as one combined
force of three, not picked off individually. Groups are a movement /
command convenience; they are **irrelevant** to how fighting works once
units are co-located. This avoids the branch on "group vs solo unit"
that would otherwise have to live in every combat code path.

### Linear-proportional, simultaneous, zero variance

Async fairness demands: no army-wiping coin-flips while a player is
offline. The simplest deterministic shape with the right pressure: each
side takes damage equal to the *other* side's force power, applied
simultaneously from start-of-round snapshots. Bigger force wins
predictably (the loser dies faster); both sides take damage (the chip
texture); no randomness anywhere — not even seeded random "which unit
dies" (a seeded roll can still cost you your best unit unfairly between
syncs).

### Lowest-Health-first casualty distribution

Deterministic, observable, intuitive: wounded units die before healthy
ones. Tiebreak on Id is arbitrary but stable. Training and equipment
will eventually give some units more Health and more BasePower — those
units naturally die last (because they have more HP) and kill more
(because they have more power), without the casualty rule needing to
know about role-typing.

### Multi-round with re-gather makes reinforcement and retreat free

Each `CombatRoundEvent` re-gathers forces on the tile. Units that
arrived between rounds join the gather (reinforcement). Units that moved
out are excluded (retreat). The interesting strategic plays fall out of
this single mechanism — no separate retreat-disengage logic.

### Per-tile anchor, M4-regenerated

`world.CombatStates: Dictionary<TileCoord, CombatState>` is sparse (one
row per active combat), tile-keyed, and follows the same canonical
serialization pattern as `world.Roads`. `RegenerateQueue.From`
reconstructs the queued `CombatRoundEvent` from the anchor on restore.
Same discipline as the M4 build/production/movement anchors and the M6
war-effective anchor.

### Per-unit Health as a single seam

Per-unit Health (vs atomic units) is more state to snapshot but
opens a single seam for *all* future combat layers:

- **Training** → buff with `HealthModifier > 0` ("Soldier" role +
  `UnitCombatCatalog.Spec(Soldier).BaseHealth = 20`).
- **Armor** → buff with `HealthModifier > 0` (worn equipment).
- **Equipment power** → buff with `PowerModifier > 0` (sword, bow).
- **Ranged-from-adjacent** → `CombatRules.GatherForcesOnTile` becomes
  `GatherForcesNearTile(tile, radius)`; round event passes `radius = 0`
  today.

None of these need to change `CombatRoundEvent`.

## Rejected alternatives

- **Group-vs-group as the primary unit of combat.** Would force a branch
  on group-vs-solo (or worse, an opaque "promote-solo-to-singleton-group"
  shim). Force-pooling-by-owner sidesteps this entirely.
- **Instant resolution.** A single-round wipe of one side is the classic
  "I went to sleep and lost my army to a coin flip" failure mode.
  Multi-round attrition opens windows for reinforcement and retreat.
- **Atomic units (no per-unit Health).** Simpler, but no seam for
  training, armor, equipment. Removing whole units proportional to
  losses is coarser; the texture of "the wounded soldier crawls home"
  is lost.
- **Random or seeded-random casualty distribution.** Async-unfair (you
  can lose your best unit to a roll you couldn't react to between
  syncs). Deterministic rule-based casualty distribution removes the
  risk entirely.
- **Casualty rule based on role.** "Kill the most valuable unit first"
  is hostile-to-the-player; "kill the least valuable first" is gameable.
  Lowest-Health-first ties the casualty rule to a stat (Health) that
  training and armor already modulate.

## Future expansion

The architecture leaves room to grow in several directions without
disturbing what's built:

- **Trainable combat roles**: new rows in `UnitCombatCatalog` with
  higher `BaseHealth` and `BasePower`. Zero round-event change.
- **Equipment + buffs**: `Unit.Buffs` is already snapshot-serialized;
  `CombatRules.EffectivePower` reads through it from day one. A buff
  with `HealthModifier > 0` is applied at equip-time (bumps current
  Health by the modifier).
- **Ranged-from-adjacent**: `CombatRules.GatherForcesOnTile(world,
  tile)` becomes `GatherForcesNearTile(world, tile, radius)`. The
  round event passes `radius = 0` today; ranged units pass `radius = 1`
  later. Damage budget from adjacent forces gets a falloff multiplier.
- **Targeted `AttackIntent`** (unit-level aggression on neutrals, for
  hunting / mobs): new intent that bypasses the
  `Diplomacy.AreHostile` gate. Resolution is unchanged — only the
  *trigger* is different.
- **Sieges / structures as combatants**: structures gain Health, become
  forces; `GatherForcesOnTile` includes their power. Capturing a castle
  is structure-death + ownership transfer.
- **Mobs**: a new faction whose intents are AI-generated. Combat treats
  them like any other faction.
- **Fog-ambush bonus**: a resolution-input modifier read from vision —
  attacker-from-fog gets a one-shot first-round damage multiplier.
- **Win/loss conditions**: separate deliberation. A faction at zero
  units just continues unit-less today.

## Acceptance tests

- `CombatStatsTests.Genesis_InitializesHealth_FromCatalog` — Health
  defaults from the catalog.
- `CombatStatsTests.EffectivePower_SumsBuffModifiers` — the rollup seam.
- `CombatTriggerTests.EnemyArrival_StartsCombat` — the trigger fires on
  hostile co-location.
- `CombatTriggerTests.NeutralArrival_NoCombat` /
  `AllyArrival_NoCombat` — fence the trigger to enemy-only.
- `CombatResolutionTests.BiggerForceWinsPredictably` — the linear-
  proportional math.
- `CombatResolutionTests.Reinforcement_MidFight_ChangesOutcome` /
  `Retreat_MidFight_StopsParticipation` — re-gather behavior.
- `CombatResolutionTests.SameTickContention_Deterministic` /
  `Twin_FullBattle_Deterministic` — twin-run determinism.
- **`CombatResolutionTests.MidFight_SnapshotRoundTrip_Identical`** —
  the M4 regen proof for combat. **The closure gate.**
- **`RecoveryTests.MidFightCombat_RecoveryResolvesIdentically`** — the
  durable-store crash-recovery proof.
- `CombatCaptureTests.LadenUnitDies_CargoDropsToTile` /
  `HaulFromGround_PicksUpLooseCargo` — the capture economy works
  end-to-end.
- `CombatPlayerViewTests.Combat_OnFoggedTile_HiddenFromView` — fog
  applies to combat.
- `CombatDeterminismTests.Views_DoNotAffectCombatState` — views are
  pure reads.

## Reference

Combat is the keystone for the design's strategic stack: it makes
diplomacy (M6) and groups (M5) consequential and unlocks raiding (the
capture economy → eventual trade, M7+). The next big system is
**spawning** — the paired birth-rate decision to combat's death-rate.
That's a separate milestone with its own deliberation.
