namespace Sim.Core.Hauling;

// Create a named route: a loop of stops with pickup / drop rules and no
// crew yet (AddRouteCrewIntent staffs it). docs/hauling-queue-and-routes.md.
//
// Structural checks only. A stop with no structure of the player's is legal
// (a waypoint, or a stop whose building is not up yet): its rules simply
// move nothing, the crew passes through, and the player sees why.
//
// Crew (optional): the route's first crew, starting at stop 0, in the same
// resolution. A client cannot know a new route's id before it exists, so
// "draw a route for these people" has to be one intent. The crew is checked
// exactly as AddRouteCrewIntent checks it; if it fails, the route is not
// created either (fail clean).
//
// Name (optional, M45): the player's name for the route, cleaned by
// RouteNames. Older logged intents carry none and stay unnamed.
public sealed class SetHaulRouteIntent : Intent
{
    public List<RouteStop> Stops { get; }
    public List<int> Crew { get; }
    public string Name { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetHaulRouteIntent(List<RouteStop> stops, List<int>? crew = null, string? name = null)
    {
        Stops = stops ?? new();
        Crew = crew ?? new();
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

        if (Crew.Count > 0)
        {
            var staffed = new AddRouteCrewIntent(id, Crew, startStop: 0) { PlayerId = PlayerId }.Resolve(sim);
            if (!staffed.IsApplied)
            {
                // AddRouteCrew mutates nothing when it rejects; undo the route.
                world.HaulRoutes.Remove(id);
                world.NextHaulRouteId = id;
                return IntentOutcome.Reject($"crew: {staffed.Reason}");
            }
        }
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetHaulRoute({Stops.Count} stops)";
}
