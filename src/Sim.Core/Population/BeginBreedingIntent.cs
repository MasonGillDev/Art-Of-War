using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Population;

// M8 Phase D, GOAL-SHAPED in M30 — "these two, that house".
//
// The player answers one question; the sim does the rest: both parents walk
// from wherever they are, the first to arrive waits, and conception fires the
// moment the house holds BirthFoodCost. See BreedGoal.cs for the mechanism and
// docs/goal-shaped-intents.md for why waiting-instead-of-failing is the whole
// point.
//
// WHAT IS STILL REJECTED HERE — malformed only, per the boundary rule
// "availability is a precondition, never a rejection":
//   1. House missing / not a House / not owned by PlayerId.
//   2. House already busy: Occupation (conceived) or PendingBreed (reserved).
//   3. ParentA == ParentB, or either missing / not owned / grouped / embarked.
//   4. Either parent already committed to another breeding cycle.
//   5. Either parent OUTSIDE the fertility window — see the note below.
//
// WHAT IS NO LONGER REJECTED: standing somewhere else (that's travel), and a
// house without the food yet (that's the wait).
//
// The fertility check is the one judgement call. Too OLD is
// impossible-forever, so it rejects — accepting it would create a goal that
// could only ever dissolve. Too YOUNG is temporary, and yet it also rejects:
// waiting years for a child to reach fertility, anchored to a house and unable
// to work, is a trap dressed as a feature. Both stay rejections; only the
// upper bound is re-tested at conception, where a long wait can cross it.
public sealed class BeginBreedingIntent : Intent
{
    public TileCoord HouseTile { get; }
    public int ParentAId { get; }
    public int ParentBId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public BeginBreedingIntent(TileCoord houseTile, int parentAId, int parentBId)
    {
        HouseTile = houseTile;
        ParentAId = parentAId;
        ParentBId = parentBId;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (ParentAId == ParentBId)
            return IntentOutcome.Reject("ParentA and ParentB must be different units");

        if (!world.Structures.TryGetValue(HouseTile, out var s) || s is not House house)
            return IntentOutcome.Reject($"no House at {HouseTile.X},{HouseTile.Y}");
        if (house.OwnerId != PlayerId)
            return IntentOutcome.Reject($"House at {HouseTile} not owned by player {PlayerId}");
        if (house.Occupation is not null)
            return IntentOutcome.Reject($"House at {HouseTile} is already occupied");
        if (house.PendingBreed is not null)
            return IntentOutcome.Reject($"House at {HouseTile} is already reserved for a pair");

        if (!world.Units.TryGetValue(ParentAId, out var a))
            return IntentOutcome.Reject($"ParentA {ParentAId} does not exist");
        if (!world.Units.TryGetValue(ParentBId, out var b))
            return IntentOutcome.Reject($"ParentB {ParentBId} does not exist");
        if (a.OwnerId != PlayerId || b.OwnerId != PlayerId)
            return IntentOutcome.Reject("both parents must be owned by the player");
        if (a.GroupId is not null || b.GroupId is not null)
            return IntentOutcome.Reject("grouped units can't be bound to a goal");
        if (a.IsEmbarked || b.IsEmbarked)
            return IntentOutcome.Reject("embarked units are off-tile");
        if (a.Activity != Activity.Idle || b.Activity != Activity.Idle)
            return IntentOutcome.Reject("both parents must be Idle");

        var cfg = world.PopulationConfig;
        if (!Population.CanBreed(a, sim.Now, cfg) || !Population.CanBreed(b, sim.Now, cfg))
            return IntentOutcome.Reject($"both parents must be in fertility window [{cfg.MinFertileAge}..{cfg.MaxFertileAge}]");

        // Neither parent already in another breeding cycle.
        if (Population.GetActiveBreedingFor(world, ParentAId) is not null)
            return IntentOutcome.Reject($"ParentA {ParentAId} already breeding");
        if (Population.GetActiveBreedingFor(world, ParentBId) is not null)
            return IntentOutcome.Reject($"ParentB {ParentBId} already breeding");
        if (a.Goal is not null || b.Goal is not null)
            return IntentOutcome.Reject("both parents must be free of other goals");

        // Reserve the house BEFORE dispatching: GoalRules.Begin can complete
        // instantly for a parent already standing here, and that completion
        // path reads the registration.
        house.PendingBreed = new PendingBreed { ParentAId = ParentAId, ParentBId = ParentBId };

        var startedA = GoalRules.Begin(sim, a, new GoalPlan(GoalKind.Breed, HouseTile, ParentBId));
        var startedB = GoalRules.Begin(sim, b, new GoalPlan(GoalKind.Breed, HouseTile, ParentAId));
        if (!startedA || !startedB)
        {
            // One of them can't get there at all (no route). Tear the whole
            // match down — half a breeding pair is not a goal.
            if (a.Goal is not null) GoalRules.Dissolve(sim, a, "partner cannot reach the house");
            if (b.Goal is not null) GoalRules.Dissolve(sim, b, "partner cannot reach the house");
            house.PendingBreed = null;
            return IntentOutcome.Reject("one of the parents cannot reach the house");
        }

        // The deadline that makes a never-fed pair fail instead of stalling.
        // Only worth scheduling while the pair is still pending — conception
        // clears PendingBreed and the event fences itself out.
        if (house.PendingBreed is { } reservation
            && BreedGoal.FertilityDeadline(world, a, b) is { } deadline
            && deadline > sim.Now)
        {
            var seq = sim.Schedule(deadline, new GoalExpiryEvent(HouseTile, ParentAId, ParentBId));
            reservation.ExpiryTick = deadline;
            reservation.ExpirySeq = seq;
        }

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"BeginBreeding(house={HouseTile.X},{HouseTile.Y} a={ParentAId} b={ParentBId})";
}
