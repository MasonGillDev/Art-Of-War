namespace Sim.Core.Intents;

// Wraps an Intent so intent-resolution is just another event in the queue.
// This guarantees intents and consequence-events are ordered by the same rule.
public sealed class IntentEvent : ScheduledEvent
{
    public Intent Intent { get; }
    public IntentEvent(Intent intent) { Intent = intent; }

    public override void Apply(Simulation sim)
    {
        // M24 — defeated-player gate. A player whose Castle has been razed
        // cannot issue new commands; every intent they queued earlier and
        // every intent the wire still forwards rejects here, BEFORE the
        // per-intent Resolve runs. The unit-level state (existing Units,
        // standing-order cursors, in-flight events) is untouched — only
        // NEW will is silenced. See docs/sieges-and-conquest.md.
        if (sim.World.Players.TryGetValue(Intent.PlayerId, out var player) && player.Defeated)
        {
            Outcome = IntentOutcome.Reject($"player {Intent.PlayerId} is defeated");
            return;
        }
        Outcome = Intent.Resolve(sim);
    }

    public override string Describe() => $"Intent[{Intent.Describe()}]";
}
