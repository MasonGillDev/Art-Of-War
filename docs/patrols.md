# Patrols — automating posture, not tactics

## The decision

A **patrol** is a Routine circuit with two extra knobs, `EngageRadius` and
`LeashRadius`. When a hostile comes within `EngageRadius` of the party, the
crew breaks off and **pursues it on foot**; when the chase ends — the target
dies, escapes the leash, or leaves sight — the crew walks back to its current
stop and resumes the loop.

Pursuit itself is a **sim verb**, not a driver behaviour: a new on-unit anchor
`Unit.Pursuit` (the `HaulPlan` pattern) re-paths one hop at a time inside
`MoveArrivalEvent`, so a chase runs at the sim's tick resolution rather than
the driver's hourly think.

Everything the patrol automates is **posture**: be here, close that distance.
Everything tactical stays with the player or with existing sim rules — combat
already auto-resolves on contact, so nothing about the fight itself is
automated.

## Why

`docs/automation-as-core-game.md` bans role-AI, goal-AI and tactical-AI, and
pins war as the wrench that erodes automation near the enemy. A patrol sits
exactly on that line, so each rule below is chosen to stay on the legal side
of it — and each closes off an alternative that would have crossed it.

### Party-local aggro, not kingdom-wide

Hostiles are detected **within `EngageRadius` of the party**, never "anywhere
in my territory".

The rejected alternative — dispatching patrols to any sighting inside the
kingdom — is goal-AI: it makes one patrol into a police service, and it
deletes the spatial game. If the route *is* the coverage, then **where you
walk is what you protect**, and a larger kingdom needs more patrols. That is
the logarithmic-scaling doctrine (user, 2026-08-04) falling out of a local
rule instead of being legislated.

The kingdom-wide version already exists in the correct form: the alert log
tells the **player**, and the player responds by hand. Automation local,
response global.

### The leash is anchored to the ROUTE, not to where the chase began

Pursuit continues while the target is within `LeashRadius` of the **patrol's
current stop**.

Anchoring to the chase's origin was rejected because it invites leash-creep:
an enemy hovering at the boundary re-aggros and drags the party across the map
in `LeashRadius`-sized increments. Anchored to the route, a patrol physically
cannot be pulled further than `LeashRadius` from the line the player drew.

What the leash *creates* is the point: **baiting becomes a real enemy tactic.**
A rival can kite a patrol off the road to walk a raid past it. The counter is
composition — shorter leashes, overlapping routes, a second patrol — never a
smarter unit. A human actively kiting should beat a dumb leash; that is
"presence buys finesse, never survival" working as designed.

### Pure pursuit — chase from behind, never intercept

Each hop, the pursuer steps toward the target's **current** tile.

Interception (predicting where the target *will* be) is rejected outright: it
fails the five-question test's "no adversary-reactive choice". Pure pursuit is
deterministic, readable from the map, and has a consequence worth having: an
equal-speed runner **escapes** a straight chase. So catching anything comes
from positioning — a patrol on a road (speed bonus) runs down raiders crossing
open ground, and cornering someone takes two patrols. The player builds the
pincer; the units stay dumb.

### Bandits and declared enemies only — never neutrals

A patrol must not be able to start a war. War is a player act with a
telegraph delay (`docs/diplomacy-model.md`); an automation that committed the
kingdom to war by walking into a rival's scout would blow a hole through it.

### The party fights as a body

Like a circuit walks as one, a patrol engages as one — no splitting, no
"soldiers engage while haulers flee". That would be tactical AI. Put a farmer
on a patrol and the farmer dies; the tools are there for a smart player to
avoid that.

### Walk on past the loot

A patrol that wins does **not** loot. Piles and graves compose with the
existing salvage/hauler machinery **through the world**, which is law 3
(compose through warehouses, never order-to-order). Looting inside the order
would make the patrol a second logistics system.

### Contact must land ON the tile

Combat triggers on **same-tile co-location** (`CombatTrigger`), not adjacency.
Pursuit therefore terminates by stepping onto the target's tile, where the
existing trigger takes over: it pins both parties (zone of control) and
schedules round 1. Nothing in the patrol layer resolves combat.

## Shape

Not a new program. `ProgramKind.Routine` with `EngageRadius > 0`; `0/0` is the
pacifist circuit that already exists. Same Named-crew rule, same durable
cursor, same mortality — kill the crew and the patrol goes dark (law 4).

## What this leaves room for

- **Structures as targets.** Pursuit anchors on a unit today. Sieging a
  spotted camp or tower is the same anchor with a tile target.
- **Stances.** `EngageRadius` is already a posture dial; a "defend only"
  stance is a smaller radius, not new machinery.
- **The morning report.** A patrol's real product in an absent-player economy
  may be the *report*, not the kill: "patrol #3 engaged bandits at (41,12),
  two dead" is a border tripwire. The journal already carries it; the client
  edge-detects the pursuit detail into the alert log with no new wire field.
- **Deferred deliberately:** interception, formations, target prioritisation,
  auto-retreat, kingdom-wide dispatch. Each is a tactical decision the player
  should be making.

## Built 2026-08-11 (M29)

Shipped in five phases: the pursuit anchor (`Unit.Pursuit`, `PursuitRules`,
`EngageUnitIntent`, snapshot **v26**), the two order knobs, the driver's engage
pass in `RunRoutine`, tests, and the client (`+ Patrol` flow, dashboard row,
engagement alert). 933 + 38 green; wire contract live-verified.

Three things the build settled that the plan had not:

- **The leash anchors to the NEAREST stop, not the current one.** Anchoring to
  the cursor's stop left dead zones in the middle of a long leg, where a patrol
  could see trouble but was forbidden to answer it. Nearest-stop makes the
  bound a *corridor* around the route, which is what the doc always claimed.
- **The leash is part of the DECISION, not just of the chase.** A target
  already outside the leash would be released on the chase's first step, so
  engaging it would submit a doomed intent every think. `FindQuarry` refuses
  those, which is why `LeashRadius < EngageRadius` degrades quietly instead of
  thrashing (the client clamps leash ≥ engage anyway).
- **Being pinned ENDS a chase.** A pursuer that walks onto a contested tile is
  held there by the zone-of-control pin; stepping again would march it back out
  and defeat the pin. Suspending the chase instead would leave a live anchor on
  a unit with no arrival scheduled — the permanent-brick shape — since nothing
  would re-enter `Step` once the fight resolved.

An ordering trap worth remembering: `TrySetActivity` bumps `AssignmentEpoch`,
and `ScheduleNextHop` stamps the arrival event with that epoch as it schedules.
Setting the activity flag *after* starting a move therefore fences the very
event just created — the chase froze on its first step with its anchor still
set. Activity must be set before the move.

## Acceptance

- A patrol engages a bandit inside `EngageRadius` and ignores one outside it.
- It ignores a **neutral** rival standing in the middle of the kingdom.
- An equal-speed target that keeps running **escapes**; a slower one is caught.
- Breaking the leash returns the crew to its stop and the circuit resumes at
  the same cursor.
- A patrol that fought and killed **replays byte-identically with the driver
  off** — the chase unfolds from the logged `EngageUnitIntent`s alone.
