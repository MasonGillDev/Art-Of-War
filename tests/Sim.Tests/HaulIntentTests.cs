using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

public class HaulIntentTests
{
    private static Simulation MakeSim(int w = 8, int h = 8)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        return new Simulation(world, seed: 1);
    }

    private static Unit AddHauler(Simulation sim, int id, TileCoord pos, int capacity = 5)
    {
        var u = new Unit(id, pos) { Role = UnitRole.Hauler, CargoCapacity = capacity };
        sim.World.AddUnit(u);
        return u;
    }

    // -------- HaulIntent submission validation --------

    [Fact]
    public void HaulIntent_HappyPath_CastleToStockpile()
    {
        var sim = MakeSim();
        var castle = sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(5, 0)));
        castle.Deposit(Resource.Wood, 20);
        AddHauler(sim, 1, new TileCoord(0, 0), capacity: 5);

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(5, 0), Resource.Wood));
        sim.Run();

        Assert.Equal(15, castle.AmountOf(Resource.Wood));
        Assert.Equal(5, stockpile.AmountOf(Resource.Wood));
        var hauler = sim.World.Units[1];
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(new TileCoord(5, 0), hauler.Position);
        Assert.Equal(0, hauler.CargoAmount);
        Assert.Equal(Resource.None, hauler.CargoResource);
    }

    [Fact]
    public void HaulIntent_HaulerAlreadyAtSource_SkipsFirstMoveLeg()
    {
        var sim = MakeSim();
        var castle = sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        castle.Deposit(Resource.Wood, 10);
        AddHauler(sim, 1, new TileCoord(0, 0), capacity: 5);

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood));
        sim.Run();

        // No move-to-source leg means total ticks = move-to-dest only.
        // 3 tiles at Grassland (cost 10) = 30 ticks.
        Assert.Equal(30, sim.Now);
        Assert.Equal(5, stockpile.AmountOf(Resource.Wood));
    }

    [Fact]
    public void HaulIntent_HaulerNotIdle_Rejected()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        AddHauler(sim, 1, new TileCoord(0, 0));
        sim.World.Units[1].TrySetActivity(Activity.Hauling); // pre-busy

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void HaulIntent_NoSourceStructure_Rejected()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        AddHauler(sim, 1, new TileCoord(0, 0));

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
    }

    [Fact]
    public void HaulIntent_NoDestStructure_Rejected()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        AddHauler(sim, 1, new TileCoord(0, 0));

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void HaulIntent_ResourceNone_Rejected()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        AddHauler(sim, 1, new TileCoord(0, 0));

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.None));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    // -------- HaulPickupEvent fail-clean paths --------

    [Fact]
    public void Pickup_SourceEmpty_AbortsCleanly()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(new TileCoord(0, 0))); // empty
        sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        AddHauler(sim, 1, new TileCoord(0, 0));

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood));
        sim.Run();

        var hauler = sim.World.Units[1];
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(new TileCoord(0, 0), hauler.Position); // never moved past source
        Assert.Equal(0, hauler.CargoAmount);
        Assert.Contains(sim.ResolvedLog.OfType<HaulPickupEvent>(),
            e => e.Outcome.IsRejected);
        // No HaulDepositEvent should ever have been scheduled.
        Assert.Empty(sim.ResolvedLog.OfType<HaulDepositEvent>());
    }

    // -------- Cross-system hooks --------

    [Fact]
    public void Pickup_FromDormantExtractor_ReArmsProduction()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var camp = new TileCoord(3, 0);
        grid.SetBiome(camp, Biome.Forest);
        var world = new GameWorld(grid);
        var ex = world.AddStructure(new Extractor(StructureKind.LumberCamp, camp));
        ex.Buffer = ex.Spec.BufferCap;
        ex.TickArmed = false;
        // ArmIfDormant only arms if there are workers; pre-staff one Lumberjack.
        var worker = world.AddUnit(new Unit(2, camp) { Role = UnitRole.Lumberjack });
        worker.TrySetActivity(Activity.Working, camp);
        ex.Workers.Add(2);

        world.AddStructure(new Stockpile(new TileCoord(0, 0)));
        var sim = new Simulation(world, seed: 1);
        AddHauler(sim, 1, new TileCoord(0, 0), capacity: 5);

        sim.SubmitIntent(0, new HaulIntent(1, camp, new TileCoord(0, 0), Resource.Wood));
        sim.Run();

        // Hauler took 5 from buffer; production may have refilled some on the
        // return trip. Either way, the camp is no longer dormant — TickArmed
        // flipped, or it refilled to cap and re-dormant'd via that path.
        Assert.True(ex.TickArmed || ex.BufferFull());
        Assert.Equal(5, ((Stockpile)sim.World.Structures[new TileCoord(0, 0)]).AmountOf(Resource.Wood));
        // The buffer was lowered (or refilled past) at some point — verify
        // a re-arming ProductionTickEvent was scheduled and ran.
        Assert.Contains(sim.ResolvedLog.OfType<Sim.Core.Logistics.ProductionTickEvent>(),
            e => e.At > 0);
    }

    [Fact]
    public void Deposit_IntoConstructionSite_TriggersBuildStart()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var siteTile = new TileCoord(4, 0);
        var world = new GameWorld(grid);
        var castle = world.AddStructure(new Castle(new TileCoord(0, 0)));
        var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
        var sim = new Simulation(world, seed: 1);

        // Provide enough Wood at castle; spawn a Builder pre-Building on the site tile.
        var spec = StructureCatalog.Spec(StructureKind.Stockpile);
        var woodNeeded = spec.BuildCost[Resource.Wood];
        castle.Deposit(Resource.Wood, woodNeeded);

        var builder = world.AddUnit(new Unit(2, siteTile) { Role = UnitRole.Builder });
        builder.TrySetActivity(Activity.Building, siteTile);
        AddHauler(sim, 1, new TileCoord(0, 0), capacity: woodNeeded);

        Assert.False(site.IsActive);

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), siteTile, Resource.Wood));
        sim.Run();

        // Deposit triggered StartOrResume → build ran → Stockpile exists.
        Assert.IsType<Stockpile>(sim.World.Structures[siteTile]);
    }

    [Fact]
    public void Deposit_OverflowStaysOnHauler()
    {
        var sim = MakeSim();
        var castle = sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        // Stockpile with capacity 2 — tiny enough to overflow a 5-cargo haul.
        // StorageStructure.Capacity comes from the catalog, so we can't shrink
        // a real Stockpile. Use the ConstructionSite path instead: target
        // requires 3 Wood, hauler brings 5.
        var siteTile = new TileCoord(3, 0);
        sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
        castle.Deposit(Resource.Wood, 5);
        AddHauler(sim, 1, new TileCoord(0, 0), capacity: 5);
        var spec = StructureCatalog.Spec(StructureKind.Stockpile);
        var requirement = spec.BuildCost[Resource.Wood]; // 20

        // Pre-deposit enough to leave only 1 outstanding when hauler arrives.
        var preDep = (ConstructionSite)sim.World.Structures[siteTile];
        preDep.Deposit(Resource.Wood, requirement - 1);

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), siteTile, Resource.Wood));
        sim.Run();

        var hauler = sim.World.Units[1];
        Assert.Equal(Activity.Idle, hauler.Activity);
        // Hauler picked up 5, deposited 1 (the outstanding need), still holds 4.
        Assert.Equal(4, hauler.CargoAmount);
        Assert.Equal(Resource.Wood, hauler.CargoResource);
    }

    // -------- determinism + snapshot --------

    [Fact]
    public void TwinRun_FullHaul_HashesMatch()
    {
        Simulation Build()
        {
            var sim = MakeSim();
            var castle = sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
            sim.World.AddStructure(new Stockpile(new TileCoord(5, 5)));
            castle.Deposit(Resource.Wood, 25);
            castle.Deposit(Resource.Stone, 10);
            AddHauler(sim, 1, new TileCoord(0, 0), capacity: 5);
            sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(5, 5), Resource.Wood));
            return sim;
        }

        var a = Build();
        var b = Build();
        a.Run();
        b.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void PostHaulIdleState_RoundTripsThroughSnapshot()
    {
        var sim = MakeSim();
        var castle = sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        castle.Deposit(Resource.Wood, 10);
        AddHauler(sim, 1, new TileCoord(0, 0), capacity: 5);

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood));
        sim.Run(); // runs to completion → hauler Idle, queue empty

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
