# M49 status: group stance

Spec: `docs/m49-group-stance-spec.md`. Built 2026-10-02 in one phase.

## What was built

- **`GroupStance`** (Passive / Defensive / Aggressive) on each group. The default is Defensive.
  `SetGroupStanceIntent` sets it on the group and everything under it. It is the player's only
  choice; the radii are `GroupConstants.AidRadius` 4, `EngageRadius` 3 and `LeashRadius` 6.
- **`GroupStances`** (`Sim.Core/Groups`) defines what each stance means, in one place:
  - the battlefield doctrine each stance gives each role
  - **aid**: on a member's enrollment, nearby non-Passive companies of the same army march to the
    fight
  - **return**: back to `ReturnTo` once standing still with no fight; a route crew goes back to its
    route instead
  - **charge**: whether it is allowed (`ChargeRefusal`: Aggressive, a visible hostile, within the
    leash)
- **`BattleDoctrine.Effective`** is the one doctrine read, replacing four copies.
- **`ChargeGroupIntent`** plus **`GroupStanceDriver`** (Sim.Server, wired into `GameHost`'s think loop).
- **Wire:** `GroupDto.Stance`, and `GroupDto.Away` (gone to help or charging).
- **Snapshot v52.**

## Calls made while building

- **A stance acts only while its group has its members under command.** A dismissed group's members
  fight by their role defaults and don't summon help.
- **Aid takes only companies that are standing or marching** (not mustering), not already fighting or
  away, and within `AidRadius` of the fight.
- **Who helps:** the whole army (every company under the topmost group), not just the attacked
  company's siblings.
- **Driver re-aims:** an Aggressive group already charging keeps chasing any visible hostile within
  the leash of where it set off.

## Tests

`GroupStanceTests` (6): doctrine by stance (and the unit's own doctrine winning); setting the army's
stance sets every company; Defensive aid and return (with Passive and too-far companies staying
put); an Aggressive charge within the leash but not past it; a charge refused when not Aggressive;
twin run and mid-fight restore.
