# M46 Spec: First-Class Groups

> Milestone spec (workflow §7 step 1). This is the *what*. Phases are
> sketched at the end, and the plan file (step 2) pins them. The
> decision and its losing alternatives are in `docs/groups-first-class.md`.

## What we're adding

1. **Named, persistent groups.** Groups are created, renamed, nested,
   merged, split and deleted. A group outlives its members' comings and
   goings, including all of them dying.
2. **Muster and dismiss.**
   - Muster calls a group (and every group under it) to a tile.
   - Each member finishes its current job first.
   - The group forms up across as many tiles as it needs.
   - Dismiss sends everyone back to exactly what they were doing.
3. **Formation march.** A mustered group walks as one body: one step at
   a time, at its slowest member's pace, members in their places.
4. **Route groups.** A haul route's crew is a group whose standing
   assignment is the route.
5. **Boats.** A group boards, crosses and lands together, or is refused
   if it doesn't fit.
6. **Stance and doctrine.** The player sets them per group.
   - **Stance** decides whether the group starts fights and how far it
     chases.
   - **Doctrine** is the battlefield behaviour its members use.

## The gap (why now)

- **The M5 group is a shared order, not a body.**
  - `MoveGroupIntent` gives each member its own `Walk.Begin`
    (`Groups/MoveGroupIntent.cs`).
  - The group is Moving until the last walk ends
    (`Walk.NoteWalkEnded`).
  - Nothing keeps formation or sets a pace. `Group.cs`'s header still
    says "at the pace of the slowest member", which stopped being true
    at M43.
- **A grouped unit is out of the economy.** Eighteen sites in
  Sim.Core gate on `GroupId`:
  - `WorkAssignment` (×2), `BuildIntent`, `Construction`, `Claim.cs`
  - `HaulIntent`, `Load/UnloadCargoIntent`, `RouteCrewIntents`
  - `TrainingRules`, `EquipRules`, `CacheLooting`
  - `DispatchScoutIntent`, `BeginBreedingIntent`, `EmbarkGoal`
  - `EngageUnitIntent`, `Retask`
  - `CombatRules.ResumeInterrupted`: grouped units don't resume after a
    fight.

  The only way back is `DisbandGroupIntent`, which deletes the group.
- **A group can't be bigger than a tile.**
  - `Walk.PickGoal` returns null once the destination tile is at its
    per-side cap (16 on open ground).
  - In `MoveGroupIntent`, members past the cap never start walking, and
    the group counts as arrived without them.
  - In `FormGroupIntent`, they're taken off `PendingArrivals` but stay
    members, so a "formed" group can have members on the far side of
    the map.
- **Group ids are reused.**
  - `FormGroupIntent.NextGroupId` hands out the highest existing id
    plus 1, so deleting the newest group brings its id back.
  - The comment beside it says ids never reuse, because a stale fence
    could hit the new group.
  - Harmless while groups were short-lived; a real bug once they persist
    and get deleted.
- **A walking member who dies is never subtracted from `PendingArrivals`.**
  - `CombatRules.OnUnitDeath` removes the unit from `Group.Members` but
    does not touch the group's arrival count, so a Forming or Moving
    group whose straggler dies on the road may never count as arrived.
  - Found while planning auto-restaffing (2026-10-02); unverified in
    play. Needs a test: form a group, kill a walking member, assert the
    group still reaches Idle.
- **Route crews are a second body system.**
  - `RouteCrew.Members` is its own roster.
  - The driver sends N `MoveIntent`s per leg and waits at the stop for
    stragglers (`HaulingDriver.RunCrew`).
  - Formation, pace, boats and stance would all have to be built twice.
- **A battle ends a group's march.** `Battlefields.HaltGroup` stops the
  group as a body and it never resumes. `docs/battlefield-grid.md` §8
  asked for resuming; the M41 build deferred it.

## Locked decisions

Made 2026-10-02 with the user. Reversing any of these needs a written
addendum in `docs/groups-first-class.md`.

1. **Groups persist and have names** (at most 32 characters, like
   routes). A group whose members all die is kept, empty. Only
   `DeleteGroupIntent` removes a group.
2. **Group ids come from a stored counter** (`GameWorld.NextGroupId`)
   and are never reused.
3. **Dismiss is not delete.** Dismiss ends the group's command, and its
   members go back to their saved tasks (rule 7). Delete dismisses
   first, then removes the record.
4. **One owner per body.** A unit belongs to at most one *leaf* group.
   - A group holds units **or** groups, never both.
   - The tree has no cycles and is at most **3 levels** deep (army →
     regiment → company).
   - There is no cap on members.
5. **Any foot unit may be a member.** No boats, no bandits. Royals may
   join; the king's aura marches with the army.
6. **Under command blocks solo work, membership does not.**
   - A unit is *under command* when its leaf group, or any group above
     it, is Mustering or Formed, or its leaf group is running a route.
   - The eighteen `GroupId` gates become one predicate:
     `Groups.UnderCommand(world, unit)`.
   - A member of a dismissed group with no route works, hauls, trains
     and builds like anyone else.
7. **A mustered unit remembers its task and goes back to it on
   dismiss.** What it remembers, and what "finish first" means for each
   kind of task, is in the tables under *Detailed rules*.
8. **Work slots are held, not released.**
   - A worker or builder called away keeps its slot.
   - The slot counts against `WorkerCap` and produces nothing.
   - The building shows it as held.
   - Giving the slot to someone else cancels the absent unit's saved
     task. The player's choice wins.
9. **Muster overflow forms up across tiles.** The anchor tile is where
   the formation centres, not the only tile it uses (see *Formation
   layout*).
10. **A mustered group marches in formation.**
    - The group owns the march: one step anchor per group, at the
      slowest member's step cost.
    - One default formation: four abreast on the march, closing into a
      block at the halt.
11. **A route crew is a group on a route.** `RouteCrew` keeps
    `CrewId`, `CurrentStop` and `LastServe`; `Members` becomes
    `GroupId`. Route groups never take haul queue jobs (they never
    did).
12. **Mustering a route group:**
    - It finishes the leg it's on: walks to the stop it's heading for
      and serves it.
    - Then it musters.
    - On dismiss it goes back to the route at the next stop,
      automatically.
13. **Boats: all or nothing.** A group boards only if the boat has a
    seat for every member. Otherwise it is refused, with "needs N seats,
    boat has M".
14. **Stance and doctrine are set per group, by hand.**
    - Stance is posture (engage radius, leash, pursue or hold).
    - Doctrine is `DoctrineCatalog` behaviour per role, plus the
      withdraw threshold.
    - No target priority and no focus fire (see the decision doc).
15. **No progression gate.** Groups exist from the first minute.
16. **The AI stays on its own mustering code** this milestone.

Defaults accepted with the user:
- A member who is fighting when the muster comes counts as busy and
  joins when the fight ends.
- When a parent and a child are mustered to different tiles, the latest
  order wins (a unit is in one leaf group).
- An order given to a group that is still mustering is accepted. The
  members who have arrived march, and the rest head for the group's
  current destination and fall in.

## Detailed rules

### The group's state

```
Group
  Id, OwnerId, Name
  ParentId?            -- null = top level
  Members              -- unit ids (leaf) — or —
  Children             -- group ids (parent); never both
  Assignment           -- None | Route(routeId, crewId)
  Command              -- None | Mustering(anchor) | Formed(anchor, march?)
  Stance               -- Passive | Defensive | Aggressive, EngageRadius, LeashRadius
  Doctrine             -- per role: DoctrineBehaviour + WithdrawBelow (null = role default)
  MovementEpoch        -- fence, as today
  NextStepTick/Seq     -- the march's anchor (§2.8), null when not marching
```

`GroupState` (Forming / Idle / Moving) is replaced by Assignment and
Command. The enum stays in the code for old snapshots' sake (append-only
rule), but nothing new writes it.

### Intents

| Intent | Does | Notes |
|---|---|---|
| `CreateGroupIntent(name, unitIds, parentId?)` | New leaf group, **dismissed** | Members are not moved. Refuses boats, units already in a leaf group, and units not yours. All or nothing. |
| `RenameGroupIntent(group, name)` | | |
| `SetGroupParentIntent(group, parentId?)` | Nest or un-nest | Refuses cycles, a depth over 3, and a parent that holds units. |
| `AddToGroupIntent(group, unitIds)` | Join a leaf group | A unit joining a group under command is called to it (rule 7 applies). |
| `SplitGroupIntent(group, unitIds, newName?)` | Members out to a new leaf group, or solo when `newName` is null | The new group copies stance and doctrine and is dismissed. A split-out unit that was mustered goes back to its saved task. |
| `MergeGroupsIntent(from, into)` | `from`'s members or children move into `into`; `from` is deleted | Both must be the same kind (leaf or parent). Merged members take `into`'s command. |
| `MusterGroupIntent(group, anchor)` | Command = Mustering, for the group and every group under it | See *Muster*. |
| `MoveGroupIntent(group, dest)` | March in formation | Valid when Mustering or Formed. Replaces the M5 intent of the same name. |
| `DismissGroupIntent(group)` | Command = None, for the group and every group under it | See *Dismiss*. |
| `DeleteGroupIntent(group)` | Dismiss, then remove | A parent's children become top level. Route groups: the crew is removed from the route. |
| `SetGroupStanceIntent(group, stance, engage, leash)` | | Children use it unless they set their own. |
| `SetGroupDoctrineIntent(group, role, behaviour, withdrawBelow)` | | Uses `DoctrineCatalog.Blocker`. |
| `AssignGroupToRouteIntent(group, route, startStop)` / `UnassignGroupFromRouteIntent` | Standing assignment | Replaces Add/RemoveRouteCrew (see *Route groups*). |
| `EmbarkGroupIntent(group, boat)` / `DisembarkGroupIntent(group)` | | See *Boats*. |

**Kept for old intent logs:** `FormGroupIntent` = Create + Muster;
`DisbandGroupIntent` = Delete; `Add/RemoveRouteCrewIntent` = Create
(named "<route> crew N") + Assign / Delete. Every new intent checks
ownership (`docs/intent-authorization.md`).

### Muster

A muster goes to every leaf under the group. For each member:

| What the member is doing | On muster | Saved task |
|---|---|---|
| Idle, or in the haul queue's pool with nothing aboard | Comes now | None (rejoins the pool on dismiss) |
| Pool haul trip (`HaulPlan`) | Finishes the trip: picks up if it hasn't, delivers, **then** comes | None |
| Carrying cargo with no job | Comes. Cargo stays aboard. | None |
| Working at an extractor | Comes now. **Slot held.** | `WorkSlot(tile)` |
| Building at a site | Comes now. **Slot held.** | `BuildSlot(tile)` |
| Walking to a job (`Goal`: worker, builder, train, equip, loot, embark, idol) | Goal ends; comes now | The goal's kind and target, re-issued on dismiss |
| Breeding, training, surveying (`Survey`), on a scout mission | Finishes, **then** comes | None (the finished task has nothing to return to) |
| Fighting (on a board) | Finishes the fight, **then** comes | Whatever it had before the fight |
| Aboard a boat, not as part of its group | Comes when it disembarks | Kept |
| A route group's leg | The **group** serves the stop it's heading for, **then** comes | The group's `Assignment` (rule 12) |

A unit that comes walks to its place in the formation (see *Formation
layout*). The group is **Formed** when every living member is in place.
Members still finishing jobs keep it Mustering. **Muster progress** is
sim state, readable from the group: here / on the way / finishing a job
(and which) / in combat / aboard.

**Nested muster.** Every leaf under the mustered group gets the same
anchor. Their blocks are laid out together: children in their order, a
block per leaf (see *Formation layout*). If a child is later mustered
somewhere else, that order wins for that child's subtree.

### Dismiss

For each leaf under the group, the command ends:
- **The leaf has a route assignment:** the group goes back to its route
  at the stop it saved (rule 12).
- **Otherwise:** each member's saved task is re-issued through the
  ordinary goal path (`GoalRules`):
  - A held slot becomes an assign-worker or assign-builder goal for the
    same tile; the slot is waiting for it.
  - A saved goal is re-issued.
  - No saved task: the unit is free where it stands.
- **A saved task that can no longer happen** (building gone, slot given
  away, route deleted): the unit is free. The group reports "N returned
  with nothing to do", following the M30 visibility contract.

### Formation layout (pure)

`FormationLayout.Places(world, owner, anchor, heading, members[]) →
place per member`:
- The block is centred on the anchor. Members fill it in a fixed order
  (role, then id), so the same group always forms up the same way.
- A place is a subtile the member can stand on (`SubtileStepRules`) with
  room for its side (`TileCapacity`). Places that aren't fit are
  skipped.
- **Overflow:** when the anchor tile is full, the block grows outward
  ring by ring onto neighbouring tiles. Nested leaves get blocks side by
  side.
- Fog-fair: planned as the owner sees the world, like `Walk.Begin`.
- A member that can't reach any place is reported as "can't reach" in
  muster progress. **It is never silently dropped.**
- **Pure read:** no writes, pinned with the 100× test.

### Formation march

- **The group plans the route once:**
  - The anchor's route, as the owner sees the world.
  - The heading follows the route.
  - Each member's place is its offset in the block, turned to the
    heading.
- **One step event per group** (`GroupStepEvent`, fenced by
  `MovementEpoch`, anchor `NextStepTick/Seq`):
  - Moves the anchor one step and every member to its place.
  - Waits the **slowest member's** step cost, so carts and the wounded
    set the pace.
- **Narrow ground:** where a member's place can't be stood on (a bridge
  lane, a gate, a ford), the column narrows. The member falls in behind
  the member ahead, and takes its own place back once the ground
  allows.
- Members' own `SubtileRoute`s are empty while the group marches. The
  group is the walker. Passing friends, the tile cap on stopping, and
  `TileEntry` (vision, scouting, roads, camps) work per member as today.
- **Arrival:**
  - The group closes into the block around the destination (*Formation
    layout*, so an 80-unit group stopping on a 16-cap tile spills).
  - It is then Formed at the destination.

### Battles

- A member drawn onto a board fights as an individual (`battlefield-grid.md`
  §8). Its doctrine comes from:
  1. the unit's own `Doctrine`, else
  2. the group's doctrine for its role, else
  3. `BattleDoctrine.DefaultFor(role)`.
- The march **pauses** while any member is on a board (its step anchor
  is cleared). It does not halt for good.
- **When the battle closes,** the group re-forms and carries on to its
  destination. This replaces M41's "halt as a body, do not resume"
  (`docs/battlefield-grid.md` gets an addendum). Members who withdrew
  rejoin at their place.
- **Stance:**

  | Stance | On the march |
  |---|---|
  | Passive | Never starts a fight. Doctrine defaults to Withdraw unless set. |
  | Defensive | Never starts a fight. Fights when a battle opens on it. Resumes after. |
  | Aggressive | Engages a **visible** hostile within `EngageRadius` of the group's anchor, and chases within `LeashRadius` of where the chase began. Then resumes. |

  - Aggressive reuses the patrol shape (`docs/patrols.md`): the driver
    spots hostiles fog-fairly and submits an engage, and the sim does
    the pursuit.
  - The leash is anchored to the group's march, so baiting is still a
    real enemy tactic.

### Route groups

- `HaulRoute.Crews[i]` becomes `{ CrewId, GroupId, CurrentStop,
  LastServe }`.
- The driver marches the **group** to the next stop with one
  `MoveGroupIntent` instead of N `MoveIntent`s. It serves the stop when
  the group is Formed there.
- "A straggler is waited for at the stop" becomes "the group arrives
  together".
- `RouteCrews.Carries` (military members are escort) is unchanged.
- A route group's members are under command (rule 6), so they never
  enter the pool. This matches today's rule.
- **Muster a route group:** finish the leg, serve, muster. **Dismiss:**
  resume at `CurrentStop + 1`.

### Boats

`EmbarkGroupIntent(group, boat)`:
- **Refused** when the group has more living members than the boat has
  free seats.
- **Otherwise:** the group marches to the dock and boards; each member
  boards the moment both it and the hull are there (`EmbarkGoal`).
  - The group is Aboard while any member is aboard.
  - Moving the boat moves them.
- `DisembarkGroupIntent` lands every member at the dock tile and forms
  the group up (*Formation layout*, so a full boat landing on a
  16-capacity tile spills onto its neighbours).
- **The boat dies:** members drown as today and leave the roster. The
  group remains (rule 1).

### What the player sees

The wire carries, for the owner only (an enemy's order of battle stays
private):
- Each group's id, name, parent, members or children, assignment,
  command, stance and doctrine.
- Muster progress, broken down as above.
- "Returned with nothing to do" notices.
- Each unit's saved task (what it will go back to).
- Each building's held slots.

The debug client (`client/main.py`) gets:
- create, rename, nest, muster, move, dismiss, delete, merge and split
- stance and doctrine
- embark and disembark
- route assignment

The production client is out of scope: its UI is a separate build, cut
from the wire.

## Determinism and persistence

- **FormatVersion 48.**
  - Group: name, parent, children, assignment, command, stance,
    doctrine, step anchor.
  - World: `NextGroupId`.
  - Unit: saved task.
  - Extractor and site: held slots.
  - Route crew: `GroupId` in place of `Members`.
- **One new anchor:** the group step (`NextStepTick/Seq`), with a case
  in `RegenerateQueue.From`.
- **No paths recomputed on restore.** The group's committed route is
  stored, the same as `Unit.SubtileRoute` (architecture §4 rule 7).
- **Mutation points** (added to `docs/determinism-audit.md`):
  - A unit's saved task is written only by the muster and cleared only
    by dismiss, split and slot reassignment.
  - Held slots are written only by the muster and cleared only by
    dismiss or reassignment.
- **Pure reads:** `FormationLayout.Places`, `Groups.UnderCommand` and
  muster progress.

## Headline test

**A muster is seamless and deterministic.** Start a kingdom with:
- workers on two farms
- a pool hauler halfway through a trip with cargo
- a builder on a site
- a route group halfway through a leg
- an army group holding two companies, 24 soldiers between them

Then:
1. Muster the army to a tile with room for 16.
2. Check that everyone finishes per the rules and forms up across
   neighbouring tiles, with nobody left behind.
3. March it, and check it moves in formation at the slowest member's
   pace.
4. Dismiss it.
5. Check that every unit is back at its exact former task: same
   slots, same route stop, the hauler back in the pool.

Must hold for all of it:
- A twin run gives the same `Snapshot.Hash`.
- Snapshot and restore mid-muster and mid-march, then finish: same hash
  as the uninterrupted run.

## Phases (sketch; the plan file pins them)

This is too big for one milestone of 4–7 phases. Proposed split:

**M46: groups as bodies**

| Phase | Work | Done when |
|---|---|---|
| A | Persistent record: id counter, name, tree, kept when empty; Create/Rename/SetParent/Add/Delete; the `UnderCommand` predicate replaces the eighteen gates; old intents kept for old logs; v48 | A dismissed member can work; ids never reuse; round-trip |
| B | `FormationLayout`: block, overflow across tiles, fog-fair, pure | 80 units on a 16-cap tile spill in a fixed order; 100× pure-read |
| C | Formation march: the group step event, slowest pace, column narrowing, closing at arrival, battle pause and resume | Twin-run and mid-march restore; a cart sets the pace; a bridge narrows the column |
| D | Muster and dismiss with saved tasks, held slots, muster progress, nested muster | The headline test, minus the route group |
| E | Merge and split | Merge into a mustered group; split a mustered unit back to its task |
| F | Wire and debug client | Pure-read view pins; manual playthrough |

**M47: groups at work and at war**

| Phase | Work |
|---|---|
| A | Route groups (`RouteCrew.GroupId`; driver marches groups; route muster and resume) |
| B | Boats (embark and disembark as a group, all or nothing, landing spill) |
| C | Stance and per-role doctrine (Passive/Defensive/Aggressive; driver aggro using the patrol shape) |
| D | Wire and debug client for the above; the full headline test |

## Out of scope

- The AI using groups (decision 16).
- Formation shapes beyond the default.
- Player-written doctrine rules (the M18 atom route, decision doc
  *Future expansion*).
- Bigger boat classes.
- The production client's UI.

## Risks for the planner

1. **The eighteen gates:** each needs re-reading, not a mechanical swap.
   Some guard a *body* concern (embarked, breeding) that has nothing to
   do with groups.
2. **Held slots touch production:** `Extractor.Workers.Count` is read
   by `ProductionTickEvent`, the `WorkersBelow` predicates and the cap
   check (17 readers). A held slot must count for the cap and not for
   production. Pin each reader.
3. **Column narrowing** is the least specified rule here. Pin it with
   tests on a bridge, a gate and a one-subtile ford before writing the
   general case.
4. **A group step that moves N units in one event** must still run
   `TileEntry` per member, in id order, and the tile cap's "standing,
   not passing" rule must hold mid-march.
5. **Death mid-muster:** the `PendingArrivals` note above. Every path
   that removes a member (death, split, dismiss) must settle the arrival
   count, or the muster progress readout lies.
6. **Old intent logs:** `FormGroupIntent`, `DisbandGroupIntent` and
   `Add/RemoveRouteCrewIntent` must replay to the same world under the
   new meaning, or be pinned as a format break under
   `docs/persistence-model.md`.
