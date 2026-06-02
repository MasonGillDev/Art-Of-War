namespace Sim.Core.Engine;

// Base type for every discrete event the resolver handles.
// `At` is the sim-tick the event fires; `Seq` is the monotonic id stamped at
// schedule time so events with equal `At` resolve in submission order — and
// from M1 on, that ordering is a *fairness* property, not just reproducibility.
public abstract class ScheduledEvent
{
    public long At { get; internal set; }
    public long Seq { get; internal set; }

    // Outcome of the resolution. Defaults to Applied; events that fail their
    // resolution-time validation (see docs/intent-validation.md) reassign
    // this to IntentOutcome.Reject(reason) and return without mutating state.
    public IntentOutcome Outcome { get; protected set; } = IntentOutcome.Applied;

    public abstract void Apply(Simulation sim);

    public virtual string Describe() => GetType().Name;
}
