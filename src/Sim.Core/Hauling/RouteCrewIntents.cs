using Sim.Core.Groups;

namespace Sim.Core.Hauling;

// M47 — A ROUTE CREW IS A GROUP ON A ROUTE (docs/m47-route-groups-spec.md). The
// group marches in formation from stop to stop (the hauling driver orders it) and
// serves each stop together. Running the route is the group's daily task: a player's
// muster or move suspends it, and dismiss sends the group back to it.

// Put a group on a route, starting from `StartStop` (which is how a player staggers a
// second crew round the loop from the first). Its members leave whatever they were
// doing: the route is their task now. Any role may crew; every member carries at its
// own capacity except Soldiers and Archers, who are escort.
public sealed class AssignGroupToRouteIntent : Intent
{
    public int GroupId { get; }
    public int RouteId { get; }
    public int StartStop { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public AssignGroupToRouteIntent(int groupId, int routeId, int startStop = 0)
    {
        GroupId = groupId;
        RouteId = routeId;
        StartStop = startStop;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (RouteCrews.Refusal(world, route: null, RouteId, PlayerId, StartStop) is { } badRoute)
            return IntentOutcome.Reject(badRoute);
        if (group.Kind != GroupKind.Units)
            return IntentOutcome.Reject($"group {GroupId} holds groups, not units");
        if (RouteCrews.Find(world, group) is { } on)
            return IntentOutcome.Reject($"group {GroupId} already crews route {on.Route.RouteId}");
        var members = RouteCrews.LivingMembers(world, group);
        if (members.Count == 0)
            return IntentOutcome.Reject($"group {GroupId} has no one to crew a route");
        if (members.Count > HaulingConstants.MaxMembersPerCrew)
            return IntentOutcome.Reject($"crew of {members.Count} exceeds cap {HaulingConstants.MaxMembersPerCrew}");
        foreach (var u in members)
        {
            if (Sim.Core.Automation.ClaimLedger.IsClaimed(world, u.Id))
                return IntentOutcome.Reject($"unit {u.Id} is claimed by an automation order");
            if (u.IsEmbarked || u.Traversal != Traversal.Foot)
                return IntentOutcome.Reject($"unit {u.Id} can't walk a land route");
        }

        RouteCrews.PutOn(sim, world.HaulRoutes[RouteId], group, StartStop);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"AssignGroupToRoute(group={GroupId}, route={RouteId}, start={StartStop})";
}

// Take a group off the route it crews. It is dismissed and kept: its members stop
// where they stand, keep whatever they carry, and are free (haulers rejoin the queue's
// pool once empty).
public sealed class UnassignGroupFromRouteIntent : Intent
{
    public int GroupId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public UnassignGroupFromRouteIntent(int groupId) { GroupId = groupId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (!RouteCrews.TakeOff(sim, group))
            return IntentOutcome.Reject($"group {GroupId} crews no route");
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"UnassignGroupFromRoute(group={GroupId})";
}

// M36 — crew a route with the named units. Since M47: a group of exactly those units,
// named after the route, put on it (kept so old intent logs replay, and as the one-step
// "these people work that loop"). The units must be free of other standing
// commitments: not in a group, not claimed by an automation order, not aboard a boat.
public sealed class AddRouteCrewIntent : Intent
{
    public int RouteId { get; }
    public List<int> Members { get; }
    public int StartStop { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public AddRouteCrewIntent(int routeId, List<int> members, int startStop = 0)
    {
        RouteId = routeId;
        Members = members ?? new();
        StartStop = startStop;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (RouteCrews.Refusal(world, route: null, RouteId, PlayerId, StartStop) is { } badRoute)
            return IntentOutcome.Reject(badRoute);
        var route = world.HaulRoutes[RouteId];

        var members = Members.Distinct().OrderBy(id => id).ToList();
        if (members.Count == 0)
            return IntentOutcome.Reject("a crew needs at least one member");
        if (members.Count > HaulingConstants.MaxMembersPerCrew)
            return IntentOutcome.Reject(
                $"crew of {members.Count} exceeds cap {HaulingConstants.MaxMembersPerCrew}");
        foreach (var id in members)
        {
            if (!world.Units.TryGetValue(id, out var u))
                return IntentOutcome.Reject($"unit {id} does not exist");
            if (u.OwnerId != PlayerId)
                return IntentOutcome.Reject($"unit {id} not owned by player {PlayerId}");
            if (u.GroupId is { } g)
                return IntentOutcome.Reject(RouteCrews.FindById(world, g) is { } on
                    ? $"unit {id} already crews route {on.Route.RouteId}"
                    : $"unit {id} is in a group");
            if (Sim.Core.Automation.ClaimLedger.IsClaimed(world, id))
                return IntentOutcome.Reject($"unit {id} is claimed by an automation order");
            if (u.IsEmbarked || u.Traversal != Traversal.Foot)
                return IntentOutcome.Reject($"unit {id} can't walk a land route");
        }

        var name = $"{(route.Name.Length > 0 ? route.Name : $"Route {RouteId}")} crew {route.NextCrewId}";
        if (name.Length > GroupConstants.MaxNameLength) name = name[..GroupConstants.MaxNameLength].TrimEnd();
        var group = new Group(GroupRules.NewId(world)) { OwnerId = PlayerId, Name = name, State = GroupState.Dismissed };
        foreach (var id in members)
        {
            group.Members.Add(id);
            world.Units[id].GroupId = group.Id;
        }
        world.Groups[group.Id] = group;
        RouteCrews.PutOn(sim, route, group, StartStop);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"AddRouteCrew(route={RouteId}, {Members.Count} members, start={StartStop})";
}

// Take a crew off its route: its group is dismissed and kept, the members stop where
// they stand and keep whatever they carry.
public sealed class RemoveRouteCrewIntent : Intent
{
    public int RouteId { get; }
    public int CrewId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public RemoveRouteCrewIntent(int routeId, int crewId)
    {
        RouteId = routeId;
        CrewId = crewId;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulRoutes.TryGetValue(RouteId, out var route))
            return IntentOutcome.Reject($"route {RouteId} does not exist");
        if (route.OwnerId != PlayerId)
            return IntentOutcome.Reject($"route {RouteId} not owned by player {PlayerId}");
        var crew = route.Crews.Find(c => c.CrewId == CrewId);
        if (crew is null)
            return IntentOutcome.Reject($"route {RouteId} has no crew {CrewId}");

        RouteCrews.Release(sim, route, crew);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"RemoveRouteCrew(route={RouteId}, crew={CrewId})";
}

// Delete a route and release every crew on it (their groups are dismissed and kept).
public sealed class ClearHaulRouteIntent : Intent
{
    public int RouteId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ClearHaulRouteIntent(int routeId) { RouteId = routeId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulRoutes.TryGetValue(RouteId, out var route))
            return IntentOutcome.Reject($"route {RouteId} does not exist");
        if (route.OwnerId != PlayerId)
            return IntentOutcome.Reject($"route {RouteId} not owned by player {PlayerId}");

        foreach (var crew in route.Crews.ToList()) RouteCrews.Release(sim, route, crew);
        world.HaulRoutes.Remove(RouteId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ClearHaulRoute(route={RouteId})";
}

public static class RouteCrews
{
    // Who loads and unloads at a stop. Military roles walk the loop as
    // escort; everyone else the player named is a carrier.
    public static bool Carries(Unit u) => u.Role is not (UnitRole.Soldier or UnitRole.Archer);

    // Why a crew can't go on route `routeId` at `startStop`, or null. Pure read.
    internal static string? Refusal(GameWorld world, HaulRoute? route, int routeId, int playerId, int startStop)
    {
        if (route is null && !world.HaulRoutes.TryGetValue(routeId, out route))
            return $"route {routeId} does not exist";
        if (route.OwnerId != playerId)
            return $"route {routeId} not owned by player {playerId}";
        if (route.Crews.Count >= HaulingConstants.MaxCrewsPerRoute)
            return $"route {routeId} already has {route.Crews.Count} crews";
        if (startStop < 0 || startStop >= route.Stops.Count)
            return $"start stop {startStop} is not on route {routeId}";
        return null;
    }

    // The route and crew a group runs, or null. A scan over the routes, of which there
    // are few (MaxRoutesPerPlayer each). Pure read.
    public static (HaulRoute Route, RouteCrew Crew)? Find(GameWorld world, Group group) => FindById(world, group.Id);

    public static (HaulRoute Route, RouteCrew Crew)? FindById(GameWorld world, int groupId)
    {
        foreach (var (_, route) in world.HaulRoutes)
            foreach (var crew in route.Crews)
                if (crew.GroupId == groupId) return (route, crew);
        return null;
    }

    // The route a unit crews (its group's), or null. Pure read.
    public static int? RouteOf(GameWorld world, Unit u) =>
        u.GroupId is { } gid && FindById(world, gid) is { } on ? on.Route.RouteId : null;

    // The crew's members still alive, ascending: its group's.
    public static List<Unit> Living(GameWorld world, HaulRoute route, RouteCrew crew)
    {
        if (!world.Groups.TryGetValue(crew.GroupId, out var group) || group.OwnerId != route.OwnerId)
            return new List<Unit>();
        return LivingMembers(world, group);
    }

    internal static List<Unit> LivingMembers(GameWorld world, Group group)
    {
        var alive = new List<Unit>(group.Members.Count);
        foreach (var id in group.Members)
            if (world.Units.TryGetValue(id, out var u)) alive.Add(u);
        return alive;
    }

    // Put `group` on `route` at `startStop`. Whatever the group was doing stops (a
    // march, a muster); its members leave their jobs — the route is their task now,
    // nothing is saved to go back to — and the crew forms up at its first stop (a
    // muster there: it serves once everyone has arrived).
    internal static void PutOn(Simulation sim, HaulRoute route, Group group, int startStop)
    {
        var world = sim.World;
        route.Crews.Add(new RouteCrew { CrewId = route.NextCrewId++, GroupId = group.Id, CurrentStop = startStop });
        group.RouteSuspended = false;
        group.PendingMuster = null;
        foreach (var u in LivingMembers(world, group))
        {
            GroupMuster.ReleaseHold(world, u);
            u.SavedTask = null;
            if (GroupMuster.Busy(world, u) is not null) continue;   // finishing a job: it comes after
            Sim.Core.Intents.Retask.Release(sim, u);
            Sim.Core.Movement.Walk.Stop(u);
        }
        GroupMuster.Muster(sim, group, route.Stops[startStop].Tile, forRoute: true);
    }

    // Take the crew off its route: its group is dismissed and kept, the members stop
    // where they stand with whatever they carry.
    internal static void Release(Simulation sim, HaulRoute route, RouteCrew crew)
    {
        route.Crews.Remove(crew);
        if (!sim.World.Groups.TryGetValue(crew.GroupId, out var group)) return;
        MoveGroupIntent.Halt(sim, group);
        group.BumpEpoch();
        group.State = GroupState.Dismissed;
        group.RouteSuspended = false;
        group.PendingMuster = null;
        group.MusterPlaces.Clear();
    }

    // The group's crew, if it has one, comes off its route. False when it crews none.
    internal static bool TakeOff(Simulation sim, Group group)
    {
        if (Find(sim.World, group) is not { } on) return false;
        Release(sim, on.Route, on.Crew);
        return true;
    }
}
