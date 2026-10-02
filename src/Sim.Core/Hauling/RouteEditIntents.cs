namespace Sim.Core.Hauling;

// M45 — change a running route's stops without touching its crews
// (docs/hauling-queue-and-routes.md, addendum 2026-10-01).
//
// The alternative was clear-and-recreate, which releases every crew, gives
// the route a new number, and sends the crews round from a fresh start. A
// player tuning "drop 6 wood here instead of 12" should not pay for that.
//
// Each crew keeps its place in the loop (RouteStops.Remap: same stop by tile,
// else the next surviving one). The route's revision moves on, so a serve the
// driver already submitted against the old list no-ops instead of serving
// whatever stop now sits at that index. Members keep their cargo; anything
// they carry that the new rules no longer drop simply rides on, as leftovers
// always do.
public sealed class UpdateHaulRouteIntent : Intent
{
    public int RouteId { get; }
    public List<RouteStop> Stops { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public UpdateHaulRouteIntent(int routeId, List<RouteStop> stops)
    {
        RouteId = routeId;
        Stops = stops ?? new();
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulRoutes.TryGetValue(RouteId, out var route))
            return IntentOutcome.Reject($"route {RouteId} does not exist");
        if (route.OwnerId != PlayerId)
            return IntentOutcome.Reject($"route {RouteId} not owned by player {PlayerId}");
        if (RouteStops.Check(world, Stops) is { } why)
            return IntentOutcome.Reject(why);

        var fresh = RouteStops.Copy(Stops);
        foreach (var crew in route.Crews)
        {
            crew.CurrentStop = RouteStops.Remap(route.Stops, fresh, crew.CurrentStop);
            crew.LastServe = null;   // its stop index belonged to the old list
        }
        route.Stops.Clear();
        route.Stops.AddRange(fresh);
        route.Revision++;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"UpdateHaulRoute(route={RouteId}, {Stops.Count} stops)";
}

// M45 — name a route ("Wood to the north farms"). "" clears the name.
public sealed class RenameHaulRouteIntent : Intent
{
    public int RouteId { get; }
    public string Name { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public RenameHaulRouteIntent(int routeId, string? name)
    {
        RouteId = routeId;
        Name = name ?? "";
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulRoutes.TryGetValue(RouteId, out var route))
            return IntentOutcome.Reject($"route {RouteId} does not exist");
        if (route.OwnerId != PlayerId)
            return IntentOutcome.Reject($"route {RouteId} not owned by player {PlayerId}");
        if (RouteNames.Clean(Name, out var clean) is { } why)
            return IntentOutcome.Reject(why);

        route.Name = clean;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"RenameHaulRoute(route={RouteId}, \"{Name}\")";
}

// M45 — change a queued job's amount or kind and KEEP ITS PLACE IN LINE.
//
// The alternative, clear and queue again, sends the job to the back: the
// player who only meant "40, not 25" would lose the job's turn. Haulers
// already walking for it finish their trips (their plans name the job id,
// which does not change).
//
// Standing and Once may switch into each other. Salvage stays salvage (its
// Target is a crew size, not an amount, and its source is nobody's). A Once
// job keeps what it has delivered; switching to Once starts the count at
// zero. A Once job whose new amount is already met is done and removed.
public sealed class UpdateHaulJobIntent : Intent
{
    public int JobId { get; }
    public HaulJobKind Kind { get; }
    public int Target { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public UpdateHaulJobIntent(int jobId, HaulJobKind kind, int target)
    {
        JobId = jobId;
        Kind = kind;
        Target = target;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulJobs.TryGetValue(JobId, out var job))
            return IntentOutcome.Reject($"haul job {JobId} does not exist");
        if (job.OwnerId != PlayerId)
            return IntentOutcome.Reject($"haul job {JobId} not owned by player {PlayerId}");

        if (job.Kind == HaulJobKind.Salvage || Kind == HaulJobKind.Salvage)
        {
            if (job.Kind != Kind)
                return IntentOutcome.Reject("salvage can't become a delivery or the other way round");
            if (Target < 1 || Target > HaulingConstants.MaxSalvageCrew)
                return IntentOutcome.Reject($"salvage crew {Target} outside 1..{HaulingConstants.MaxSalvageCrew}");
        }
        else
        {
            if (Kind != HaulJobKind.Standing && Kind != HaulJobKind.Once)
                return IntentOutcome.Reject($"unknown job kind {(byte)Kind}");
            if (Target <= 0)
                return IntentOutcome.Reject($"target {Target} must be positive");
        }

        var delivered = Kind == HaulJobKind.Once && job.Kind == HaulJobKind.Once ? job.Delivered : 0;
        if (Kind == HaulJobKind.Once && delivered >= Target)
        {
            world.HaulJobs.Remove(JobId);   // already met: the job is done
            return IntentOutcome.Applied;
        }

        // The definition fields are init-only by design (one write site each);
        // the update replaces the record whole and carries the line position.
        world.HaulJobs[JobId] = new HaulJob
        {
            JobId = job.JobId,
            OwnerId = job.OwnerId,
            Source = job.Source,
            Dest = job.Dest,
            Resource = job.Resource,
            Kind = Kind,
            Target = Target,
            Delivered = delivered,
            QueueStamp = job.QueueStamp,
            QueuedAtTick = job.QueuedAtTick,
        };
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"UpdateHaulJob(job={JobId}, {Kind} {Target})";
}
