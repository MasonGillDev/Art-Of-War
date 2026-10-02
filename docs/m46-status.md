# M46 status: groups as bodies

Spec: `docs/m46-groups-spec.md`. Decision: `docs/groups-first-class.md`.

| Phase | Work | Status |
|---|---|---|
| A | Persistent record, id counter, tree, `UnderCommand`, v48 | Built |
| B | `FormationLayout`: block, overflow across tiles | Built |
| C | Formation march (the group walks) | Built |
| D | Muster and dismiss with saved tasks and held slots | Built |
| E | Merge and split | Built |
| F | Wire (server side only) | Built |

## Phase A: what was built

- **The record.**
  - Fields: `Group.Name`, `Kind` (units / groups), `ParentId`, `Children`.
  - `GroupState.Dismissed` (4).
  - `GameWorld.NextGroupId`, used through `GroupRules.NewId`.
  - Snapshot v48.
- **Intents** (`Groups/GroupRecordIntents.cs`): `CreateGroupIntent`, `RenameGroupIntent`,
  `SetGroupParentIntent`, `AddToGroupIntent`, `DeleteGroupIntent`. All are registered in `IntentJson`.
- **`GroupRules`**: the shared rules. Covers who may join, the name rule, the id counter, the tree
  (depth, height, refusing cycles and over-deep nesting), removal, and the pending count.
- **Gates.** All 18 sites in the spec's list now ask `GroupRules.UnderCommand`, where they used to
  ask `GroupId is not null`. The list always had 18; its first count said 17.
  - `EquipRules.Blocker` and `CacheLooting.Blocker` gained a world parameter.
  - `EquipRules.Grant` (the sandbox's pre-tick-0 gear) keeps a body-only check.
- **Bug fixed: the pending-arrival leak** (`GroupPendingArrivalTests`; the spec's "The gap").
  - A member dying mid-walk, or a halted `Forming` member, never came off `PendingArrivals`, so the
    group never arrived.
  - All walk endings now go through `GroupRules.OneLessPending`.
  - A `Forming` member's walk now counts wherever it ends, not only on the rendezvous tile: a walk
    aimed at water ends on the nearest land.

### Calls made while building

- **`GroupState` was extended, not replaced.** The spec has Assignment and Command replace
  `GroupState`. Phase A only needed "dismissed or not", so it adds `Dismissed` to the existing enum.
  The muster (D) and route groups (M47) decide whether Command becomes its own field.
- **The route-crew gate stays on membership.** A member of any group can't join a crew, because M47
  makes the crew a group of its own and a unit is in one group at most. The other side is
  `GroupRules.JoinRefusal`, which refuses a unit already crewing a route.
- **`AddToGroupIntent` refuses a group under command** until Phase D. Joining a mustered group means
  being called to it, and that is the muster's job.
- **`MoveGroupIntent` refuses a dismissed group and a group of groups.** Both point the player at
  the muster (Phase D). A dismissed group can't be moved until then; only `FormGroupIntent` groups
  march.
- **`FormGroupIntent` and `DisbandGroupIntent` are kept** so old intent logs replay. Disband now
  means Delete: it goes through `GroupRules.Remove`, which also lifts children to the top level.
- **The test that pinned "a group at zero members is deleted"** (`CombatDeathTests`) now pins the
  reverse, as the user decided: an empty group is kept.

## Phase B: what was built

- `FormationLayout.Places(world, owner, anchor, members, visible)` is a pure read.
  - It fills subtiles outward from the anchor tile's centre (doubled-integer distance, ties by row
    then column), so the anchor tile fills before any neighbour.
  - It uses only tiles that a foot unit can reach from the anchor by crossing tile edges
    (`SubtileStepRules.Problem` along the shared edge), out to `MaxRadius` = 8 tiles. A lake or a
    wall line bounds the block.
  - A subtile where a non-member friend or neutral stands is taken. A member's own subtile and an
    enemy's are not (an enemy is a fight, not a block).
  - A member with no place gets `null`. The caller reports it; nobody is dropped silently.
- `FormationLayout.FillOrder` sorts members by role, then id.

## Phase C: what was built (the design, as planned before building)

**Two stages.** The column marches, then it closes into the block.

1. **The column.** The group plans one **lead path** `P`, as the owner sees the world.
   - It starts at the anchor's centre-most place and ends at the destination tile's centre-most
     standable subtile.
   - Member `k` in fill order has rank `k / 4` and file `k % 4`. Its slot at lead step `t` is the
     trail point `P[t − rank]`, shifted sideways by `file − 1` (so −1…+2) at right angles to the
     path's heading there.
   - Before the path starts, the trail runs straight back from `P[0]`.
   - Where the shifted slot can't be stood on, the slot is the trail point itself: the column
     narrows, and friends pass through each other while walking.
2. **One group step event** (`GroupStepEvent`, anchored on the group with `NextStepTick/Seq`,
   fenced by `MovementEpoch`):
   - Each member takes one step toward its slot (a short local search), in id order, through the
     same step effects a solo walk uses: road wear, `TileEntry`, the combat trigger.
   - The lead advances only when every member is on its slot. That is the pace: it waits for the
     slowest member, and the outer files wheel at a turn.
   - The next step is scheduled at the slowest member's step cost.
   - A member that makes no progress for several steps is released to walk on alone (a straggler),
     so the column can never deadlock.
3. **Closing.** When the lead reaches the end of `P`, each member walks its ordinary solo walk to
   its `FormationLayout` place around the destination. `PendingArrivals` counts them, and the group
   is Idle when the last one stops (the Phase A machinery).
4. **Battles.**
   - A member enrolled on a board pauses the column: the epoch bump fences the step.
   - When the last board holding a member closes, the group plans a new `P` from the lead's
     position and carries on.
   - This replaces `HaltGroup`'s halt for good.
5. **Persistence.** The group stores `P`, the lead index, the stage and the step anchor. Nothing is
   recomputed on restore.

### Phase C: calls made while building

- **The step moves first and waits after.** A solo walk waits the step's cost, then moves. The column
  moves its members, then waits the slowest step just taken before its next event. The pace is the
  same; only the column's first step is immediate.
- **Members are marked as walking while in the column**: a one-step marker route and no anchor of
  their own (see `docs/determinism-audit.md`). They take no room on the tile cap's "standing" count,
  and friends pass through them.
- **A rank behind the start waits where it stands.** The column unspools from the block it formed up
  in, rank by rank, and the lead waits for each rank to reach its slot. Starting a big group is slower
  than marching it.
- **The lead checks the ground truth before each step.** A step the plan didn't see coming (a wall in
  the fog) makes the column plan again from the lead's position, with what the owner can see now. No
  path at all ends the march there: the group goes Idle at the lead's tile.
- **`HaltGroup` is gone.** A member enrolled on a board pauses the column (`OnEnrolled`). When a board
  closes or a unit leaves one, `WakeColumns` restarts every paused column that has no member left on a
  board. `MovementRulesTests.AMovingGroup_PausesForAFight_ThenMarchesOn` replaces the old
  "halts as a body" test. Both sides there are given Advance, because two Hold doctrines never fight.
- **A column narrows to single file** wherever a side-step slot can't be stood on. Ranks are one
  subtile apart, so a straight trail holds at most 16 members per tile. Where the trail turns inside a
  tile, more can stand on it for a moment.
- **An unseated member is not marched.** A unit standing on an over-full tile has no subtile. The game
  never creates one (spawns use `TileCapacity.RoomNear`), and the march leaves it out rather than guess
  where it is.

## Phase D: what was built

- **The intents.** `MusterGroupIntent(group, anchor)` and `DismissGroupIntent(group)`. Both cover the
  group and every group under it.
  - The anchor must be ground someone can stand on.
  - `FormGroupIntent` is now "create, then muster at the rendezvous".
- **The muster** (`GroupMuster.Muster`):
  - One block is laid out for every leaf under the group: leaves in tree order, members in fill
    order, so each company stands together.
  - Each member saves its task and leaves it, or finishes first:

    | What the member is doing | What happens |
    |---|---|
    | Working at an extractor | Leaves now. The slot is held (`Extractor.HeldBy`), and the saved task is an AssignWorker goal. |
    | Building a site | Leaves now. The saved task is an AssignBuilder goal. |
    | Walking to a job | The goal is saved and dissolved ("called to the muster"). |
    | A chase, an empty haul trip, a plain walk | Dropped. |
    | Delivering cargo, breeding, surveying, scouting, fighting, aboard a boat | Finishes first. `OnFreed` sends it on when it is done. |

  - Each leaf is Forming until `Awaiting` is empty.
- **Dismiss** (`GroupMuster.Dismiss`, `DismissLeaf`): each member's saved task is re-issued through
  `GoalRules.Begin`.
  - A worker's hold is released first; from then on its walking goal reserves the slot.
  - A target that is gone sends a `GoalDissolvedEvent` ("nothing to go back to").
  - `DeleteGroupIntent` dismisses a company before removing it.
- **Held slots.**
  - The staffing count (`PredicateEvaluator`) counts them.
  - An explicit `AssignWorkersIntent` that overfills the building evicts the newest hold. That cancels
    the absent worker's saved task, with a notice ("its place was given to another").
- **Joining a group under command** (`AddToGroupIntent`) calls the newcomer to the group's block
  (`GroupMuster.CallIn`). A marching group refuses newcomers until it stops.
- **Moving.**
  - `MoveGroupIntent` accepts a group that is still mustering: who has arrived marches.
  - A member finishing a job becomes a straggler. It follows to the destination once free, or to
    where the group stands if it has already arrived.
  - A group of groups moves as its companies, each its own column.
  - `Halt` leaves a member that is finishing a job alone.
- **Progress** (`GroupMuster.Progress`, a pure read): here, on the way, finishing (and what), no room.

### Phase D: calls made while building

- **A building job keeps no slot.** Construction sites have no builder cap, so there is nothing to
  hold. The saved task alone brings the builder back.
- **A chase is not a job.** A unit pursuing an enemy drops the chase and answers the muster. A unit
  already fighting on a board finishes the fight.
- **Two forming counts.** A Forming group tracks its members in `Awaiting`, a set, because a member
  finishing a job may answer long after the muster. A Moving group keeps the `PendingArrivals`
  count for its closing walks.
- **A muster walk that ends short tries again.** A member whose walk to its place ends anywhere
  else re-plans from where it stands. If there is no way, it is counted in where it is (no room in
  reach).
- **Delete dismisses a company first.** It doesn't dismiss the groups under a deleted parent: they
  move to the top level and keep their own orders.
- **Future intents are not in a snapshot.** The round-trip test submits the dismiss on both sides
  of the restore. This matches the persistence model: intents live in the intent log.

## Phase E: what was built

- **`MergeGroupsIntent(from, into)`.** Both groups must hold the same kind of thing. Neither may be
  marching. `from` is removed afterwards.
  - Units take `into`'s orders. Into a dismissed group, members that were under command go back to
    their saved tasks. Into a group under command, they are called to its block (`CallIn`).
  - Groups of groups move their children, subject to the same cycle and depth checks as
    `SetGroupParentIntent`.
- **`SplitGroupIntent(group, units, newName?)`.** Without a name the units go solo. With a name they
  form a new group under the same parent, starting dismissed. A group under command lets them go
  (`GroupMuster.Release`): the muster stops waiting for them, they stop walking to the block, and they
  go back to their saved tasks. A marching group splits once it has stopped.
- `GroupRules.Discard` removes an emptied record without dismissing anyone (used by merge).

## Phase F: what was built (server side only)

The user asked that neither client be touched. `client/` is git-ignored and stale: it posts an
intent shape the server no longer accepts. The production client is a separate project. Everything
here is additive on the wire.

- **`ViewDto.Groups`** (owner only), one `GroupDto` per group:
  - id, name, kind, parent, children, members, state
  - where the group is, and where it is marching
  - the muster's progress: here, on the way, no room, and who is finishing a job and what job
  - `ViewV2Dto` inherits it.
- **`UnitDto.SavedTaskKind`, `SavedTaskX`, `SavedTaskY`** (own units only). Filled on both projection
  paths (`ToUnitDto` from a `Unit`, and from a fogged `UnitView`).
- **`StructDto.HeldSlots`** (own extractors, filled in `EnrichOwned`).
- Pinned by `GroupWireTests`: own groups on the wire, other players' not, and the projection is a
  pure read.

## Not built yet (M47)

Route groups, boats, stance and per-role doctrine (`docs/m46-groups-spec.md`, "Phases").
