# Client intent migration (2026-10-02)

**For the production client.** The server removed five group and route-crew orders that
duplicated newer ones. A client still sending them gets the envelope rejected with an
unknown type, so the feature behind it stops working until the client switches to the
replacement below.

Why they were removed: the game is in development with no saved games to replay, so the
old orders were only confusion (two ways to do one thing). See `docs/groups-first-class.md`
(Update 2026-10-02, "one way per order").

Intents are sent as `POST /intent` with `{ "TypeName": "...", "Payload": "<json>" }`. The
payload is the Sim.Core intent's properties in PascalCase, plus `PlayerId`.

## Removed, and what to send instead

| Removed `TypeName` | Send instead |
|---|---|
| `FormGroupIntent` `{ UnitIds, RendezvousTile }` | `CreateGroupIntent` `{ Name, UnitIds, ParentId?, HoldsGroups }`, then `MusterGroupIntent` `{ GroupId, Anchor }` |
| `DisbandGroupIntent` `{ GroupId }` | `DeleteGroupIntent` `{ GroupId }`. For "stand down but keep the group", use `DismissGroupIntent` `{ GroupId }` |
| `AddRouteCrewIntent` `{ RouteId, Members, StartStop }` | `CreateGroupIntent` `{ Name, UnitIds }`, then `AssignGroupToRouteIntent` `{ GroupId, RouteId, StartStop }` |
| `RemoveRouteCrewIntent` `{ RouteId, CrewId }` | `UnassignGroupFromRouteIntent` `{ GroupId }`. The crew's group is on `HaulCrewDto.GroupId` |
| `SetHaulRouteIntent`'s `Crew` field | `SetHaulRouteIntent` `{ Stops, Name }` with no crew, then `CreateGroupIntent` and `AssignGroupToRouteIntent` |

### The two-step create

`CreateGroupIntent` doesn't return the new group's id. Read it from the next view, as the
new entry in `ViewDto.Groups`, or predict it: group ids come from one world counter and
are never reused, so `GroupId` is one more than the highest the client has seen.

## The group orders, for reference

| Order | Payload | Notes |
|---|---|---|
| `CreateGroupIntent` | `Name, UnitIds, ParentId?, HoldsGroups` | Starts **dismissed**: members keep working. `HoldsGroups: true` makes an army (a group of groups) |
| `RenameGroupIntent` | `GroupId, Name` | ≤ 32 characters, trimmed |
| `SetGroupParentIntent` | `GroupId, ParentId?` | Nest under an army, or lift out (null) |
| `AddToGroupIntent` | `GroupId, UnitIds` | Joining a mustered group calls the newcomer to it |
| `MusterGroupIntent` | `GroupId, Anchor` | Calls the group and everything under it; members finish deliveries, fights and births first |
| `MoveGroupIntent` | `GroupId, Destination, ForRoute` | The group marches in formation. Clients send `ForRoute: false` |
| `DismissGroupIntent` | `GroupId` | Everyone goes back to the job they left; a route crew goes back to its route |
| `DeleteGroupIntent` | `GroupId` | Dismisses, then removes the group for good |
| `MergeGroupsIntent` | `FromId, IntoId` | |
| `SplitGroupIntent` | `GroupId, UnitIds, NewName?` | No name means the units go solo |
| `SetGroupStanceIntent` | `GroupId, Stance` | 1 Passive, 2 Defensive, 3 Aggressive |
| `ChargeGroupIntent` | `GroupId, TargetUnitId` | Aggressive groups; the server's stance driver also sends it |
| `SetGroupMarchModeIntent` | `GroupId, Mode` | M50: 1 single file, 2 column. Cascades to the groups under it |
| `ArrangeGroupMemberIntent` | `GroupId, UnitId, Tile, SubX, SubY` | M50: while formed, walk a member to a subtile (0..3) within 4 tiles; onto another member's spot, the two swap |
| `SaveGroupFormationIntent` | `GroupId, LeaderId, Front` | M50: save the shape as it stands. `Front` 0 N, 1 E, 2 S, 3 W |
| `ClearGroupFormationIntent` | `GroupId` | M50: back to the default (four abreast) |
| `AssignGroupToRouteIntent` | `GroupId, RouteId, StartStop` | Also switches the group to single file |
| `UnassignGroupFromRouteIntent` | `GroupId` | |

## What the client reads

- `ViewDto.Groups`: each `GroupDto` has id, name, kind, parent, children, members,
  state, stance, `Away`, position, destination, muster progress (`Here`, `OnTheWay`,
  `NoRoom`, `Finishing`), and (M50) `MarchMode`, `FormationFront` (-1 = default) and
  `FormationSlots` (unit id, `A` right, `B` behind the leader, front-is-north).
- A marching group member now carries its step in flight in `SubStepX/Y`,
  `SubStepArriveTick` and `SubStepTotalTicks`, like any walker, so the client glides it
  instead of snapping it (M50).
- `UnitDto`: `GroupId`, `GroupState`, `Busy` (why the unit is busy), `SavedTaskKind/X/Y`
  (the job it goes back to on dismiss), `HaulRouteId`.
- `StructDto.HeldSlots`: work slots held for workers away at a muster.
- `HaulCrewDto.GroupId`: the group that crews a route.

## Update 2026-10-06 — M51 ore tiers (`docs/m51-ore-tiers-spec.md`)

No intents were removed. Enums travel as numbers, so payloads keep their shape, but what some
numbers **mean** changed, and there are new ones.

### Resources: renamed and new

| Id | Was | Now |
|---|---|---|
| 3 | Ore | **Copper ore** |
| 5 | Sword | **Bronze sword** |
| 9 | Iron | **Bronze** (the bar) |
| 10 | — | Iron ore |
| 11 | — | Iron (the bar) |
| 12 | — | Iron sword |
| 13 | — | Steel ore |
| 14 | — | Steel (the bar) |
| 15 | — | Steel sword |

Relabel 3, 5 and 9, and add names and icons for 10–15.

### Structure kinds

| Id | Was | Now |
|---|---|---|
| 7 | Mine | **Copper mine** |
| 25 | — | Iron mine (costs bronze to build) |
| 26 | — | Steel mine (costs iron to build) |

`PlaceSiteIntent` refuses a mine on another ore's vein ("… holds IronOre").

### What the client reads

- **`VeinDto.Ore`:** the resource id of the vein's ore (3, 10 or 13). Show it, and offer only
  the matching mine.
- **The build menu's structure spec:**
  - a refiner's `Inputs` became **`Recipes`**: one `RecipeDto` per bar, each an `Output` and
    its `Inputs`, best first;
  - a refiner's `OutputResource` is now 0;
  - the smelter makes the best bar it holds ore for.
- **A smelter's `Holdings`** can list several bars at once.
- **Survey notices** name the ore ("iron ore vein found at …").

### Equipping

A soldier carries one sword. Equipping a **better** one swaps it in, and the old sword goes
back into the store it was equipped from. A sword of the same or a lower tier is refused
("already carries …").
