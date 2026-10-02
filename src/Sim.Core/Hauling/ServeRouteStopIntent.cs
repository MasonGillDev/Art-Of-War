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
// The rules, per CARRIER standing at the stop. A crew is the units the
// player named, so every member carries at its own capacity, except
// Soldiers and Archers, who are escort (M45; M36 had Haulers only):
//   1. every Drop rule, in order: hand over up to Percent of capacity worth
//      of the resource; what the structure can't take stays aboard;
//   2. every Pickup rule, in order: fill the resource up to Percent of
//      capacity, limited by free space and by what the structure holds.
// Drops go first so a stop that trades ore for iron frees the space it then
// fills. Only a structure of the route owner's on the stop tile is traded
// with; anywhere else the crew passes through untouched. The crew never
// waits: a stop that can't be served is skipped, and leftovers ride on.
//
// M45 -- the serve writes RouteCrew.LastServe (what moved, what got in the
// way) and is also fenced on the route's Revision, so a serve submitted
// before an UpdateHaulRouteIntent no-ops. ExpectedRevision -1 (logs written
// before M45) skips that fence.
public sealed class ServeRouteStopIntent : Intent
{
    public int RouteId { get; }
    public int CrewId { get; }
    public int ExpectedStop { get; }
    public int ExpectedRevision { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ServeRouteStopIntent(int routeId, int crewId, int expectedStop, int expectedRevision = -1)
    {
        RouteId = routeId;
        CrewId = crewId;
        ExpectedStop = expectedStop;
        ExpectedRevision = expectedRevision;
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
        if (ExpectedRevision >= 0 && route.Revision != ExpectedRevision)
            return IntentOutcome.Reject(
                $"revision fence: route {RouteId} is at revision {route.Revision}, expected {ExpectedRevision}");

        var stop = route.Stops[crew.CurrentStop];
        // M47 — the crew is a group: it serves as a body once it has formed up at the stop
        // (its block may spill onto the next tile), every member standing still there.
        if (!world.Groups.TryGetValue(crew.GroupId, out var group))
            return IntentOutcome.Reject($"crew {CrewId}'s group {crew.GroupId} is gone");
        if (group.RouteSuspended)
            return IntentOutcome.Reject($"crew {CrewId} has been called away");
        if (group.State != Sim.Core.Groups.GroupState.Idle || group.Position != stop.Tile)
            return IntentOutcome.Reject($"crew {CrewId} has not formed up at stop {crew.CurrentStop}");
        // Standing in the block: on the stop or a tile beside it (a crew's block spills
        // where a building's footprint leaves too little room on the stop itself).
        var present = new List<Unit>();
        foreach (var u in RouteCrews.Living(world, route, crew))
            if (UnitAvailability.IsFree(world, u)
                && Math.Max(Math.Abs(u.Position.X - stop.Tile.X), Math.Abs(u.Position.Y - stop.Tile.Y)) <= 1)
                present.Add(u);
        if (present.Count == 0)
            return IntentOutcome.Reject($"no member of crew {CrewId} is standing at stop {crew.CurrentStop}");

        var loaded = 0;
        var unloaded = 0;
        var notes = ServeNote.None;
        if (!world.Structures.TryGetValue(stop.Tile, out var here) || here.OwnerId != route.OwnerId)
            notes |= ServeNote.NotYours;
        else if (!present.Exists(RouteCrews.Carries))
            notes |= ServeNote.NoCarriers;
        else
        {
            foreach (var u in present)
            {
                if (!RouteCrews.Carries(u)) continue;
                var before = u.CargoAmount;
                var cap = u.CargoCapacity;

                foreach (var rule in stop.Rules)
                {
                    if (rule.Op != StopRuleOp.Drop) continue;
                    var give = Math.Min(u.Cargo.AmountOf(rule.Resource), rule.QuotaFor(cap));
                    if (give <= 0) continue;
                    var taken = CargoTransfer.DepositInto(sim, here, rule.Resource, give);
                    u.Cargo.Take(rule.Resource, taken);
                    unloaded += taken;
                    if (taken < give) notes |= ServeNote.DropRefused;
                }
                foreach (var rule in stop.Rules)
                {
                    if (rule.Op != StopRuleOp.Pickup) continue;
                    var want = rule.QuotaFor(cap) - u.Cargo.AmountOf(rule.Resource);
                    if (want <= 0) continue;
                    var room = Math.Min(want, cap - u.CargoAmount);
                    if (room <= 0) { notes |= ServeNote.CarrierFull; continue; }
                    var got = CargoTransfer.WithdrawFrom(sim, here, rule.Resource, room);
                    u.Cargo.Add(rule.Resource, got);
                    loaded += got;
                    if (got < room) notes |= ServeNote.SourceEmpty;
                }

                if (u.CargoAmount != before) u.BumpEpoch();
            }
        }

        crew.LastServe = new ServeReport(crew.CurrentStop, sim.Now, loaded, unloaded, notes);
        crew.CurrentStop = (crew.CurrentStop + 1) % route.Stops.Count;

        // M47 — a muster that came mid-leg is answered now the leg is done (in this same
        // intent, so serving and answering can't come apart across a restart).
        if (group.PendingMuster is { } anchor)
        {
            group.PendingMuster = null;
            group.RouteSuspended = true;
            Sim.Core.Groups.GroupMuster.Muster(sim, group, anchor);
        }
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ServeRouteStop(route={RouteId}, crew={CrewId}, stop={ExpectedStop})";
}
