namespace Sim.Server.Hauling;

// Knobs for the haul-queue driver (docs/hauling-queue-and-routes.md).
// Driver-side, not sim state: changing them changes when intents are
// submitted, never how a submitted intent resolves.
public sealed class HaulingConfig
{
    public bool Enabled { get; init; } = true;

    // How often the queue is walked. Shorter than the automation substrate's
    // 60: a hauler that finishes a trip waits at most this long for its next
    // job, and at tps 4 that is 5 seconds.
    public long ThinkPeriodTicks { get; init; } = 20;
}
