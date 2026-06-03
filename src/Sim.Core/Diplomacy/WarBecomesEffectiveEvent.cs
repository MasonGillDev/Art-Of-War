namespace Sim.Core.Diplomacy;

// M6 Phase C — fires at declaredTick + Delay to flip a pair's relationship
// to Enemy.
//
// Fencing: the event carries the FactionPair it was scheduled for; on Apply
// it checks the relationship's pending anchor (PendingEffectiveTick + Seq)
// still matches. If the pair has been ratified via a Phase D peace proposal
// before the effective tick, the pending anchor is cleared and this event
// no-ops when it fires — same M4 fencing discipline as
// MoveArrivalEvent/AssignmentEpoch and BuildCompleteEvent/ScheduledCompletion.
public sealed class WarBecomesEffectiveEvent : ScheduledEvent
{
    public FactionPair Pair { get; }

    public WarBecomesEffectiveEvent(FactionPair pair) { Pair = pair; }

    public override void Apply(Simulation sim)
    {
        var d = sim.World.Diplomacy;
        if (!d.Relationships.TryGetValue(Pair, out var rel))
        {
            // The pair has been wiped (e.g. via a hypothetical future
            // disband-faction path). Nothing to do.
            Outcome = IntentOutcome.Reject($"no relationship row for pair {Pair}");
            return;
        }
        if (rel.PendingEffectiveTick != At || rel.PendingSeq != Seq)
        {
            // The pending war was cleared (peace accepted) or replaced
            // (shouldn't happen today — no re-declare while pending — but
            // the fence is cheap).
            Outcome = IntentOutcome.Reject(
                $"stale war-effective event for {Pair} " +
                $"(rel pending=({rel.PendingEffectiveTick},{rel.PendingSeq}), event=({At},{Seq}))");
            return;
        }

        rel.State = RelationshipState.Enemy;
        rel.PendingEffectiveTick = null;
        rel.PendingSeq = null;
    }

    public override string Describe() => $"WarBecomesEffective({Pair})";
}
