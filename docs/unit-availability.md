# Unit availability: one rule for "is this unit busy?"

## The decision

Whether a unit is free to be given something to do is answered in one place:
`UnitAvailability.Busy(world, unit)` (`src/Sim.Core/World/UnitAvailability.cs`). It
returns *why* the unit is busy (a `BusyReason`) or `None`. It is **derived** from the
unit's existing state on every call, not stored.

Every system that picks free units reads it:
- the automation's dormancy (`ClaimLedger.IsDormant`)
- the hauling driver
- the bandit driver
- a route stop's serve
- a group's muster (`GroupMuster.Busy`)
- the AI, through `UnitDto.Busy` on the wire

Each keeps only what is its own business on top:
- the automation adds "not under a group's command" and "not claimed by an order";
- a muster decides which kinds of busy must *finish* before a member answers.

**A unit on a battle's tile is busy (`Fighting`) until it dies or leaves the tile**,
whether it is on the board or waiting at its edge for room (user, 2026-10-02).

## Why

### What forced it (2026-10-02)

The user asked who serves a route stop and what a crew does when a member is attacked.
Tracing it showed six separate "is this unit free?" checks. Each started from
`Unit.Activity` and added its own pick of extra conditions:

| Where | Counted a fight as busy? |
|---|---|
| `ClaimLedger.IsDormant` | no |
| `HaulingDriver.IsFree` | no |
| `ServeRouteStopIntent` (who stands at the stop) | no |
| `GroupMuster.Busy` | only on the board |
| `ThinkContext.IsIdleStill` (AI) | no |
| `BanditDriver.Free` | yes |

So a route crew could be served with a member mid-battle, then marched to the next stop
without it. `Activity` alone can't answer the question: walking, fighting and riding a
boat all read `Idle`. That is the "M16 pitfall" each system had worked around in its own
way.

The user's direction: "being in a fight should never count that unit as idle." When
shown that each system computed "free" for itself, they said "this is the kind of stuff
we should always be looking out for."

### Alternatives that lost

- **Patch each check.** Six edits for one rule, and the seventh system written next would
  repeat the drift. This is the failure mode the decision exists to end.
- **A stored flag or state on the unit** (for example an `Activity.Fighting` value, or a
  `Busy` field).
  - Every transition that changes availability would have to write it: every step, every
    battle joined or left, every haul event, every goal started or dissolved, every boat
    boarded. One missed write is a unit stuck busy or wrongly free, and the snapshot would
    carry the mistake forward.
  - `Activity`'s state machine also forbids direct hops (Working → Fighting), so folding
    the fight into it would reshape the work and production code.
  - A derived read can't fall out of step, and it is a pure read, so it can't touch the
    determinism contract.
- **Leave the AI on its own check.** The AI reads only its view (`ViewDto`), so it
  can't call Sim.Core. Carrying the same answer on the wire (`UnitDto.Busy`) keeps the AI
  and the client on the rule the sim acts on, without breaking the fairness wall.

## Shape

- `BusyReason` is an append-only byte enum (it crosses the wire): Fighting, Aboard,
  Breeding, Surveying, Scouting, Chasing, Hauling, OnAnErrand, Working, Building,
  Walking. The first match wins, most pressing first.
- **Cost.** One structure scan for an active breeding (`Population.GetActiveBreedingFor`),
  as `IsDormant` already did. If a profile ever shows it, the fix is an index on the
  house's occupation, not a stored busy flag.
- **Not covered:** player orders. A player may still give a unit in a fight a battle
  order. Availability is about what the *systems* may pick up, not what the player may
  command.

## Future expansion

- A new kind of busy (a unit at a forge, a guard on a wall) is one reason and one line
  here, and every system respects it at once.
- Stance (the next group work) reads the same rule to decide which members can go to
  each other's aid.
- If a reason ever needs to be stored for performance, it gets a single write point and
  this function stays the only reader the systems call.
