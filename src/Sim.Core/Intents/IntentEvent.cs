namespace Sim.Core.Intents;

// Wraps an Intent so intent-resolution is just another event in the queue.
// This guarantees intents and consequence-events are ordered by the same rule.
public sealed class IntentEvent : ScheduledEvent
{
    public Intent Intent { get; }
    public IntentEvent(Intent intent) { Intent = intent; }

    public override void Apply(Simulation sim) => Outcome = Intent.Resolve(sim);
    public override string Describe() => $"Intent[{Intent.Describe()}]";
}
