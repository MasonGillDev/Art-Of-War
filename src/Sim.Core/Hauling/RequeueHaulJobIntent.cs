namespace Sim.Core.Hauling;

// Send a job to the back of its owner's queue, the moment a hauler takes it
// (docs/hauling-queue-and-routes.md, "re-queue when a hauler takes the job").
//
// SERVER-INTERNAL (wire-rejected, driver-submitted) but DURABLE like every
// intent, so a restart resumes the queue in the same order.
//
// NO STAMP FENCE, on purpose. One think can hand a hungry job several
// haulers, sending it to the back several times, interleaved with other
// jobs. Requeues resolve in submission order, so "move to the back" applied
// in that order reproduces exactly the line the driver walked. A fence on
// the expected stamp would need the driver to predict stamps, and any player
// intent resolving in the same tick (a new job takes a stamp too) would
// break the prediction and silently drop a move.
public sealed class RequeueHaulJobIntent : Intent
{
    public int JobId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public RequeueHaulJobIntent(int jobId) { JobId = jobId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulJobs.TryGetValue(JobId, out var job))
            return IntentOutcome.Reject($"haul job {JobId} does not exist");
        if (job.OwnerId != PlayerId)
            return IntentOutcome.Reject($"haul job {JobId} not owned by player {PlayerId}");
        job.QueueStamp = world.NextHaulStamp++;
        job.QueuedAtTick = sim.Now;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"RequeueHaulJob(job={JobId})";
}
