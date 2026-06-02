namespace Sim.Core.Engine;

// Base type for every discrete event the resolver handles.
// `At` is the sim-tick the event fires; `Seq` is the monotonic id stamped at
// schedule time so events with equal `At` resolve in submission order — and
// from M1 on, that ordering is a *fairness* property, not just reproducibility.
public abstract class ScheduledEvent
{
    public long At { get; internal set; }
    public long Seq { get; internal set; }

    public abstract void Apply(Simulation sim);

    public virtual string Describe() => GetType().Name;
}
