namespace Sim.Core.Progression;

// M37 — an omen's countdown ran out (docs/progression.md). Fenced on the
// omen's (DueTick, DueSeq) anchor: a slip reschedules and moves the anchor, so
// the older event no-ops. Regenerated from the anchor on restore.
public sealed class OmenDueEvent : ScheduledEvent
{
    public int OmenId { get; }

    public OmenDueEvent(int omenId) { OmenId = omenId; }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Omens.TryGetValue(OmenId, out var omen))
        {
            Outcome = IntentOutcome.Reject($"omen {OmenId} is gone");
            return;
        }
        if (omen.State != OmenState.Pending || omen.DueTick != At || omen.DueSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale omen event {OmenId} (stored=({omen.DueTick},{omen.DueSeq}), event=({At},{Seq}))");
            return;
        }
        Omens.Arrive(sim, omen);
    }

    public override string Describe() => $"OmenDue(omen={OmenId})";
}
