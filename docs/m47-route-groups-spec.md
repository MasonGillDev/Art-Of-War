# M47 Spec: Route Groups

> Milestone spec (workflow §7 step 1). The decision is in
> `docs/groups-first-class.md` ("A haul route crew is a group whose standing
> assignment is a route"). M46 built groups as bodies (`docs/m46-status.md`).
> The user put route groups next on 2026-10-02. Boats, stance and doctrine
> follow in later milestones.

## What we're adding

A haul route's crew becomes **a group on a route**. There is one system for
units that move together, not two.

- The crew **marches in formation** between stops (`GroupMarch`). This
  replaces one `MoveIntent` per member per leg.
- The crew **arrives together**. A stop is served when the group has formed
  up there.
- **Mustering** a route group finishes the leg it is on (walk to the stop,
  serve it), then answers. **Dismissing** it sends it back to its route at the
  next stop.
- A crew can be named, nested under an army, merged and split, like any
  group.

## The gap

- `RouteCrew.Members` is its own roster.
- `Unit.RouteId` is a second "which body do I belong to" pointer beside
  `Unit.GroupId`.
- `GroupRules.JoinRefusal` and the crew intents keep the two apart by
  refusing each other's members.
- The driver walks each member separately and waits at the stop for
  stragglers (`HaulingDriver.RunCrew`).

## Locked decisions (from the M46 spec, user 2026-10-02)

1. **A crew is a group.** `RouteCrew` keeps `CrewId`, `CurrentStop` and
   `LastServe`. `Members` becomes `GroupId`.
2. **Route groups never take haul queue jobs** (unchanged). Military members
   are escort (unchanged).
3. **Muster finishes the leg:** walk to the stop it is heading for, serve it,
   then muster. **Dismiss resumes the route** at the next stop.

## Defaults taken for this milestone (reversible; listed for the user)

1. **`Unit.RouteId` is retired.** A unit's route is its group's. The wire's
   `UnitDto.HaulRouteId` is derived from the group.
2. **The link lives on the route** (`RouteCrew.GroupId`). A group finds its
   route through `RouteGroups.Find`, a scan over the routes, of which there
   are few.
3. **A route group is never Dismissed.** Its rest state is running the route,
   so its members are always under command.
   - A player order (muster, move) suspends the route (`Group.RouteSuspended`).
   - Dismiss lifts the suspension and the driver picks the route up again.
4. **A muster that arrives mid-leg waits on the group**
   (`Group.PendingMuster`). The serve applies it after advancing the crew's
   stop, so serving and answering can't come apart across a restart.
5. **A player's `MoveGroupIntent` on a route group suspends the route at
   once.** Only a muster finishes the leg first. The driver's own moves carry
   `ForRoute` and suspend nothing.
6. **Assigning a group to a route** (`AssignGroupToRouteIntent`):
   - releases its members from their jobs, with no saved task, because the
     route is now their daily task;
   - lifts any muster;
   - is refused for a group of groups, a group already on a route, or a
     group over `MaxMembersPerCrew`.
7. **Taking a group off its route** (`UnassignGroupFromRouteIntent`, and the
   old `RemoveRouteCrewIntent` / `ClearHaulRouteIntent`) leaves the group
   **Dismissed and kept**: the members are free where they stand, with their
   cargo. Deleting a route group takes its crew off the route first.
8. **The old intents are kept** so old logs replay.
   - `AddRouteCrewIntent` creates a group named "<route name> crew <n>" and
     assigns it.
   - `SetHaulRouteIntent`'s crew does the same.
9. **Merging a group on a route is refused** (take it off the route first).
   Splitting members out of a route group works as for any group, as long as
   it isn't marching.
10. **Serving counts the group's body.** The serve's carriers are the group's
    living members standing still, once the group is Idle at the stop. A crew
    whose block spills onto the next tile still serves as one.

## Persistence

FormatVersion 51:
- the crew row carries `GroupId` in place of members;
- the unit row loses `RouteId`;
- the group row gains `RouteSuspended` and `PendingMuster`.

## Headline test

A two-stop route crewed by a named group of four carriers and a soldier runs
the loop in formation. Then:
1. Muster the army above it mid-leg. The crew finishes the leg (the stop is
   served), then forms up at the muster.
2. Dismiss it. It walks back and serves the next stop.

A twin run and a mid-leg restore must end with the same hash.
