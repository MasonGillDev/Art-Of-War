using Sim.Core.Intents;

namespace Sim.Core.Diplomacy;

// M6 Phase C — unilateral, telegraphed war declaration.
//
// The declarer commits to going hostile with the target after a uniform
// world-level Delay. The target sees the pending war in their PlayerView
// from the moment of declaration; the relationship doesn't actually flip
// to Enemy until the WarBecomesEffectiveEvent fires at declaredTick + Delay.
//
// Aggression is unilateral by design — you can't require the target's
// consent to attack them. Fairness comes from the telegraph: the target
// has the full Delay window to see it coming and react (sue for peace,
// reposition forces, ready defenses).
//
// Rejected if:
//   * declarer == target;
//   * either faction missing from world.Players;
//   * the pair is already Enemy;
//   * the pair already has a pending war.
//
// Today's commitment rule: once declared, the war cannot be cancelled
// unilaterally. The only way out is a bilateral peace proposal that the
// declarer accepts (Phase D), which clears the pending anchor before the
// effective event fires.
public sealed class DeclareWarIntent : Intent
{
    public int DeclarerId { get; }
    public int TargetId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DeclareWarIntent(int declarerId, int targetId)
    {
        DeclarerId = declarerId;
        TargetId = targetId;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (DeclarerId == TargetId)
            return IntentOutcome.Reject($"declarer {DeclarerId} cannot declare war on itself");
        if (!sim.World.Players.ContainsKey(DeclarerId))
            return IntentOutcome.Reject($"declarer {DeclarerId} is not a registered faction");
        if (!sim.World.Players.ContainsKey(TargetId))
            return IntentOutcome.Reject($"target {TargetId} is not a registered faction");

        var pair = FactionPair.Of(DeclarerId, TargetId);
        var d = sim.World.Diplomacy;
        if (d.RelationshipBetween(DeclarerId, TargetId) == RelationshipState.Enemy)
            return IntentOutcome.Reject($"factions {DeclarerId} and {TargetId} are already at war");
        if (d.Relationships.TryGetValue(pair, out var existing) && existing.HasPendingWar)
            return IntentOutcome.Reject(
                $"war between {DeclarerId} and {TargetId} is already pending " +
                $"(effective at tick {existing.PendingEffectiveTick})");

        var effectiveTick = sim.Now + d.Config.Delay;
        var ev = new WarBecomesEffectiveEvent(pair);
        var seq = sim.Schedule(effectiveTick, ev);
        d.BeginPendingWar(pair, effectiveTick, seq);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"DeclareWarIntent(declarer={DeclarerId} target={TargetId})";
}
