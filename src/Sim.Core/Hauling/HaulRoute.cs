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

    // M45 — what the crew's last serve did, so a stop that moves nothing can
    // say why. Written ONLY by ServeRouteStopIntent; cleared by
    // UpdateHaulRouteIntent (its stop index may no longer mean the same stop).
    public ServeReport? LastServe { get; set; }
}

// M45 — why a serve moved less than its rules asked for. Flags: one serve can
// meet an empty store at one rule and a full one at another. Append-only bits.
[Flags]
public enum ServeNote : byte
{
    None        = 0,
    SourceEmpty = 1,    // a pick-up found less in store than it wanted
    DropRefused = 2,    // a drop was not (fully) taken: store full, or it doesn't take that
    CarrierFull = 4,    // a pick-up had no room left aboard
    NotYours    = 8,    // no building of the route owner's on the stop tile
    NoCarriers  = 16,   // everyone standing there is escort
}

// M45 — one serve, as the player needs to read it: which stop, when, how much
// came aboard and went out across the crew, and what got in the way.
public readonly record struct ServeReport(int Stop, long Tick, int Loaded, int Unloaded, ServeNote Notes);

// A NAMED ROUTE (docs/hauling-queue-and-routes.md): a loop of stops, each
// with pickup / drop rules, walked by one or more crews that never stop.
// Haulers on a route are never in the queue's pool.
//
// Definition (Stops) is set by SetHaulRouteIntent and replaced whole by
// UpdateHaulRouteIntent (M45); the crews list changes through
// Add/RemoveRouteCrewIntent; a crew's cursor moves only through
// ServeRouteStopIntent, and is remapped by UpdateHaulRouteIntent.
public sealed class HaulRoute
{
    public int RouteId { get; init; }
    public int OwnerId { get; init; }
    public List<RouteStop> Stops { get; init; } = new();
    public List<RouteCrew> Crews { get; init; } = new();   // ascending CrewId
    public int NextCrewId { get; set; } = 1;

    // M45 — the player's name for the route ("" = unnamed). Set by
    // SetHaulRouteIntent and RenameHaulRouteIntent only.
    public string Name { get; set; } = "";

    // M45 — bumped by every UpdateHaulRouteIntent. A serve the driver
    // submitted against the old stop list carries the revision it saw and
    // no-ops if the list changed under it (the stop index alone could now
    // name a different stop).
    public int Revision { get; set; }
}
