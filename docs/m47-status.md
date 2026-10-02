# M47 status: route groups

Spec: `docs/m47-route-groups-spec.md`. Built 2026-10-02 in one phase.

## What was built

- **A route crew is a group** (`RouteCrew.GroupId`). `Unit.RouteId` is retired: a unit's route is its
  group's (`RouteCrews.RouteOf`).
- **New intents.** `AssignGroupToRouteIntent(group, route, startStop)` and
  `UnassignGroupFromRouteIntent(group)`.
  - The old `AddRouteCrewIntent` makes a group named "<route name> crew <n>" and assigns it.
  - `RemoveRouteCrewIntent` and `ClearHaulRouteIntent` take crews off and keep their groups, dismissed.
- **Putting a group on a route** releases its members from their jobs (nothing saved) and musters
  the group at its first stop (`GroupMuster.Muster(forRoute: true)`). It serves once everyone has
  arrived.
- **The driver** marches a crew with one `MoveGroupIntent(ForRoute)` per leg and serves once the group
  is Idle at the stop.
  - `RouteCrewState.CalledAway` (5) is new: the player has mustered or moved the crew.
- **The serve** counts the group's living members standing still on the stop or a tile beside it.
  It refuses a suspended crew, and it answers a pending muster after advancing the stop.
- **Muster** of a crew running its route sets `PendingMuster`: the crew finishes its leg, and the serve
  answers. **Dismiss** of a suspended crew resumes the route. A player's **move** suspends the route
  at once.
- **Merge** refuses route groups. **Delete** takes a crew off its route first.
- **Wire.** `HaulCrewDto.GroupId` is new. `Members` lists the group's living members. `UnitDto.HaulRouteId`
  is derived from the group.
- **Snapshot v51.**

## Calls made while building

- **Forming up at the first stop.** Putting a crew on a route originally set the group's position to
  its first member's tile. A crew whose first member stood on the first stop was then served before
  the others arrived (`HaulRouteTests.Crew_WaitsAtTheStop_ForAStraggler` caught it). Assignment now
  musters the crew at that stop.
- **Two tests pinned the old flow.** `HaulRouteTests.Serve_IsFencedOnTheStopTheDriverSaw` lets the crew
  form up before resolving its serves by hand. Tests that read `Unit.RouteId` read `RouteCrews.RouteOf`.
- **No straggler wait at the stop.** The group arrives together. A member still tied up (a fight)
  holds the serve (`MemberBusy`) until it is free.

## Tests

`RouteGroupTests` (8): the loop in formation; muster mid-leg (finish, serve, answer); dismiss resumes;
a player's move suspends; unassign keeps the group; delete takes the crew off; refusals; the old
intent's named group; twin run and mid-leg restore. All 16 `HaulRouteTests`, 12 `HaulRouteEditTests`
and the wire tests pass unchanged apart from the two above.
