namespace Sim.Core.Mining;

// M44 — the dig is done; the Miner reports. Fenced on the survey's own
// anchor (architecture §2.6): a cancelled survey (cleared plan) or a
// restarted one (different CompleteTick) makes this a no-op.
public sealed class SurveyCompleteEvent : ScheduledEvent
{
    public int UnitId { get; }

    public SurveyCompleteEvent(int unitId) { UnitId = unitId; }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit)) return;   // died
        if (unit.Survey is not { } plan || plan.CompleteTick != At) return;
        SurveyRules.Complete(sim, unit);
    }

    public override string Describe() => $"SurveyComplete(unit={UnitId})";
}
