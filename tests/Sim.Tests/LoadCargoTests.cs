using Sim.Core.Bandits;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests;

// M16 Phase 3 — LoadCargoIntent: the mirror of UnloadCargoIntent and the
// bandits' stealing verb. Source ownership is deliberately unchecked
// (raiding economy by design — docs/intent-authorization.md addendum).
public class LoadCargoTests
{
    private static Simulation MakeSim(out GameWorld world)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        return new Simulation(world, seed: 0x10AD);
    }

    [Fact]
    public void Load_FromOwnStockpile_FillsToCapacity()
    {
        var sim = MakeSim(out var world);
        var pile = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(2, 2)));
        pile.Deposit(Resource.Wood, 100);
        var hauler = world.AddUnit(new Unit(1, new TileCoord(2, 2)) { Role = UnitRole.Hauler });

        var outcome = new LoadCargoIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(Resource.Wood, hauler.CargoResource);
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, hauler.CargoAmount);
        Assert.Equal(100 - UnitCargoCatalog.HaulerCapacity, pile.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Load_StructureFirst_GroundPileForRemainder()
    {
        var sim = MakeSim(out var world);
        var pile = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(2, 2)));
        pile.Deposit(Resource.Wood, 3);
        CargoTransfer.DropToGround(world, new TileCoord(2, 2), Resource.Wood, 50);
        var hauler = world.AddUnit(new Unit(1, new TileCoord(2, 2)) { Role = UnitRole.Hauler });

        var outcome = new LoadCargoIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, hauler.CargoAmount);
        Assert.Equal(0, pile.AmountOf(Resource.Wood));
        // 3 came from the stockpile; capacity − 3 came off the ground.
        Assert.Equal(50 - (UnitCargoCatalog.HaulerCapacity - 3),
            world.GroundResources[new TileCoord(2, 2)][Resource.Wood]);
    }

    [Fact]
    public void Load_TopUp_SameResource_Allowed_OtherResource_Rejected()
    {
        var sim = MakeSim(out var world);
        CargoTransfer.DropToGround(world, new TileCoord(2, 2), Resource.Wood, 50);
        var hauler = world.AddUnit(new Unit(1, new TileCoord(2, 2))
        {
            Role = UnitRole.Hauler, CargoResource = Resource.Wood, CargoAmount = 10,
        });

        Assert.True(new LoadCargoIntent(1, Resource.Stone) { PlayerId = 0 }
            .Resolve(sim).IsRejected);
        var outcome = new LoadCargoIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, hauler.CargoAmount);
    }

    [Fact]
    public void Load_ValidationMatrix()
    {
        var sim = MakeSim(out var world);
        CargoTransfer.DropToGround(world, new TileCoord(2, 2), Resource.Wood, 50);
        var unit = world.AddUnit(new Unit(1, new TileCoord(2, 2)) { Role = UnitRole.Hauler });

        // Wrong owner.
        Assert.True(new LoadCargoIntent(1, Resource.Wood) { PlayerId = 7 }.Resolve(sim).IsRejected);
        // No resource named.
        Assert.True(new LoadCargoIntent(1, Resource.None) { PlayerId = 0 }.Resolve(sim).IsRejected);
        // Unknown unit.
        Assert.True(new LoadCargoIntent(99, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsRejected);
        // Busy unit.
        unit.TrySetActivity(Activity.Hauling);
        Assert.True(new LoadCargoIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsRejected);
        unit.TrySetActivity(Activity.Idle);
        // Nothing of the named resource on the tile.
        Assert.True(new LoadCargoIntent(1, Resource.Stone) { PlayerId = 0 }.Resolve(sim).IsRejected);
        // Full cargo.
        unit.CargoResource = Resource.Wood;
        unit.CargoAmount = unit.CargoCapacity;
        Assert.True(new LoadCargoIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Load_FromDormantExtractor_ReArmsProduction()
    {
        // Mirrors HaulIntentTests.Pickup_FromDormantExtractor_ReArmsProduction:
        // freeing buffer space through the LOAD path must hit the same
        // Phase-D hook.
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var campSpec = StructureCatalog.Spec(StructureKind.LumberCamp);
        var camp = new TileCoord(3, campSpec.ClaimRange);
        for (var dy = -campSpec.ClaimRange; dy <= campSpec.ClaimRange; dy++)
            for (var dx = -campSpec.ClaimRange; dx <= campSpec.ClaimRange; dx++)
                grid.SetBiome(new TileCoord(camp.X + dx, camp.Y + dy), Biome.Forest);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var ex = world.AddStructure(new Extractor(StructureKind.LumberCamp, camp));
        ex.Buffer = ex.Spec.BufferCap;
        ex.TickArmed = false;
        var worker = world.AddUnit(new Unit(2, camp) { Role = UnitRole.Lumberjack });
        worker.TrySetActivity(Activity.Working, camp);
        ex.Workers.Add(2);
        var hauler = world.AddUnit(new Unit(1, camp) { Role = UnitRole.Hauler });
        var sim = new Simulation(world, seed: 0x10AD);

        var outcome = new LoadCargoIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, hauler.CargoAmount);
        Assert.Equal(ex.Spec.BufferCap - UnitCargoCatalog.HaulerCapacity, ex.Buffer);
        Assert.True(ex.TickArmed);
    }

    [Fact]
    public void StealLoop_Headline_BanditLoots_Dies_PlayerRecovers()
    {
        // The M16 raid economy end-to-end: a bandit empties a player
        // extractor's buffer with LoadCargo (source ownership unchecked —
        // raiding by design), is hunted down, drops the loot on death, and
        // the player recovers it with the SAME LoadCargo atom.
        var sim = MakeSim(out var world);
        var camp = new TileCoord(5, 5);
        world.Grid.SetBiome(camp, Biome.Forest);
        var ex = world.AddStructure(new Extractor(StructureKind.LumberCamp, camp) { OwnerId = 0 });
        ex.Buffer = 20;
        ex.TickArmed = false;   // unstaffed camp — nothing re-arms

        var bandit = world.AddUnit(new Unit(10, camp)
        {
            Role = UnitRole.Bandit, OwnerId = BanditConstants.OwnerId, BornTick = 0,
        });

        // The steal.
        var steal = new LoadCargoIntent(10, Resource.Wood)
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim);
        Assert.True(steal.IsApplied, steal.Reason);
        Assert.Equal(UnitCargoCatalog.BanditCapacity, bandit.CargoAmount);
        Assert.Equal(20 - UnitCargoCatalog.BanditCapacity, ex.Buffer);

        // The flight — bandit runs; a soldier intercepts on the road tile.
        sim.SubmitIntent(sim.Now, new MoveIntent(10, new TileCoord(9, 5))
            { PlayerId = BanditConstants.OwnerId });
        var soldier = world.AddUnit(new Unit(20, new TileCoord(9, 5))
        {
            Role = UnitRole.Soldier, OwnerId = 0, BornTick = 0,
        });
        sim.Run(until: sim.Now + 20_000);

        // Soldier (30HP/3pwr) beats one bandit (25HP/3pwr); the loot hit
        // the ground where the carrier fell.
        Assert.False(world.Units.ContainsKey(10));
        Assert.True(world.Units.ContainsKey(20));
        var dropTile = world.GroundResources.Keys.Single();
        Assert.Equal(UnitCargoCatalog.BanditCapacity,
            world.GroundResources[dropTile][Resource.Wood]);

        // The recovery — player walks a hauler over and loads it back.
        var hauler = world.AddUnit(new Unit(30, dropTile) { Role = UnitRole.Hauler });
        var recover = new LoadCargoIntent(30, Resource.Wood) { PlayerId = 0 }.Resolve(sim);
        Assert.True(recover.IsApplied, recover.Reason);
        Assert.Equal(UnitCargoCatalog.BanditCapacity, hauler.CargoAmount);
        Assert.False(world.GroundResources.ContainsKey(dropTile));
    }
}
