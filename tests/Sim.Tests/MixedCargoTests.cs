using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M36 Phase A (docs/hauling-queue-and-routes.md): a unit carries any mix of
// resources under one total capacity, and load / unload / haul take an
// explicit amount.
public class MixedCargoTests
{
    private static readonly TileCoord Here = new(2, 2);

    private static Simulation MakeSim(out GameWorld world)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        return new Simulation(world, seed: 0xCA96);
    }

    [Fact]
    public void Hold_KeepsTotalInStep_AndDropsEmptyRows()
    {
        var hold = new CargoHold();
        hold.Add(Resource.Wood, 10);
        hold.Add(Resource.CopperOre, 4);
        hold.Add(Resource.Wood, 2);

        Assert.Equal(16, hold.Total);
        Assert.Equal(12, hold.AmountOf(Resource.Wood));

        Assert.Equal(4, hold.Take(Resource.CopperOre, 99));   // capped at what is held
        Assert.False(hold.Items.ContainsKey(Resource.CopperOre));
        Assert.Equal(0, hold.Take(Resource.Stone, 5));  // not held: nothing taken
        Assert.Equal(12, hold.Total);
    }

    [Fact]
    public void Hold_Dominant_IsLargestRow_TiesToLowestEnum()
    {
        var hold = new CargoHold();
        Assert.Equal(Resource.None, hold.Dominant);

        hold.Add(Resource.CopperOre, 5);
        hold.Add(Resource.Wood, 5);
        var lower = (byte)Resource.CopperOre < (byte)Resource.Wood ? Resource.CopperOre : Resource.Wood;
        Assert.Equal(lower, hold.Dominant);

        hold.Add(Resource.Wood, 1);
        Assert.Equal(Resource.Wood, hold.Dominant);
    }

    [Fact]
    public void Load_WithAmount_TakesNoMoreThanAsked()
    {
        var sim = MakeSim(out var world);
        var pile = (Stockpile)world.AddStructure(new Stockpile(Here));
        pile.Deposit(Resource.Wood, 100);
        var hauler = world.AddUnit(new Unit(1, Here) { Role = UnitRole.Hauler });

        var outcome = new LoadCargoIntent(1, Resource.Wood, amount: 7) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(7, hauler.CargoAmount);
        Assert.Equal(93, pile.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Unload_Everything_DepositsEachResource_OverflowToGround()
    {
        var sim = MakeSim(out var world);
        var pile = (Stockpile)world.AddStructure(new Stockpile(Here));
        pile.Deposit(Resource.Stone, pile.FreeSpace() - 3);   // room for 3 more
        var hauler = world.AddUnit(new Unit(1, Here)
        {
            Role = UnitRole.Hauler, Cargo = { { Resource.Wood, 2 }, { Resource.CopperOre, 5 } },
        });

        var outcome = new UnloadCargoIntent(1) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(0, hauler.CargoAmount);
        // Whatever the store took came off the hauler; the rest is on the
        // ground. Nothing was destroyed.
        var ground = world.GroundResources.TryGetValue(Here, out var g) ? g : new();
        Assert.Equal(2, pile.AmountOf(Resource.Wood) + ground.GetValueOrDefault(Resource.Wood));
        Assert.Equal(5, pile.AmountOf(Resource.CopperOre) + ground.GetValueOrDefault(Resource.CopperOre));
        Assert.Equal(0, pile.FreeSpace());
    }

    [Fact]
    public void Unload_Targeted_HandsOverOnlyThatResource_OverflowStaysAboard()
    {
        var sim = MakeSim(out var world);
        var pile = (Stockpile)world.AddStructure(new Stockpile(Here));
        pile.Deposit(Resource.Stone, pile.FreeSpace() - 4);   // room for 4 more
        var hauler = world.AddUnit(new Unit(1, Here)
        {
            Role = UnitRole.Hauler, Cargo = { { Resource.Wood, 10 }, { Resource.CopperOre, 6 } },
        });

        var outcome = new UnloadCargoIntent(1, Resource.Wood, amount: 8) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(4, pile.AmountOf(Resource.Wood));       // what fit
        Assert.Equal(6, hauler.Cargo.AmountOf(Resource.Wood)); // 10 − 4, not dumped
        Assert.Equal(6, hauler.Cargo.AmountOf(Resource.CopperOre));  // untouched
        Assert.False(world.GroundResources.ContainsKey(Here));
    }

    [Fact]
    public void Unload_Targeted_NoStructure_DropsThatAmountToGround()
    {
        var sim = MakeSim(out var world);
        var hauler = world.AddUnit(new Unit(1, Here)
        {
            Role = UnitRole.Hauler, Cargo = { { Resource.Wood, 10 }, { Resource.CopperOre, 6 } },
        });

        var outcome = new UnloadCargoIntent(1, Resource.CopperOre, amount: 2) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied, outcome.Reason);
        Assert.Equal(2, world.GroundResources[Here][Resource.CopperOre]);
        Assert.Equal(4, hauler.Cargo.AmountOf(Resource.CopperOre));
        Assert.Equal(10, hauler.Cargo.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Unload_Targeted_ResourceNotAboard_Rejected()
    {
        var sim = MakeSim(out var world);
        world.AddUnit(new Unit(1, Here) { Role = UnitRole.Hauler, Cargo = { { Resource.Wood, 10 } } });

        Assert.True(new UnloadCargoIntent(1, Resource.CopperOre) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UnloadCargoIntent(1, Resource.Wood, amount: -1) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Haul_WithAmount_PicksUpOnlyThatMuch()
    {
        var sim = MakeSim(out var world);
        var src = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(1, 1)));
        src.Deposit(Resource.Wood, 100);
        var dst = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(6, 1)));
        world.AddUnit(new Unit(1, new TileCoord(1, 1)) { Role = UnitRole.Hauler });

        sim.SubmitIntent(0, new HaulIntent(1, src.At, dst.At, Resource.Wood, amount: 9) { PlayerId = 0 });
        sim.Run();

        Assert.Equal(9, dst.AmountOf(Resource.Wood));
        Assert.Equal(91, src.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Death_DropsEveryResourceAboard()
    {
        var sim = MakeSim(out var world);
        var unit = world.AddUnit(new Unit(1, Here)
        {
            Role = UnitRole.Hauler, Cargo = { { Resource.Wood, 3 }, { Resource.Bronze, 2 } },
        });

        CombatRules.OnUnitDeath(sim, unit);

        Assert.Equal(3, world.GroundResources[Here][Resource.Wood]);
        Assert.Equal(2, world.GroundResources[Here][Resource.Bronze]);
    }

    [Fact]
    public void Snapshot_RoundTrips_MixedCargo_AndHaulAmount()
    {
        var sim = MakeSim(out var world);
        var src = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(1, 1)));
        src.Deposit(Resource.Wood, 100);
        var dst = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(9, 1)));
        world.AddUnit(new Unit(1, Here)
        {
            Role = UnitRole.Hauler, Cargo = { { Resource.CopperOre, 4 }, { Resource.Food, 1 } },
        });
        world.AddUnit(new Unit(2, new TileCoord(1, 1)) { Role = UnitRole.Hauler });
        sim.SubmitIntent(0, new HaulIntent(2, src.At, dst.At, Resource.Wood, amount: 12) { PlayerId = 0 });
        sim.Run(until: 1);   // mid-trip: plan and cargo both in flight

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xCA96);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(4, restored.World.Units[1].Cargo.AmountOf(Resource.CopperOre));
        Assert.Equal(1, restored.World.Units[1].Cargo.AmountOf(Resource.Food));
        Assert.Equal(12, restored.World.Units[2].HaulPlan!.Amount);
    }
}
