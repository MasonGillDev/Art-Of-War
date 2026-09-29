namespace Sim.Core.Hauling;

// A crew has reached its stop: apply the stop's rules, then point the crew
// at the next stop. docs/hauling-queue-and-routes.md.
//
// ONE intent does both, so serving and advancing can't come apart: a
// restart between them would otherwise serve the same stop twice.
//
// SERVER-INTERNAL (wire-rejected, driver-submitted), durable, and fenced on
// the stop the driver saw, so a stale serve (the crew was re-staffed or the
// route cleared in between) no-ops.
//
// The rules, per Hauler-role member standing at the stop (escorts carry
// nothing):
//   1. every Drop rule, in order: hand over up to Percent of capacity worth
//      of the resource; what the structure can't take stays aboard;
//   2. every Pickup rule, in order: fill the resource up to Percent of
//      capacity, limited by free space and by what the structure holds.
// Drops go first so a stop that trades ore for iron frees the space it then
// fills. Only a structure of the route owner's on the stop tile is traded
// with; anywhere else the crew passes through untouched. The crew never
// waits: a stop that can't be served is skipped, and leftovers ride on.
public sealed class ServeRouteStopIntent : Intent
{
    public int RouteId { get; }
    public int CrewId { get; }
    public int ExpectedStop { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ServeRouteStopIntent(int routeId, int crewId, int expectedStop)
    {
        RouteId = routeId;
        CrewId = crewId;
        ExpectedStop = expectedStop;
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
        if (crew.CurrentStop != ExpectedStop)
            return IntentOutcome.Reject(
                $"stop fence: crew {CrewId} is at stop {crew.CurrentStop}, expected {ExpectedStop}");

        var stop = route.Stops[crew.CurrentStop];
        var present = new List<Unit>();
        foreach (var u in RouteCrews.Living(world, route, crew))
            if (u.Position == stop.Tile && u.Activity == Activity.Idle && u.PathRemaining is null)
                present.Add(u);
        if (present.Count == 0)
            return IntentOutcome.Reject($"no member of crew {CrewId} is standing at stop {crew.CurrentStop}");

        if (world.Structures.TryGetValue(stop.Tile, out var here) && here.OwnerId == route.OwnerId)
        {
            foreach (var u in present)
            {
                if (u.Role != UnitRole.Hauler) continue;
                var before = u.CargoAmount;
                var cap = u.CargoCapacity;

                foreach (var rule in stop.Rules)
                {
                    if (rule.Op != StopRuleOp.Drop) continue;
                    var give = Math.Min(u.Cargo.AmountOf(rule.Resource), rule.QuotaFor(cap));
                    u.Cargo.Take(rule.Resource, CargoTransfer.DepositInto(sim, here, rule.Resource, give));
                }
                foreach (var rule in stop.Rules)
                {
                    if (rule.Op != StopRuleOp.Pickup) continue;
                    var want = rule.QuotaFor(cap) - u.Cargo.AmountOf(rule.Resource);
                    var room = Math.Min(want, cap - u.CargoAmount);
                    u.Cargo.Add(rule.Resource, CargoTransfer.WithdrawFrom(sim, here, rule.Resource, room));
                }

                if (u.CargoAmount != before) u.BumpEpoch();
            }
        }

        crew.CurrentStop = (crew.CurrentStop + 1) % route.Stops.Count;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ServeRouteStop(route={RouteId}, crew={CrewId}, stop={ExpectedStop})";
}
