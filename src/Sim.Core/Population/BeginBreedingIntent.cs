using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Population;

// M8 Phase D — kick off a breeding cycle at a House.
//
// Validations (all checked once, here, never again):
//   1. House exists at HouseTile, is a House, owned by PlayerId.
//   2. House is vacant (Occupation == null).
//   3. Both parents exist, both owned by PlayerId.
//   4. Both parents are physically on the house tile.
//   5. Both parents are Idle.
//   6. Both parents are in the fertility window (CanBreed).
//   7. ParentA != ParentB.
//   8. House holds >= config.BirthFoodCost food.
//   9. Neither parent is already a breeding parent in another house.
//
// On success: occupy both parents (Activity.Working, Assignment = house);
// withdraw food; set Occupation; schedule BirthEvent at sim.Now +
// GestationTicks.
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

        if (!world.Units.TryGetValue(ParentAId, out var a))
            return IntentOutcome.Reject($"ParentA {ParentAId} does not exist");
        if (!world.Units.TryGetValue(ParentBId, out var b))
            return IntentOutcome.Reject($"ParentB {ParentBId} does not exist");
        if (a.OwnerId != PlayerId || b.OwnerId != PlayerId)
            return IntentOutcome.Reject("both parents must be owned by the player");
        if (a.Position != HouseTile || b.Position != HouseTile)
            return IntentOutcome.Reject("both parents must be on the house tile");
        if (a.Activity != Activity.Idle || b.Activity != Activity.Idle)
            return IntentOutcome.Reject("both parents must be Idle");

        var cfg = world.PopulationConfig;
        if (!Population.CanBreed(a, sim.Now, cfg) || !Population.CanBreed(b, sim.Now, cfg))
            return IntentOutcome.Reject($"both parents must be in fertility window [{cfg.MinFertileAge}..{cfg.MaxFertileAge}]");

        if (house.AmountOf(Resource.Food) < cfg.BirthFoodCost)
            return IntentOutcome.Reject($"house has insufficient food (need {cfg.BirthFoodCost})");

        // Neither parent already in another breeding cycle.
        if (Population.GetActiveBreedingFor(world, ParentAId) is not null)
            return IntentOutcome.Reject($"ParentA {ParentAId} already breeding");
        if (Population.GetActiveBreedingFor(world, ParentBId) is not null)
            return IntentOutcome.Reject($"ParentB {ParentBId} already breeding");

        // All clear. Commit.
        house.Withdraw(Resource.Food, cfg.BirthFoodCost);
        a.TrySetActivity(Activity.Working, HouseTile);
        b.TrySetActivity(Activity.Working, HouseTile);

        var birthTick = sim.Now + cfg.GestationTicks;
        var ev = new BirthEvent(HouseTile);
        var seq = sim.Schedule(birthTick, ev);
        house.Occupation = new BreedingOccupation
        {
            ParentAId = ParentAId,
            ParentBId = ParentBId,
            BirthTick = birthTick,
            BirthSeq = seq,
        };
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"BeginBreeding(house={HouseTile.X},{HouseTile.Y} a={ParentAId} b={ParentBId})";
}
