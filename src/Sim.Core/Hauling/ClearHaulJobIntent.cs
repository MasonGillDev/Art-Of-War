namespace Sim.Core.Hauling;

// Remove a job from the player's haul queue. Haulers already walking for it
// finish their trip: their HaulPlan still names the job, and a deposit for a
// job that no longer exists simply isn't counted.
public sealed class ClearHaulJobIntent : Intent
{
    public int JobId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ClearHaulJobIntent(int jobId) { JobId = jobId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.HaulJobs.TryGetValue(JobId, out var job))
            return IntentOutcome.Reject($"haul job {JobId} does not exist");
        if (job.OwnerId != PlayerId)
            return IntentOutcome.Reject($"haul job {JobId} not owned by player {PlayerId}");
        world.HaulJobs.Remove(JobId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ClearHaulJob(job={JobId})";
}
