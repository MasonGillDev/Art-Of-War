using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Population;

// M30 — the deadline that keeps "a goal that can never complete dissolves"
// honest (docs/goal-shaped-intents.md).
//
// Every other wake-up in the goal engine rides an event that already exists: a
// partner arriving, food landing in a house. Those are enough to COMPLETE a
// goal, but not to FAIL one, because the failure in question is the absence of
// an event — food that never comes while the parents age past fertility. Left
// alone, that pair would stand at the house for the rest of their lives, which
// is precisely the silent stall the visibility contract forbids.
//
// So one scheduled event, at the earlier of the two parents' fertility
// deadlines, takes the last look: conceive if it can, dissolve if it cannot.
// Fenced the M4 way — the house must still be expecting exactly this pair, or
// the event is stale and no-ops.
public sealed class GoalExpiryEvent : ScheduledEvent
{
    public TileCoord HouseTile { get; }
    public int ParentAId { get; }
    public int ParentBId { get; }

    public GoalExpiryEvent(TileCoord houseTile, int parentAId, int parentBId)
    {
        HouseTile = houseTile;
        ParentAId = parentAId;
        ParentBId = parentBId;
    }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(HouseTile, out var s) || s is not House house)
        {
            Outcome = IntentOutcome.Reject($"no House at {HouseTile.X},{HouseTile.Y}");
            return;
        }
        if (house.PendingBreed is not { } pending
            || pending.ParentAId != ParentAId || pending.ParentBId != ParentBId
            || pending.ExpiryTick != At || pending.ExpirySeq != Seq)
        {
            // Conceived, dissolved, or re-reserved by a different pair since
            // this was scheduled.
            Outcome = IntentOutcome.Reject("stale (house no longer expects this pair)");
            return;
        }

        // Last chance: the deadline tick is still inside the fertility window
        // for at most this instant, so a delivery landing on the very same
        // tick (earlier Seq) has already had its say.
        BreedGoal.TryConceive(sim, house);
        if (house.Occupation is not null)
        {
            Outcome = IntentOutcome.Applied;
            return;
        }

        // Dissolve both halves. Clearing the registration FIRST stops the
        // second Dissolve from trying to tear down a reservation that the
        // first one already removed.
        house.PendingBreed = null;
        if (world.Units.TryGetValue(ParentAId, out var a))
            GoalRules.Dissolve(sim, a, "no longer fertile");
        if (world.Units.TryGetValue(ParentBId, out var b))
            GoalRules.Dissolve(sim, b, "no longer fertile");
        Outcome = IntentOutcome.Reject("breeding goal expired (fertility)");
    }

    public override string Describe() =>
        $"GoalExpiry(house={HouseTile.X},{HouseTile.Y} a={ParentAId} b={ParentBId})";
}
