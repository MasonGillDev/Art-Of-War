namespace Sim.Core.Hauling;

// Create a named route: a loop of stops with pickup / drop rules and no
// crew yet (AssignGroupToRouteIntent puts a group on it). docs/hauling-queue-and-routes.md.
//
// Structural checks only. A stop with no structure of the player's is legal
// (a waypoint, or a stop whose building is not up yet): its rules simply
// move nothing, the crew passes through, and the player sees why.
//
// Name (optional, M45): the player's name for the route, cleaned by
// RouteNames. Older logged intents carry none and stay unnamed.
public sealed class SetHaulRouteIntent : Intent
{
    public List<RouteStop> Stops { get; }
    public string Name { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetHaulRouteIntent(List<RouteStop> stops, string? name = null)
    {
        Stops = stops ?? new();
        Name = name ?? "";
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (RouteStops.Check(world, Stops) is { } why)
            return IntentOutcome.Reject(why);
        if (RouteNames.Clean(Name, out var name) is { } badName)
            return IntentOutcome.Reject(badName);

        var owned = 0;
        foreach (var (_, r) in world.HaulRoutes)
            if (r.OwnerId == PlayerId) owned++;
        if (owned >= HaulingConstants.MaxRoutesPerPlayer)
            return IntentOutcome.Reject(
                $"player {PlayerId} already has {owned} routes (cap {HaulingConstants.MaxRoutesPerPlayer})");

        var id = world.NextHaulRouteId++;
        world.HaulRoutes.Add(id, new HaulRoute
        {
            RouteId = id,
            OwnerId = PlayerId,
            Stops = RouteStops.Copy(Stops),
            Name = name,
        });

        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetHaulRoute({Stops.Count} stops)";
}
