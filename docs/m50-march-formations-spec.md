# M50 Spec: March Modes and Saved Formations

> Milestone spec. User, 2026-10-02. Builds on M46 Phase C (`GroupMarch`) and M49.
> Fixes the march stutter noted in `docs/m46-status.md` and the Brain note "group
> march feel".

## What we're adding

1. **Two march modes**, a setting on the group used for every march it makes until
   switched (`SetGroupMarchModeIntent`). Setting an army sets every company in it.
   - **Single file:** fast. Every member follows the lead's exact trail, one subtile
     behind the one in front. Nobody shuffles sideways. For caravans.
   - **Column:** formation. Members hold their places in the group's formation as it
     marches. That is the saved formation if there is one, otherwise the default:
     four abreast.
2. **Saved formations,** set up in the world:
   - **Arrange** (`ArrangeGroupMemberIntent`): while the group stands formed, walk one
     member to a chosen subtile near it.
   - **Save** (`SaveGroupFormationIntent`): record every member's position relative to a
     chosen **leader**, with a chosen **front** side. The leader walks the march path,
     and everyone else holds their offset, turned to face the way the group travels.
   - **Clear** (`ClearGroupFormationIntent`) goes back to the default.
3. **A muster forms up in the saved formation,** facing the front it was saved with.
4. **No stutter.** The formation holds on straight stretches, and the group moves at
   its slowest member's walking speed.

## Defaults (user, 2026-10-02)

- New groups march in **Column**.
- **Putting a group on a route switches it to Single file** (caravans want speed). The
  player may switch it back.
- **Switching mode mid-march** takes effect at once. The group re-forms on the move.
- **Members change:** a slot left empty is filled by role, and gaps close from the back.
  A newcomer without a slot falls in at the rear, four abreast. No re-saving needed.
- **The formation turns only when the route turns,** not on every stair step of the
  subtile path.

## Rules

### The march frame

- **Each row has its own anchor.** A row `b` subtiles behind the leader is anchored on
  the lead path at `P[t − b]`, so the formation follows the path's bends.
- **Facing.** A member `a` subtiles to the right of the leader stands at
  `anchor + a × right(facing)`.
- **Facing at a path index is the dominant direction** of the path over a 16-subtile
  window (4 tiles) leading to it. A tie falls back to the whole path's dominant
  direction.
  - A staircase on a diagonal therefore faces one way for its whole length.
  - An L-shaped route turns once.
  - It is a pure function of the stored path, so nothing new is stored.
- **Before the start.** A row whose anchor would fall before the start of the path is
  placed along the start's facing, extended backwards. If that ground can't be stood
  on, the row waits where it stands. A group mustered in formation facing its march
  sets off with no unspooling.
- **Narrow ground.** Where a member's spot can't be stood on, its spot is its row's
  trail point (`P[t − b]`). It returns to its spot once the ground allows.

### The pace (no stutter)

- **The leader advances unless a member has fallen behind:** more than 2 subtiles
  (Manhattan) from its spot. This replaces "everyone exactly on their slot".
- **The beat is the slowest step being taken.** Every member moves one step per beat,
  and the beat lasts as long as the slowest of those steps, so the slowest member sets
  the pace.

### Smooth on the client (user: "make sure it doesn't read as jittery")

- **The jitter's main source is the wire, not the pace.** The client animates a unit's
  step only from the step in flight it carries (`UnitDto.SubStepX/Y`,
  `SubStepArriveTick`, `SubStepTotalTicks`). A solo walker always has one. A column
  member never did: the group's event moved it, so it snapped from subtile to subtile.
- **The column now plans, announces, then lands, like a solo walk.**
  - Each group step first lands the steps planned at the last one.
  - Then it advances the leader (pace permitting).
  - Then it plans each member's next step. The member carries that step (its one-step
    route) until the next group step lands it, at `Group.NextStepTick`, after
    `Group.MarchStepTicks`.
  - The wire reports it as the member's step in flight, so the client glides every
    member across every beat.
- **The pace allows one step per member per beat** (no double steps: a glide can only
  cross one subtile). Lag closes when the leader waits, which the elastic gate makes
  rare.

### Single file

- Member `k` in the march order stands at `P[t − k]`. The leader is `k = 0`, and
  members before the start wait where they stand.
- The march order is: the saved formation's slots front to back, else the fill order
  (role, id).
- Same pace and catch-up rules. With no sideways spots, it moves at the slowest
  member's walking speed.

### Saved formations

- **Arrange.**
  - The group must be formed (Idle) and the unit a member that is not busy.
  - The target must be a subtile within 4 tiles of the group's position, standable,
    and not held by a standing friend.
  - The member walks there and the group stays formed.
- **Save.**
  - The group must be formed, every living member standing still and not busy, all
    within 8 tiles of the leader.
  - The leader is a member.
  - Offsets are stored in a front-is-north frame (`a` = right, `b` = behind), with
    each slot's unit id and role, plus the saved front.
- **Slot assignment** (pure, every time the shape is needed):
  1. A slot goes to its own unit if alive and a member.
  2. Then same-role members fill the remaining slots front to back.
  3. Then any member.
  4. Then gaps are closed from the back: the rearmost filled slot behind an empty one
     moves up.
  5. Unassigned members form rows four abreast behind the rearmost slot.

  The leader is whoever holds slot (0, 0), or the front-most filled slot when that's
  empty.
- **Muster** with a saved formation places each member at
  `anchor + rotate(offset, saved front)`, the leader on the anchor's centre subtile.
  A spot that can't be stood on or is taken goes to the nearest free place in the
  ordinary block layout.

## Persistence

FormatVersion 53. Each group row gains:
- `MarchMode` (byte);
- `MarchStepTicks` (long: the beat in flight, for the wire);
- its formation (has-flag, front byte, count, then per slot: unit id, role byte, `a`,
  `b`).

## Wire

`GroupDto.MarchMode`, `GroupDto.FormationFront` (-1 when there is none) and
`GroupDto.FormationSlots` (unit id, `a`, `b`).

## Headline tests

- An 8-member group in Column marches a straight 20 tiles:
  - it arrives within 1.25× one soldier's solo walking time;
  - at least 90% of the straight-stretch samples have every member within 1 subtile
    of its spot.
- Single file arrives within 1.2× the solo time. Its last member starts the line's
  length behind the leader, so that gap is the caravan's length, not stalling. Like the
  column, at least 95% of member-beats on the straight are steps.
- A diagonal route changes the formation's facing at most twice.
- A saved formation musters and marches in its shape.
- Twin run and mid-march restore give the same hash.

## Built (2026-10-02): calls made while building

- **Arranging onto a member's spot swaps the two.** Each walks to the other's spot,
  passing through. Without this, a crowded formation couldn't be rearranged one move at
  a time. A spot held by someone outside the group is still refused.
- **A Column march stops centred on the destination**, still facing the way it marched.
  The footprint is centred as it actually lies (after turning). "Move to that tile"
  leaves the group on that tile: the default 16 fill exactly one. A **muster** puts the
  leader on the anchor's centre, as specified.
- **One shaped company per muster.** In an army muster, the first company in tree order
  with a saved formation takes its shape at the anchor, and the others fill the block
  around it. Laying out whole armies is a later step.
- **Road wear.** A column walks parallel lanes and spreads its wear. A single-file caravan
  walks one trail and stacks it, so supply lines pave themselves.
  (`GroupMovementTests.GroupMove_WearsTheRoad_PerMember` now marches single file.)
