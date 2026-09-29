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
public sealed class SetHaulRouteIntent : Intent
{
    public List<RouteStop> Stops { get; }
    public List<int> Crew { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetHaulRouteIntent(List<RouteStop> stops, List<int>? crew = null)
    {
        Stops = stops ?? new();
        Crew = crew ?? new();
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (Stops.Count == 0)
            return IntentOutcome.Reject("a route needs at least one stop");
        if (Stops.Count > HaulingConstants.MaxStopsPerRoute)
            return IntentOutcome.Reject(
                $"route of {Stops.Count} stops exceeds cap {HaulingConstants.MaxStopsPerRoute}");

        foreach (var stop in Stops)
        {
            if (stop is null)
                return IntentOutcome.Reject("null stop");
            if (!world.Grid.InBounds(stop.Tile))
                return IntentOutcome.Reject($"stop {stop.Tile.X},{stop.Tile.Y} out of bounds");
            if (stop.Rules.Count > HaulingConstants.MaxRulesPerStop)
                return IntentOutcome.Reject(
                    $"stop {stop.Tile.X},{stop.Tile.Y} has {stop.Rules.Count} rules (cap {HaulingConstants.MaxRulesPerStop})");
            foreach (var rule in stop.Rules)
            {
                if (rule.Resource == Resource.None)
                    return IntentOutcome.Reject("a stop rule needs a resource");
                if (rule.Op != StopRuleOp.Pickup && rule.Op != StopRuleOp.Drop)
                    return IntentOutcome.Reject($"unknown stop rule op {(byte)rule.Op}");
                if (rule.Percent < 1 || rule.Percent > 100)
                    return IntentOutcome.Reject($"percent {rule.Percent} must be 1..100");
            }
        }

        var owned = 0;
        foreach (var (_, r) in world.HaulRoutes)
            if (r.OwnerId == PlayerId) owned++;
        if (owned >= HaulingConstants.MaxRoutesPerPlayer)
            return IntentOutcome.Reject(
                $"player {PlayerId} already has {owned} routes (cap {HaulingConstants.MaxRoutesPerPlayer})");

        // Deep copy: the intent's lists belong to the durable log, the
        // route's to the world.
        var id = world.NextHaulRouteId++;
        world.HaulRoutes.Add(id, new HaulRoute
        {
            RouteId = id,
            OwnerId = PlayerId,
            Stops = Stops.Select(s => new RouteStop { Tile = s.Tile, Rules = new List<StopRule>(s.Rules) })
                .ToList(),
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
