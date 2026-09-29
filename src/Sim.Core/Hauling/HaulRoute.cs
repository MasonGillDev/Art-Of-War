namespace Sim.Core.Hauling;

// Append-only enum (serialized).
public enum StopRuleOp : byte
{
    // Fill this resource UP TO Percent of the hauler's capacity (what is
    // already aboard counts toward it).
    Pickup = 1,
    // Hand over up to Percent of the hauler's capacity worth of this
    // resource; keep whatever the stop can't take.
    Drop   = 2,
}

// One rule at a stop. Percent is of CAPACITY, never of what is carried or
// what the stop holds: 25/25/25/25 over four stops splits evenly in any
// order, and the schedule scales on its own when a cart raises capacity
// (docs/hauling-queue-and-routes.md, "the route's rules").
public readonly record struct StopRule(Resource Resource, StopRuleOp Op, int Percent)
{
    // The rule's size for one hauler: Percent of capacity, rounded down,
    // never less than 1 (a 1% rule on a 25-cap hauler still moves a unit).
    public int QuotaFor(int capacity) =>
        capacity <= 0 ? 0 : Math.Max(1, capacity * Percent / 100);
}

public sealed class RouteStop
{
    public TileCoord Tile { get; init; }
    public List<StopRule> Rules { get; init; } = new();
}

// One crew on a route: the members walk together and serve each stop
// together. CurrentStop is the stop the crew is heading to (or standing at,
// about to serve). Durable because "which stop am I on" is not in the world:
// a crew on a tile that appears twice in the loop can't be placed by map
// alone (the Routine cursor lesson, docs/automation-substrate.md).
public sealed class RouteCrew
{
    public int CrewId { get; init; }
    public List<int> Members { get; init; } = new();   // ascending, distinct
    public int CurrentStop { get; set; }
}

// A NAMED ROUTE (docs/hauling-queue-and-routes.md): a loop of stops, each
// with pickup / drop rules, walked by one or more crews that never stop.
// Haulers on a route are never in the queue's pool.
//
// Definition (Stops) is fixed at Set; the crews list changes through
// Add/RemoveRouteCrewIntent; a crew's cursor moves only through
// ServeRouteStopIntent.
public sealed class HaulRoute
{
    public int RouteId { get; init; }
    public int OwnerId { get; init; }
    public List<RouteStop> Stops { get; init; } = new();
    public List<RouteCrew> Crews { get; init; } = new();   // ascending CrewId
    public int NextCrewId { get; set; } = 1;
}
