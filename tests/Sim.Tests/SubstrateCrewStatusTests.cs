using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// WHAT "SHORT OF HANDS" IS ALLOWED TO MEAN.
//
// NoCrew is the dashboard's ALARM state: the one row colour that asks the
// player to do something (make more people, or raise this order's priority).
// It therefore has to mean exactly "this order wants to work and there is
// nobody to do it" — never "my crew is busy doing this very job".
//
// The original haul recipe reported NoCrew ("crew busy") on every think
// between trips, because a hand mid-haul is not a FREE hand. A one-hauler
// supply line is mid-trip most of its life, so a perfectly healthy line
// screamed "short of hands" almost permanently — and an alarm that is always
// on is an alarm the player learns to ignore.
//
// In-flight work is INFLIGHT — its own outcome, not Waiting-with-a-detail,
// because the client renders outcomes 1:1 and must never guess a state from
// the detail string. NoCrew is reserved for a genuinely empty pool.
public class SubstrateCrewStatusTests
{
    private static readonly TileCoord Keep = new(5, 5);
    private static readonly TileCoord Farm = new(2, 2);

    private static Simulation BuildWorld(int haulers, int castleFood = 10)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, castleFood);

        var farm = new Extractor(StructureKind.Farm, Farm) { OwnerId = 0 };
        farm.Buffer = 40;
        farm.TickArmed = false;              // fixed stock; this is a status test
        world.AddStructure(farm);

        for (var i = 1; i <= haulers; i++)
            world.AddUnit(new Unit(i, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
        world.NextUnitId = haulers + 1;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 7);
    }

    private static Order SupplyLine(CrewMode crew, params int[] named)
    {
        var order = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Resource = Resource.Food,
            SourceTile = Farm,
            Target = 100,
            CrewMode = crew,
            Selector = Selector.OfRole(UnitRole.Hauler, Keep, radius: 0),
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, 100)),
        };
        foreach (var id in named) order.NamedCrew.Add(id);
        return order;
    }

    /// Drive the driver for `until` ticks and collect every outcome seen.
    private static (List<JournalOutcome> seen, OrderJournal journal) Run(
        Simulation sim, Order definition, long until = 600, long period = 60)
    {
        sim.SubmitIntent(0, new SetOrderIntent(definition) { PlayerId = 0 });
        sim.Run(0);
        var installed = Assert.Single(sim.World.Orders.Values);

        var journal = new OrderJournal();
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = period }, journal);
        var seen = new List<JournalOutcome>();
        for (long t = 0; t <= until; t += period)
        {
            sim.Run(t);
            driver.Think(sim, t);
            if (journal.Last(installed.OrderId) is { } e) seen.Add(e.Outcome);
        }
        return (seen, journal);
    }

    [Fact]
    public void NamedCrewMidHaul_ReportsWorkInFlight_NotShortOfHands()
    {
        var sim = BuildWorld(haulers: 1);
        var (seen, _) = Run(sim, SupplyLine(CrewMode.Named, 1));

        Assert.Contains(JournalOutcome.Fired, seen);
        Assert.Contains(JournalOutcome.InFlight, seen);   // mid-trip thinks report progress
        Assert.DoesNotContain(JournalOutcome.NoCrew, seen);

        // And it really did the job — the alarm was never the point.
        var castle = (Castle)sim.World.Structures[Keep];
        Assert.True(castle.AmountOf(Resource.Food) > 10,
            "the supply line should have delivered food while 'busy'");
    }

    [Fact]
    public void PullLineWithAnEmptyPool_StillReportsShortOfHands()
    {
        // The alarm must still fire when it is TRUE: a line that wants to
        // work with nobody to send is exactly the case the player must see.
        var sim = BuildWorld(haulers: 0);
        var (seen, journal) = Run(sim, SupplyLine(CrewMode.Pull), until: 180);

        Assert.Contains(JournalOutcome.NoCrew, seen);
        Assert.DoesNotContain(JournalOutcome.Fired, seen);

        var order = Assert.Single(sim.World.Orders.Values);
        Assert.Equal("no free hauler in reach", journal.Last(order.OrderId)!.Value.Detail);
    }

    [Fact]
    public void DeadNamedCrew_StillReportsNoCrew_AndWindsTheOrderDown()
    {
        // Law 4: kill the crew and the order goes dark. This is the one case
        // that legitimately walks toward auto-disable, because a named crew
        // is standing membership no pull will refill.
        var sim = BuildWorld(haulers: 1);
        sim.SubmitIntent(0, new SetOrderIntent(SupplyLine(CrewMode.Named, 1)) { PlayerId = 0 });
        sim.Run(0);
        var order = Assert.Single(sim.World.Orders.Values);

        sim.World.Units.Remove(1);   // the crew falls

        var journal = new OrderJournal();
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = 60 }, journal);
        sim.Run(60);
        driver.Think(sim, 60);
        sim.Run(60);   // the driver's status intent resolves on the next run

        var last = journal.Last(order.OrderId)!.Value;
        Assert.Equal(JournalOutcome.NoCrew, last.Outcome);
        Assert.Equal("crew is dead", last.Detail);
        Assert.True(order.RetryCount > 0, "a dead crew must burn budget toward standing the order down");
    }
}
