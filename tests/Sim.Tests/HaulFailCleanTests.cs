using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// A FAILED PICKUP MUST LEAVE THE HAULER USABLE.
//
// HaulPlan is the on-unit anchor that says "this body is mid-haul". Every
// consumer that asks whether a unit is free — the automation driver's
// IsFreeForWork, the selectors — treats a set HaulPlan as busy, and
// deliberately so: a marching hauler reads Activity.Idle between steps, so
// the anchors are the truth rather than the activity flag.
//
// That makes clearing the anchor on EVERY exit path load-bearing. The
// dest-leg failures in HaulPickupEvent (no route, dock razed) already fail
// clean; the SOURCE-leg failures did not. So a hauler that walked to a farm
// whose buffer had emptied went Idle holding a dead anchor and was bricked
// for the rest of the game — invisible to every selector, and permanently
// "busy" to the supply line that named it. One empty pickup retired the unit
// and wedged the order.
//
// Arriving at a dry source is ORDINARY (a farm can empty while the hauler
// walks over), which is exactly why it must not be terminal.
public class HaulFailCleanTests
{
    private static readonly TileCoord Keep = new(8, 8);
    private static readonly TileCoord Store = new(6, 8);

    private static Simulation BuildWorld(int sourceFood)
    {
        var grid = new TileGrid(16, 16, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 50);

        var store = new Stockpile(Store) { OwnerId = 0 };
        if (sourceFood > 0) store.Holdings[Resource.Food] = sourceFood;
        world.AddStructure(store);

        world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
        world.NextUnitId = 2;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 4);
    }

    [Fact]
    public void PickupFromAnEmptySource_LeavesTheHaulerFreeNotBricked()
    {
        // The raw sim contract, with no automation anywhere near it: a manual
        // haul against an empty source must not retire the hauler.
        var sim = BuildWorld(sourceFood: 0);
        var hauler = sim.World.Units[1];

        sim.SubmitIntent(0, new HaulIntent(1, Store, Keep, Resource.Food) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(hauler.HaulPlan);          // the trip started

        sim.Run(2000);                            // walk over, fail the pickup

        Assert.Equal(Store, hauler.Position);
        Assert.Null(hauler.HaulPlan);             // ← the fix: anchor released
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(0, hauler.CargoAmount);

        // And the unit is genuinely reusable: a second haul is accepted.
        sim.World.Structures[Store].As<Stockpile>().Holdings[Resource.Food] = 40;
        sim.SubmitIntent(sim.Now, new HaulIntent(1, Store, Keep, Resource.Food) { PlayerId = 0 });
        sim.Run(sim.Now);
        Assert.NotNull(hauler.HaulPlan);
    }

    [Fact]
    public void SupplyLine_RecoversAfterItsSourceRunsDry()
    {
        // The whole failure the player actually reported: a named supply line
        // that fired once against a source that emptied, and then reported its
        // idle crew as busy forever because the dead anchor was never cleared.
        var sim = BuildWorld(sourceFood: 40);
        var store = (Stockpile)sim.World.Structures[Store];

        var order = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Resource = Resource.Food,
            SourceTile = Store,
            Target = 400,
            CrewMode = CrewMode.Named,
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, 400)),
        };
        order.NamedCrew.Add(1);
        sim.SubmitIntent(0, new SetOrderIntent(order) { PlayerId = 0 });
        sim.Run(0);
        var installed = Assert.Single(sim.World.Orders.Values);

        var journal = new OrderJournal();
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = 60 }, journal);

        // Dispatch the first trip, then empty the source behind it so the
        // pickup fails on arrival — the race that used to brick the hauler.
        sim.Run(0);
        driver.Think(sim, 0);
        sim.Run(0);
        Assert.Equal(JournalOutcome.Fired, journal.Last(installed.OrderId)!.Value.Outcome);
        store.Holdings[Resource.Food] = 0;

        for (long t = 60; t <= 600; t += 60) { sim.Run(t); driver.Think(sim, t); sim.Run(t); }

        // The crew survived the dud trip, and the order reports BLOCKED — the
        // destination is hungry and the source is bare, which is news ("your
        // chain is broken one link upstream"), not the healthy resting state.
        Assert.Null(sim.World.Units[1].HaulPlan);
        Assert.Equal(JournalOutcome.Blocked, journal.Last(installed.OrderId)!.Value.Outcome);
        Assert.Contains("has no Food", journal.Last(installed.OrderId)!.Value.Detail);

        // Restock, and the line picks straight back up — the proof it was
        // waiting rather than wedged.
        store.Holdings[Resource.Food] = 200;
        var before = ((Castle)sim.World.Structures[Keep]).AmountOf(Resource.Food);
        for (long t = 660; t <= 4000; t += 60) { sim.Run(t); driver.Think(sim, t); sim.Run(t); }

        Assert.True(((Castle)sim.World.Structures[Keep]).AmountOf(Resource.Food) > before,
            "the supply line should have resumed delivering once its source refilled");
    }
}

internal static class StructureCastEx
{
    public static T As<T>(this Structure s) where T : Structure => (T)s;
}
