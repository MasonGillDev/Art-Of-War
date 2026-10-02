# M49 Spec: Group Stance

> Milestone spec. Decision: `docs/groups-first-class.md` (stance and doctrine). User,
> 2026-10-02: the player chooses only one of three stances; everything else is defined
> in code and gets more options when stances are expanded later.

## What we're adding

A group's **stance** decides how it fights. The player sets it with one choice
(`SetGroupStanceIntent`). Setting it on a parent sets it on everything under it. New
groups are **Defensive**.

| Stance | Starts fights? | When a member is attacked | Members on a battlefield fight as |
|---|---|---|---|
| **Passive** | Never | Nobody comes | Withdraw: every member leaves the fight |
| **Defensive** | Never | Every company of the same army within `AidRadius` (4) tiles of the fight marches to it and joins in, then returns | The role defaults: soldiers Hold, archers Support, others Withdraw |
| **Aggressive** | Charges a **visible** hostile within `EngageRadius` (3) tiles of any member, chasing it no further than `LeashRadius` (6) tiles from where the group was | As Defensive | Soldiers Advance, archers Support, others Withdraw |

The radii are constants (`GroupConstants`), not player settings. A member's own doctrine,
set with `SetBattleDoctrineIntent`, still wins over its stance.

## Rules

- **Only a group under command acts on its stance.** That covers mustered, marching,
  formed and route crews. A dismissed group's members are free individuals.
- **Aid** is event-driven, in the sim.
  - When a member of a non-Passive group is enrolled on a battlefield, every company in
    the same army (the tree under its topmost group) qualifies if it:
    - is not Passive,
    - is Idle or marching,
    - is not already fighting or helping,
    - and stands within `AidRadius` of the fight.
  - Each qualifying company records where to return (`Group.ReturnTo`) and marches to the
    battle tile. Its members join the fight as they step onto it.
- **Charge** (Aggressive): a server driver (`GroupStanceDriver`) spots a target, the
  same fog-fair way patrols do. It submits `ChargeGroupIntent(group, target)`, which
  marches the group to the target's tile and records `ReturnTo`. The driver re-aims the
  charge as the target moves, within the leash.
- **Return.** Once the group is Idle, with no member in a fight and no battle where it
  stands, it marches back to `ReturnTo`. A route crew instead goes back to its route:
  the hauling driver takes it to its stop.
- **Battlefield doctrine** has one source: `BattleDoctrine.Effective(world, unit)` gives
  the unit's own doctrine, else its stance's doctrine for its role, else the role
  default. It replaces four copies of `u.Doctrine ?? BattleDoctrine.DefaultFor(u.Role)`.

## Persistence

FormatVersion 52. Each group row gains `Stance` (byte) and `ReturnTo` (nullable tile).

## Headline test

An army of two Defensive companies sits four tiles apart.
1. An enemy attacks one company. The other marches to the fight, joins it, and returns
   home afterwards.
2. A Passive company in the same scene neither helps nor stays to fight.
3. An Aggressive company charges a visible bandit within 3 tiles, and it does not chase
   past 6.

A twin run and a mid-fight restore must give the same hash.
