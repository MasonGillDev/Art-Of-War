using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// A STOCK PREDICATE ON FOOD AT A FOOD HOME MUST READ THE EFFECTIVE LEVEL.
//
// Food consumption is lazily caught up (architecture §2.5): a food home's
// Holdings[Food] is decremented only at rate-changing events, so between
// them it is STALE — it can still read the world's starting value while the
// residents have been eating for days, and during famine the unpaid debt is
// not in Holdings at all. FoodConsumption.CurrentLevel is the live number,
// and it is what the realm panel and the AI brains have always used.
//
// The substrate originally read raw Holdings, which meant a player's supply
// line sat still while the castle it was meant to feed starved (observed
// live: the castle panel read 200 while the realm read 88), and the AI —
// which reads CurrentLevel — saw the truth the player's automation did not.
// That is a fairness break as well as a bug.
public class SubstrateFoodLevelTests
{
    private static readonly TileCoord Keep = new(5, 5);

    // A castle with `food` banked and `mouths` residents eating from it.
    private static Simulation BuildWorld(int food, int mouths)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, food);

        for (var i = 1; i <= mouths; i++)
            world.AddUnit(new Unit(i, Keep) { Role = UnitRole.Farmer, OwnerId = 0 });
        world.NextUnitId = mouths + 1;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 11);
    }

    [Fact]
    public void StoredAmount_AtAFoodHome_ReadsTheLiveLevelNotStaleHoldings()
    {
        var sim = BuildWorld(food: 200, mouths: 10);
        var castle = (Castle)sim.World.Structures[Keep];

        // Let the colony eat for a while WITHOUT any event that would force a
        // catch-up. Holdings stays where it started; the effective level falls.
        var later = 20 * Sim.Core.Time.Hour;
        sim.Run(later);

        var raw = castle.AmountOf(Resource.Food);
        var live = FoodConsumption.CurrentLevel(castle, sim.World, later);

        Assert.True(live < raw,
            $"expected the live level ({live}) to trail stale holdings ({raw}) after 20h of eating");
        Assert.Equal(live, PredicateEvaluator.StoredAmount(sim.World, Keep, Resource.Food, later));
    }

    [Fact]
    public void SupplyLineToTheCastle_FiresOnTheLiveLevel_NotStaleHoldings()
    {
        var sim = BuildWorld(food: 200, mouths: 10);
        var castle = (Castle)sim.World.Structures[Keep];
        var later = 20 * Sim.Core.Time.Hour;
        sim.Run(later);

        // A threshold BETWEEN the two numbers is the whole point: stale
        // holdings say "full, rest", the live level says "haul now". Guard the
        // arrangement so a tuning change can't silently make this vacuous.
        var raw = castle.AmountOf(Resource.Food);
        var live = FoodConsumption.CurrentLevel(castle, sim.World, later);
        var threshold = (raw + live) / 2;
        Assert.True(live < threshold && threshold < raw,
            $"test needs live ({live}) < threshold ({threshold}) < holdings ({raw})");

        var order = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Resource = Resource.Food,
            SourceTile = new TileCoord(2, 2),
            Target = threshold,
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, threshold)),
        };

        var visible = new HashSet<TileCoord>(sim.World.Explored[0]);
        Assert.True(PredicateEvaluator.IsMet(sim.World, order, visible, later),
            "the castle is below the threshold in real terms, so the line must want to haul");
    }

    [Fact]
    public void BreedGate_AtAHungryHome_ReadsTheLiveLarder()
    {
        // The mirror-image failure: Breed is gated on StockAtLeast, so stale
        // holdings make a starving house look well-fed enough to have a child.
        var sim = BuildWorld(food: 200, mouths: 10);
        var castle = (Castle)sim.World.Structures[Keep];
        var later = 20 * Sim.Core.Time.Hour;
        sim.Run(later);

        var raw = castle.AmountOf(Resource.Food);
        var live = FoodConsumption.CurrentLevel(castle, sim.World, later);
        var threshold = (raw + live) / 2;

        var order = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Breed,
            Trigger = Trigger.When(Predicate.StockAtLeast(Keep, Resource.Food, threshold)),
        };

        var visible = new HashSet<TileCoord>(sim.World.Explored[0]);
        Assert.False(PredicateEvaluator.IsMet(sim.World, order, visible, later),
            "the larder is below the floor in real terms, so breeding must NOT be gated open");
    }
}
