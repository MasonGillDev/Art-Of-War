namespace Sim.Core.Hauling;

// Put a crew on a route (docs/hauling-queue-and-routes.md). The members walk
// together and start from `StartStop`, which is how a player staggers a
// second crew round the loop from the first.
//
// Any role may crew. Every member carries at its own capacity except
// Soldiers and Archers, who are escort (M45: a crew is the units the player
// named; M36 carried with Haulers only). A member must be free of other standing commitments —
// not on another route, not claimed by an automation order, not in a group,
// not aboard a boat. Busy with a one-off task is fine: the crew waits for
// everyone to be free before it moves.
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
        if (!world.HaulRoutes.TryGetValue(RouteId, out var route))
            return IntentOutcome.Reject($"route {RouteId} does not exist");
        if (route.OwnerId != PlayerId)
            return IntentOutcome.Reject($"route {RouteId} not owned by player {PlayerId}");
        if (route.Crews.Count >= HaulingConstants.MaxCrewsPerRoute)
            return IntentOutcome.Reject($"route {RouteId} already has {route.Crews.Count} crews");
        if (StartStop < 0 || StartStop >= route.Stops.Count)
            return IntentOutcome.Reject($"start stop {StartStop} is not on route {RouteId}");

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
            if (u.RouteId is { } other)
                return IntentOutcome.Reject($"unit {id} already crews route {other}");
            if (Sim.Core.Automation.ClaimLedger.IsClaimed(world, id))
                return IntentOutcome.Reject($"unit {id} is claimed by an automation order");
            if (u.GroupId is not null)
                return IntentOutcome.Reject($"unit {id} is in a group");
            if (u.IsEmbarked || u.Traversal != Traversal.Foot)
                return IntentOutcome.Reject($"unit {id} can't walk a land route");
        }

        var crew = new RouteCrew
        {
            CrewId = route.NextCrewId++,
            Members = members,
            CurrentStop = StartStop,
        };
        route.Crews.Add(crew);
        foreach (var id in members) world.Units[id].RouteId = RouteId;
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"AddRouteCrew(route={RouteId}, {Members.Count} members, start={StartStop})";
}

// Take a crew off its route. The members stop where they stand and keep
// whatever they carry; they are ordinary units again (and haulers rejoin the
// queue's pool once empty).
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

        RouteCrews.Release(world, route, crew);
        route.Crews.Remove(crew);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"RemoveRouteCrew(route={RouteId}, crew={CrewId})";
}

// Delete a route and release every crew on it.
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

        foreach (var crew in route.Crews) RouteCrews.Release(world, route, crew);
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

    // Clear Unit.RouteId for the crew's living members. A member that died
    // is already gone from world.Units; one that somehow belongs elsewhere
    // is left alone.
    internal static void Release(GameWorld world, HaulRoute route, RouteCrew crew)
    {
        foreach (var id in crew.Members)
            if (world.Units.TryGetValue(id, out var u) && u.RouteId == route.RouteId)
                u.RouteId = null;
    }

    // The crew's members still alive and still on this route, ascending.
    public static List<Unit> Living(GameWorld world, HaulRoute route, RouteCrew crew)
    {
        var alive = new List<Unit>(crew.Members.Count);
        foreach (var id in crew.Members)
            if (world.Units.TryGetValue(id, out var u) && u.OwnerId == route.OwnerId
                && u.RouteId == route.RouteId)
                alive.Add(u);
        return alive;
    }
}
