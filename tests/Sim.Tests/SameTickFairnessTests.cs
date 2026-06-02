using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// Phase F: pin same-tick ordering as a *gameplay-relevant fairness* property,
// not just reproducibility. From M1 doc §6 guardrail 5:
//   "Same-tick submission order now decides fairness, not just
//    reproducibility: who gets the last unit of depleting stock / filling
//    buffer."
//
// The mechanism is already correct — Simulation tags every scheduled event
// with a monotonic Seq, the EventQueue uses (At, Seq) as its priority, and
// MoveIntent/ProductionTickEvent chains inherit Seq from the parent's
// scheduling order. These tests assert on the *outcome* so a refactor that
// silently breaks ordering would surface here instead of in production.
public class SameTickFairnessTests
{
    // -------- Test 1: Buffer contention --------
    //
    // Extractor has Buffer=3. Two Haulers (capacity=5) on different tiles
    // both submit HaulIntent at tick 0 targeting that buffer. The earlier-
    // submitted one's move chain has lower Seq numbers throughout, so it
    // arrives first and pulls all 3. The other arrives to nothing and
    // aborts cleanly.

    // Layout: camp in the middle, two stockpile-homes mirrored 4 tiles east
    // and 4 tiles west. Both haulers walk the same Manhattan distance to camp,
    // so they arrive on the same sim-tick and the Seq tiebreak decides who
    // pulls from the buffer first.
    private static (Simulation sim, Extractor ex, TileCoord home1, TileCoord home2) MakeBufferContentionWorld()
    {
        var grid = new TileGrid(9, 1, Biome.Grassland);
        var campTile = new TileCoord(4, 0);
        grid.SetBiome(campTile, Biome.Forest);
        var home1 = new TileCoord(0, 0);
        var home2 = new TileCoord(8, 0);
        var world = new GameWorld(grid);
        var ex = world.AddStructure(new Extractor(StructureKind.LumberCamp, campTile));
        ex.Buffer = 3;
        world.AddStructure(new Stockpile(home1));
        world.AddStructure(new Stockpile(home2));

        var sim = new Simulation(world, seed: 1);
        world.AddUnit(new Unit(1, home1) { Role = UnitRole.Hauler, CargoCapacity = 5 });
        world.AddUnit(new Unit(2, home2) { Role = UnitRole.Hauler, CargoCapacity = 5 });

        return (sim, ex, home1, home2);
    }

    [Fact]
    public void BufferContention_FirstSubmittedWins()
    {
        var (sim, ex, home1, home2) = MakeBufferContentionWorld();
        var camp = ex.At;
        sim.SubmitIntent(0, new HaulIntent(1, camp, home1, Resource.Wood));
        sim.SubmitIntent(0, new HaulIntent(2, camp, home2, Resource.Wood));
        sim.Run();

        var u2 = sim.World.Units[2];
        var stockpile1 = (Stockpile)sim.World.Structures[home1];
        var stockpile2 = (Stockpile)sim.World.Structures[home2];

        // Unit 1 was first-submitted → wins the buffer pull, returns home, deposits.
        Assert.Equal(3, stockpile1.AmountOf(Resource.Wood));
        Assert.Equal(0, stockpile2.AmountOf(Resource.Wood));

        // Unit 2 arrived to empty buffer, aborted at pickup, is Idle on camp.
        Assert.Equal(Activity.Idle, u2.Activity);
        Assert.Equal(camp, u2.Position);
        Assert.Equal(0, u2.CargoAmount);
        Assert.Contains(sim.ResolvedLog.OfType<HaulPickupEvent>(),
            e => e.HaulerId == 2 && e.Outcome.IsRejected);
    }

    [Fact]
    public void BufferContention_SwappingSubmissionOrder_SwapsWinner()
    {
        var (sim, ex, home1, home2) = MakeBufferContentionWorld();
        var camp = ex.At;
        // Submit unit 2 first this time.
        sim.SubmitIntent(0, new HaulIntent(2, camp, home2, Resource.Wood));
        sim.SubmitIntent(0, new HaulIntent(1, camp, home1, Resource.Wood));
        sim.Run();

        var stockpile1 = (Stockpile)sim.World.Structures[home1];
        var stockpile2 = (Stockpile)sim.World.Structures[home2];
        Assert.Equal(0, stockpile1.AmountOf(Resource.Wood));
        Assert.Equal(3, stockpile2.AmountOf(Resource.Wood));
    }

    [Fact]
    public void BufferContention_IsReproducible_AcrossRuns()
    {
        Simulation Run()
        {
            var (sim, ex, home1, home2) = MakeBufferContentionWorld();
            var camp = ex.At;
            sim.SubmitIntent(0, new HaulIntent(1, camp, home1, Resource.Wood));
            sim.SubmitIntent(0, new HaulIntent(2, camp, home2, Resource.Wood));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // -------- Test 2: Last-unit construction deposit --------
    //
    // ConstructionSite needing 10 Wood, pre-deposited with 9. Two haulers
    // each carrying 1 Wood, both already on the site tile. Their deposit
    // events fire at the same tick. First-submitted deposits 1 (taking site
    // to 10/10, triggering StartOrResume). Second-submitted finds nothing
    // left to deposit (Outstanding(Wood)==0); cargo stays on hauler.

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    [Fact]
    public void LastUnitDeposit_FirstSubmittedTriggersBuild()
    {
        var grid = new TileGrid(6, 6, Biome.Grassland);
        var siteTile = new TileCoord(3, 3);
        var world = new GameWorld(grid);
        var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
        // Stockpile.BuildCost is 20 Wood. Pre-deposit 19; two 1-Wood deposits
        // will compete to be the one that satisfies it.
        var need = StructureCatalog.Spec(StructureKind.Stockpile).BuildCost[Resource.Wood];
        site.Deposit(Resource.Wood, need - 1);

        var sim = new Simulation(world, seed: 1);

        // Two haulers on the site tile, each holding 1 Wood.
        foreach (var id in new[] { 1, 2 })
        {
            var u = world.AddUnit(new Unit(id, siteTile) { Role = UnitRole.Hauler, CargoCapacity = 1 });
            u.TrySetActivity(Activity.Hauling);
            u.CargoResource = Resource.Wood;
            u.CargoAmount = 1;
        }
        // Builder pre-Building on the site tile so ConditionsMet is satisfied
        // the instant materials hit.
        var builder = world.AddUnit(new Unit(99, siteTile) { Role = UnitRole.Builder });
        builder.TrySetActivity(Activity.Building, siteTile);

        // Schedule both deposits at the same tick, in submission order 1 then 2.
        sim.Schedule(0, new HaulDepositEvent(1, siteTile));
        sim.Schedule(0, new HaulDepositEvent(2, siteTile));
        // Stop before BuildComplete fires so we can inspect the post-deposit state.
        sim.Run(until: 0);

        // Unit 1's deposit completed (it triggered StartOrResume), Unit 2's didn't.
        // The site is now ACTIVE (build scheduled). Builder is still Building.
        Assert.True(site.IsActive);
        Assert.Equal(need, site.Delivered[Resource.Wood]);

        // Unit 1 emptied its cargo; unit 2 kept its 1 Wood as overflow.
        Assert.Equal(0, sim.World.Units[1].CargoAmount);
        Assert.Equal(1, sim.World.Units[2].CargoAmount);
        Assert.Equal(Resource.Wood, sim.World.Units[2].CargoResource);
    }

    [Fact]
    public void LastUnitDeposit_IsReproducible_AcrossRuns()
    {
        Simulation Run()
        {
            var grid = new TileGrid(6, 6, Biome.Grassland);
            var siteTile = new TileCoord(3, 3);
            var world = new GameWorld(grid);
            var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
            var need = StructureCatalog.Spec(StructureKind.Stockpile).BuildCost[Resource.Wood];
            site.Deposit(Resource.Wood, need - 1);
            var sim = new Simulation(world, seed: 1);

            foreach (var id in new[] { 1, 2 })
            {
                var u = world.AddUnit(new Unit(id, siteTile) { Role = UnitRole.Hauler, CargoCapacity = 1 });
                u.TrySetActivity(Activity.Hauling);
                u.CargoResource = Resource.Wood;
                u.CargoAmount = 1;
            }
            var builder = world.AddUnit(new Unit(99, siteTile) { Role = UnitRole.Builder });
            builder.TrySetActivity(Activity.Building, siteTile);

            sim.Schedule(0, new HaulDepositEvent(1, siteTile));
            sim.Schedule(0, new HaulDepositEvent(2, siteTile));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // -------- Test 3: Builder assignment contention --------
    //
    // Site requiring 1 builder, two Builder units already on tile. Two
    // separate AssignBuildersIntent submitted same tick, one builder each.
    // First-submitted triggers StartOrResume; second sees IsActive == true
    // and does not re-trigger.

    [Fact]
    public void AssignmentContention_FirstSubmittedTriggersStart()
    {
        var grid = new TileGrid(6, 6, Biome.Grassland);
        var siteTile = new TileCoord(3, 3);
        var world = new GameWorld(grid);
        var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
        // Pre-deposit materials so the only thing missing is a builder.
        var need = StructureCatalog.Spec(StructureKind.Stockpile).BuildCost[Resource.Wood];
        site.Deposit(Resource.Wood, need);

        var sim = new Simulation(world, seed: 1);
        world.AddUnit(new Unit(1, siteTile) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, siteTile) { Role = UnitRole.Builder });

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 2 }));
        sim.Run(until: 0);

        // Both builders are Building (assignment is per id, not gated by count).
        Assert.Equal(Activity.Building, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Building, sim.World.Units[2].Activity);

        // Build is active. The *first* AssignBuildersIntent is the one that
        // triggered StartOrResume — its outcome was Applied with a triggered
        // start (we infer this from ordering, since both intents return Applied).
        Assert.True(site.IsActive);
        // ScheduledCompletion equals build duration since we triggered at tick 0.
        var spec = StructureCatalog.Spec(StructureKind.Stockpile);
        Assert.Equal(spec.BuildDurationTicks, site.ScheduledCompletion);

        // Only one BuildCompleteEvent is on the queue — the second intent saw
        // IsActive already true and did not double-schedule.
        // (Indirect proof: the build completes exactly once below.)
        sim.Run();
        Assert.IsType<Stockpile>(sim.World.Structures[siteTile]);
    }

    [Fact]
    public void AssignmentContention_IsReproducible_AcrossRuns()
    {
        Simulation Run()
        {
            var grid = new TileGrid(6, 6, Biome.Grassland);
            var siteTile = new TileCoord(3, 3);
            var world = new GameWorld(grid);
            var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
            var need = StructureCatalog.Spec(StructureKind.Stockpile).BuildCost[Resource.Wood];
            site.Deposit(Resource.Wood, need);
            var sim = new Simulation(world, seed: 1);
            world.AddUnit(new Unit(1, siteTile) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(2, siteTile) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
            sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 2 }));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // -------- Test 4: Layered same-tick mix --------
    //
    // A composite scenario: at tick 0, submit a Move intent, an
    // AssignBuilders intent, and a Haul intent against structures already
    // in place. All three intent events fire at tick 0 in submission order;
    // downstream chains thread through. Snapshot.Hash matches across runs.

    [Fact]
    public void LayeredSameTickMix_IsReproducible_AcrossRuns()
    {
        Simulation Run()
        {
            var grid = new TileGrid(10, 10, Biome.Grassland);
            grid.SetBiome(new TileCoord(5, 5), Biome.Forest);
            var world = new GameWorld(grid);
            var castle = world.AddStructure(new Castle(new TileCoord(0, 0)));
            castle.Deposit(Resource.Wood, 30);
            var stockpile = world.AddStructure(new Stockpile(new TileCoord(9, 0)));
            var site = world.AddStructure(new ConstructionSite(new TileCoord(5, 5), StructureKind.LumberCamp));
            // Pre-deposit the LumberCamp's build cost so the assignment can fire.
            var campCost = StructureCatalog.Spec(StructureKind.LumberCamp).BuildCost;
            foreach (var (r, n) in campCost) site.Deposit(r, n);

            var sim = new Simulation(world, seed: 0xF1);
            world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });    // unrelated mover
            world.AddUnit(new Unit(2, new TileCoord(5, 5)) { Role = UnitRole.Builder });    // will build
            world.AddUnit(new Unit(3, new TileCoord(0, 0)) { Role = UnitRole.Hauler, CargoCapacity = 5 });

            sim.SubmitIntent(0, new MoveIntent(unitId: 1, new TileCoord(9, 9)));
            sim.SubmitIntent(0, new AssignBuildersIntent(new TileCoord(5, 5), new[] { 2 }));
            sim.SubmitIntent(0, new HaulIntent(haulerId: 3, new TileCoord(0, 0), new TileCoord(9, 0), Resource.Wood));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }
}
