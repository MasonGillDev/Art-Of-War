using Sim.Core.Groups;

namespace Sim.Core.Hauling;

// M47 — A ROUTE CREW IS A GROUP ON A ROUTE (docs/m47-route-groups-spec.md). The
// group marches in formation from stop to stop (the hauling driver orders it) and
// serves each stop together. Running the route is the group's daily task: a player's
// muster or move suspends it, and dismiss sends the group back to it.
//
// A crew is made one way: CreateGroupIntent, then AssignGroupToRouteIntent. (The M36
// AddRouteCrewIntent / RemoveRouteCrewIntent were removed 2026-10-02; see
// docs/client-intent-migration.md.)

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
        group.MarchMode = MarchMode.SingleFile;   // M50: a caravan wants speed (the player may switch it back)
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
