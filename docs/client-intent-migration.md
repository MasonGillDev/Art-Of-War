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
| `AssignGroupToRouteIntent` | `GroupId, RouteId, StartStop` | |
| `UnassignGroupFromRouteIntent` | `GroupId` | |

## What the client reads

- `ViewDto.Groups`: each `GroupDto` has id, name, kind, parent, children, members,
  state, stance, `Away`, position, destination, and muster progress (`Here`, `OnTheWay`,
  `NoRoom`, `Finishing`).
- `UnitDto`: `GroupId`, `GroupState`, `Busy` (why the unit is busy), `SavedTaskKind/X/Y`
  (the job it goes back to on dismiss), `HaulRouteId`.
- `StructDto.HeldSlots`: work slots held for workers away at a muster.
- `HaulCrewDto.GroupId`: the group that crews a route.
