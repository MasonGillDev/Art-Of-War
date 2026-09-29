namespace Sim.Core.Hauling;

// Add a job to the back of the player's haul queue
// (docs/hauling-queue-and-routes.md).
//
// Structural checks only (intent-validation.md): both ends exist and are
// the player's, the resource is real, the target is positive. Whether the
// job will ever be served (the source may be empty, the pool may have no
// haulers) is the player's business: a job with nothing to do just waits
// in line.
public sealed class SetHaulJobIntent : Intent
{
    public TileCoord Source { get; }
    public TileCoord Dest { get; }
    public Resource Resource { get; }
    public HaulJobKind Kind { get; }
    public int Target { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetHaulJobIntent(TileCoord source, TileCoord dest, Resource resource, HaulJobKind kind, int target)
    {
        Source = source;
        Dest = dest;
        Resource = resource;
        Kind = kind;
        Target = target;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (Kind != HaulJobKind.Standing && Kind != HaulJobKind.Once && Kind != HaulJobKind.Salvage)
            return IntentOutcome.Reject($"unknown job kind {(byte)Kind}");
        if (Source == Dest)
            return IntentOutcome.Reject("source and destination are the same tile");
        if (Kind == HaulJobKind.Salvage)
        {
            // M40 — salvage takes everything (no resource named); Target is its
            // crew; the source is nobody's and must be charted or seen.
            if (Resource != Resource.None)
                return IntentOutcome.Reject("salvage takes everything: name no resource");
            if (Target < 1 || Target > HaulingConstants.MaxSalvageCrew)
                return IntentOutcome.Reject($"salvage crew {Target} outside 1..{HaulingConstants.MaxSalvageCrew}");
            if (Salvage.Blocker(world, PlayerId, Source) is { } why)
                return IntentOutcome.Reject(why);
        }
        else
        {
            if (Resource == Resource.None)
                return IntentOutcome.Reject("no resource named");
            if (Target <= 0)
                return IntentOutcome.Reject($"target {Target} must be positive");
            if (!world.Structures.TryGetValue(Source, out var src))
                return IntentOutcome.Reject($"no structure at source {Source.X},{Source.Y}");
            if (src.OwnerId != PlayerId)
                return IntentOutcome.Reject($"source {Source.X},{Source.Y} not owned by player {PlayerId}");
        }
        if (!world.Structures.TryGetValue(Dest, out var dst))
            return IntentOutcome.Reject($"no structure at destination {Dest.X},{Dest.Y}");
        if (dst.OwnerId != PlayerId)
            return IntentOutcome.Reject($"destination {Dest.X},{Dest.Y} not owned by player {PlayerId}");

        var owned = 0;
        foreach (var (_, j) in world.HaulJobs)
            if (j.OwnerId == PlayerId) owned++;
        if (owned >= HaulingConstants.MaxJobsPerPlayer)
            return IntentOutcome.Reject(
                $"player {PlayerId} already has {owned} haul jobs (cap {HaulingConstants.MaxJobsPerPlayer})");

        var id = world.NextHaulJobId++;
        world.HaulJobs.Add(id, new HaulJob
        {
            JobId = id,
            OwnerId = PlayerId,
            Source = Source,
            Dest = Dest,
            Resource = Resource,
            Kind = Kind,
            Target = Target,
            QueueStamp = world.NextHaulStamp++,
            QueuedAtTick = sim.Now,
        });
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"SetHaulJob({Kind} {Target} {Resource} {Source.X},{Source.Y} -> {Dest.X},{Dest.Y})";
}
