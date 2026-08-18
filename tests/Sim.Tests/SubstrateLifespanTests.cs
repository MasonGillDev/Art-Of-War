using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// THE SUBSTRATE MUST WORK ON UNITS THAT WILL SOMEDAY DIE.
//
// On the real server every unit carries a pre-rolled Unit.DeathTick from
// genesis — the SCHEDULED death from old age, not a tombstone. Actually-dead
// units are REMOVED from world.Units by every death path, so presence in the
// table is the one true liveness test.
//
// The first cut of the substrate read "DeathTick is not null" as "dead".
// Hand-built test worlds skip the lifespan roll, so every test passed — and
// on the real server every named crew read as a corpse, every selector
// returned nobody, and every role/population count read zero. The entire
// automation layer was inert in real games and alive only in unit tests.
//
// This fixture reproduces the genesis condition (every unit has a future
// DeathTick) and pins each consumer that got it wrong. If a new liveness
// filter ever creeps back in, these go red while the hand-built fixtures
// stay green — exactly the gap that hid the bug.
public class SubstrateLifespanTests
{
    private static readonly TileCoord Keep = new(5, 5);
    private static readonly TileCoord Store = new(2, 2);

    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 10);

        var store = new Stockpile(Store) { OwnerId = 0 };
        store.Holdings[Resource.Food] = 200;
        world.AddStructure(store);

        // GENESIS-LIKE UNITS: adults with a lifespan already rolled, exactly
        // as the server's spec-aware Simulation ctor leaves them.
        var cfg = new Sim.Core.Population.PopulationConfig();
        for (var i = 1; i <= 3; i++)
        {
            world.AddUnit(new Unit(i, new TileCoord(4 + i, 5))
            {
                Role = UnitRole.Hauler,
                OwnerId = 0,
                BornTick = -25 * cfg.TicksPerYear,          // age 25
                DeathTick = 60L * cfg.TicksPerYear,          // dies of old age... someday
                DeathSeq = 1,
            });
        }
        world.NextUnitId = 4;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 21);
    }

    [Fact]
    public void AUnitWithAScheduledLifespan_IsStillDormant()
    {
        var sim = BuildWorld();
        Assert.True(ClaimLedger.IsDormant(sim.World, sim.World.Units[1]),
            "an idle unit with a pre-rolled DeathTick is alive and available");
    }

    [Fact]
    public void Selectors_StillFindUnitsWithAScheduledLifespan()
    {
        var sim = BuildWorld();
        var order = new Order { OwnerId = 0 };
        var pick = SelectorResolver.First(
            sim.World, order, Selector.OfRole(UnitRole.Hauler, Keep, radius: 0), now: 0, excluded: null);
        Assert.NotNull(pick);
    }

    [Fact]
    public void RoleAndPopulationCounts_CountUnitsWithAScheduledLifespan()
    {
        var sim = BuildWorld();
        var order = new Order { OwnerId = 0 };
        var visible = new HashSet<TileCoord>(sim.World.Explored[0]);

        Assert.True(PredicateEvaluator.IsMet(sim.World, order,
            Predicate.RoleCountAtLeast(UnitRole.Hauler, 3), visible, now: 0),
            "three living haulers must count as three");
        Assert.True(PredicateEvaluator.IsMet(sim.World, order,
            Predicate.PopulationAtLeast(3), visible, now: 0),
            "three living units must count as a population of three");
    }

    [Fact]
    public void NamedSupplyLine_FiresWithACrewThatHasAScheduledLifespan()
    {
        // The exact live failure: a named supply line whose whole crew read
        // as dead, reporting "crew is on the trip" forever while the hauler
        // stood idle and the order never fired once.
        var sim = BuildWorld();

        var order = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Resource = Resource.Food,
            SourceTile = Store,
            Target = 100,
            CrewMode = CrewMode.Named,
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, 100)),
        };
        order.NamedCrew.Add(1);
        sim.SubmitIntent(0, new SetOrderIntent(order) { PlayerId = 0 });
        sim.Run(0);
        var installed = Assert.Single(sim.World.Orders.Values);

        var journal = new OrderJournal();
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = 60 }, journal);
        sim.Run(0);
        driver.Think(sim, 0);
        sim.Run(0);

        Assert.Equal(JournalOutcome.Fired, journal.Last(installed.OrderId)!.Value.Outcome);
        Assert.NotNull(sim.World.Units[1].HaulPlan);   // the trip genuinely began
    }
}
