# Groups are first-class: named, persistent, commanded bodies

## The decision

A **group** is a lasting thing in the game world. It has:
- a name
- a roster
- a place in a tree of groups
- a standing assignment
- a stance and a battle doctrine

It exists whether or not its members are standing together. The player's main verbs are:
- **Muster:** call the group together to a place.
- **Dismiss:** send it back to what it was doing.

When the group is dismissed, its members are free. Each goes back to the task it had before the muster, because the sim saved that task when it called them.

A mustered group marches **in formation, as one body**. The group itself takes the steps, at the pace of its slowest member, and its members walk in their places around it.

A haul route crew is **a group whose standing assignment is a route**. There is one system for "units that move together", not two.

Groups are available from the first minute. They are not a progression unlock.

## Why

### What forced it (user, 2026-10-02)

The world is going to get much more dangerous. Players will want organised bodies early, and a kingdom at scale cannot be run unit by unit. M5 groups could not carry that:

- **A group was only a shared order.**
  - Since M43, each member walks its own route at its own speed.
  - The group is "Moving" until the last one stops.
  - Nothing holds members together, and nothing sets a common pace.
- **A group could do nothing but walk.**
  - Eighteen intents and rules gate on `GroupId`: work, haul, build, train, equip, loot, embark, scout, breed and route crews.
  - Joining a group took a unit out of the economy for as long as the group existed.
  - "Disband" was the only way back, and it destroyed the group.
- **A group couldn't be bigger than a tile.**
  - A tile holds 16 of a side, fewer on structures.
  - Members past the cap got no place, never started walking, and were quietly left out of the move or the forming.
- **Route crews were a second, separate body.**
  - `RouteCrew` keeps its own member list.
  - The hauling driver sends each member its own `MoveIntent` and waits at each stop for stragglers.
  - Everything groups need (formation, pace, boats, stance) would have had to be built twice.

### User decisions (2026-10-02)

| Rule | Decision |
|---|---|
| Identity | Groups have names and persist. A group whose members all die is kept, empty, so it can be refilled. Delete is a separate, explicit act. |
| Dismiss vs delete | Dismiss frees the members and keeps the group. Muster calls them back to a tile in a few clicks. Delete removes the group. |
| Memory | A mustered unit remembers what it was doing and goes straight back to it on dismiss. "This needs to feel seamless." |
| Finishing work | On muster, a unit finishes its current job first. A hauler carrying valuable loot should not walk it into danger. |
| Stance and doctrine | The player sets them by hand. The aim is a battle plan the player can spend real time tuning. Start with the simple version. |
| Nesting | Groups nest. An army group holds the military groups, so mustering the army calls everyone. |
| Overflow | Mustering more units than a tile holds must work. |
| Route crews | A route crew is a group on a route. One system. |
| Haul queue | Groups never take jobs from the haul queue. |
| Merge / split | Groups merge, and members can be split out by hand. |
| Boats | A group crosses together. A group that does not fit the boat is refused; bigger boat classes come later. |
| Formation | Members keep formation while they walk. One default formation to start. |
| Membership | Any foot unit. No boats. |
| Limits | No cap on group size. At most three levels of nesting. |
| Progression | No gate. Groups exist from the start; the player decides when to use them. |
| AI | The AI stays on its own mustering code for now and moves onto groups later. |

Defaults the user accepted:
- A member in a fight joins the muster when the fight ends.
- When a parent and a child are mustered to different tiles, the latest order wins.
- Giving a mustered unit's held work slot to someone else cancels that unit's saved task.

### Alternatives that lost

- **Keep groups as shared orders (M5/M43, the status quo).** Lost for every reason under *What forced it*.
- **The group as one abstract map entity** (members hidden inside a single token).
  - It would give the strongest "first-class" feel and the cheapest movement.
  - It lost because everything else in the sim works on bodies: combat on the board, the tile cap, vision, death and cargo drops, healing, the king's aura.
  - A token would need a translation layer at every one of those points, and would turn back into bodies whenever a battle opens.
  - Keeping the bodies and having the group drive them costs less and loses nothing.
- **Formation by each member planning its own path.**
  - Each member would plan to its own slot, with step timing stretched to the slowest member's.
  - Lost because paths split around any obstacle, so the formation only exists on open ground.
  - It also keeps N step events per step where one would do.
- **Dismiss = delete (M5's Disband).**
  - The user's model is an army called up and sent home, again and again. Re-forming, renaming and re-tuning every time would make groups a chore.
- **Release the work slot on muster.**
  - Simpler, but someone else (or an automation order) takes the job. When the unit comes home, its task is gone and the player has to set it up again. That is the opposite of seamless.
  - The slot is **held** instead: it counts against the building's worker cap and produces nothing while the worker is away.
- **Drop the job at once on muster.** Lost to the user's loot rule: a laden hauler would walk its cargo into the fight.
- **Gate groups behind a building or a milestone.**
  - The progression ledger forbids gating automation, and groups are a command tool rather than automation, so a gate was allowed.
  - It lost on design: a dangerous early world needs organised bodies before any gate would open.
- **Split a group across boats automatically.** Lost to "refuse": the group would land in pieces, and bigger boat classes are the real answer.
- **Route crews as a separate body (M36/M45).** Lost to "one system". `RouteCrew` keeps its cursor and last-serve report, and its members become a group reference.
- **Doctrine as tactical AI** (target priority, focus fire, flanking).
  - `docs/automation-as-core-game.md` rules out tactical AI. `docs/patrols.md` held automation to *posture* for the same reason: presence buys finesse, never survival.
  - A group's stance and doctrine therefore stay posture: when to engage, how far to chase, when to leave, and the board behaviour the battlefield already has.
  - If the user later wants tactical rules, that is a deliberate change to the design rules and needs its own addendum.

## Shape

The engineering detail is in `docs/m46-groups-spec.md`. The shape:

- **Two layers of state on a group.**
  - **Standing assignment:** `None` or `Route(route, stop)`. This is the group's daily task.
  - **Command:** `None`, `Mustering(anchor)` or `Formed(at / marching to)`. This is the player's temporary override.
  - **Dismiss** clears the command:
    - A group with a route goes back to its route.
    - A group without one frees its members, and each member goes back to its own saved task.
- **Under command is what blocks solo work.** The `GroupId is not null` checks become one predicate, "is this unit under its group's command". A member of a dismissed group with no assignment can work, haul and build.
- **One owner per body.** A unit is in at most one lowest-level group. Parent groups hold only groups. A group holds units or groups, never both.
- **The group walks.**
  - A marching group owns one step anchor. Each step event moves every member to its place.
  - The step costs what the slowest member's step costs.
  - Event volume grows with the number of groups, not members.
- **The formation layout is a pure function.** Given the anchor, heading, ordered members, the world and the owner, it returns a place for every member. It spills onto neighbouring tiles in a fixed order when the anchor tile is full. Muster, halting, merging and landing from a boat all use it.
- **Saved tasks live on the unit** and are stored in snapshots: a held work or build slot, or an errand to re-issue. A group's route assignment lives on the group. Neither is derived; both are stored.
- **Doctrine reuses the battlefield's catalogue.**
  - A group carries a battle doctrine per role, from `DoctrineCatalog` (Hold / Advance / Support / Withdraw, plus a withdraw threshold).
  - Its members use it unless the player has given a unit its own.
  - The **stance** (Passive / Defensive / Aggressive, with an engage radius and a leash) is the world-scale half: whether the group starts fights while marching and how far it chases. It reuses the patrol and pursuit machinery from M29.

## Future expansion

- **Player-written doctrine.** The M18 automation engine already has condition/action atoms. A group's doctrine can grow into a list of player-built rules ("if two archers are hit, fall back to the retreat tile") without a second rules system. This is the "spend real time tuning the battle plan" goal. The simple stance and doctrine settings are its first rules.
- **Formation shapes.** The layout function takes a shape. v1 has one (a column four abreast on the march, closing into a block at the halt). Line, wedge and loose order are new shapes, with no change to marching or mustering.
- **Boat classes.** The boat rule reads seats live. Bigger hulls work unchanged.
- **The AI on groups.** The Rival and Defender muster with their own code today. Moving them onto groups replaces that code with the same verbs the player uses. Deferred until the user has played with groups.
- **Caravans and trade.** A trade caravan is a group on a route whose stop is another kingdom's post (the old M7 roadmap note). Nothing here rules it out.
- **What would need rework:** a body in two groups at once (for example a soldier who is both "Bridge Guard" and "Night Watch"). The one-owner rule is what keeps command and saved tasks unambiguous. It is ruled out on purpose.

## Update 2026-10-02: stance is three choices, defined in code (M49)

The user narrowed stance and doctrine for now. **The player chooses only Passive, Defensive or
Aggressive.** What each means is defined in code (`GroupStances`): battlefield behaviour per role,
coming to help, charging, and the radii (aid 4, engage 3, leash 6, all `GroupConstants`).

Per-role doctrine settings, and the engage and leash radii, are **not** player options yet. "When we
expand the stances we will give the user more options." The design above (doctrine reusing the M18
rule atoms) remains the direction for that expansion. A unit's own battlefield doctrine
(`SetBattleDoctrineIntent`) still overrides its stance.
