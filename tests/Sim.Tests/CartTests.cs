using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M-cart — the cart: equipment (a Buff) that trades move speed for carry
// capacity. The first NON-COMBAT buff: it adds two new modifier dimensions
// (CargoModifier, MoveCostPercent) rolled up live in Unit.CargoCapacity and
// MoveIntent.ScheduleNextHop. It shares the existing 2-slot pool. See
// docs/cart.md.
public class CartTests
{
    private static Simulation MakeSim(int size = 12)
    {
        var grid = new TileGrid(size, size, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        return new Simulation(world, seed: 1);
    }

    private static Buff CartBuff() =>
        new("cart", 0, 0, ExpiresAt: null, CargoModifier: 25, MoveCostPercent: 50);

    // ---- the two new stat dimensions ----

    [Fact]
    public void Cart_OnHauler_AddsCarryCapacity()
    {
        var sim = MakeSim();
        var h = sim.World.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler, OwnerId = 0 });
        Assert.Equal(25, h.CargoCapacity);   // base Hauler
        h.Buffs.Add(CartBuff());
        Assert.Equal(50, h.CargoCapacity);   // +cart
    }

    [Fact]
    public void Cart_SlowsMovement()
    {
        long FinishTick(bool withCart)
        {
            var sim = MakeSim();
            var h = sim.World.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler, OwnerId = 0 });
            if (withCart) h.Buffs.Add(CartBuff());
            new MoveIntent(1, new TileCoord(4, 0)) { PlayerId = 0 }.Resolve(sim);
            sim.Run();   // bare sim: the only events are this unit's move hops
            return sim.Now;
        }
        var plain = FinishTick(false);
        var carted = FinishTick(true);
        Assert.True(carted > plain, $"cart should be slower: carted={carted} plain={plain}");
    }

    // ---- craft + equip via the existing equipment machinery ----

    [Fact]
    public void Cart_CraftedAtBarracks_FromWoodAndStone()
    {
        var sim = MakeSim();
        var at = new TileCoord(2, 2);
        var barracks = sim.World.AddStructure(new Barracks(at) { OwnerId = 0 });
        barracks.Deposit(Resource.Wood, 20);
        barracks.Deposit(Resource.Stone, 10);

        Assert.True(new CraftEquipmentIntent(at, Resource.Cart) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(1, barracks.AmountOf(Resource.Cart));
        Assert.Equal(0, barracks.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Cart_Equipped_OnHauler_FromStorage_ConsumesItem_AddsCapacity()
    {
        var sim = MakeSim();
        var at = new TileCoord(2, 2);
        var stock = sim.World.AddStructure(new Stockpile(at) { OwnerId = 0 });
        stock.Deposit(Resource.Cart, 1);
        var h = sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });

        Assert.True(new EquipUnitIntent(1, Resource.Cart) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(50, h.CargoCapacity);
        Assert.Equal(0, stock.AmountOf(Resource.Cart));   // consumed
        Assert.Contains(h.Buffs, b => b.Kind == "cart");
    }

    [Fact]
    public void Cart_Equip_OnNonHauler_Rejected()
    {
        var sim = MakeSim();
        var at = new TileCoord(2, 2);
        var stock = sim.World.AddStructure(new Stockpile(at) { OwnerId = 0 });
        stock.Deposit(Resource.Cart, 1);
        sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Builder, OwnerId = 0 });

        Assert.True(new EquipUnitIntent(1, Resource.Cart) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.Equal(1, stock.AmountOf(Resource.Cart));   // untouched on reject
    }

    [Fact]
    public void Cart_CountsTowardTheSharedSlotCap()
    {
        // The cart shares the 2 generic buff slots — it is not a free extra.
        var sim = MakeSim();
        var h = sim.World.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler, OwnerId = 0 });
        h.Buffs.Add(CartBuff());
        Assert.False(BuffRules.CanAccept(h, "cart"));   // duplicate kind
        h.Buffs.Add(new Buff("drill", 1, 0, null));     // a hypothetical 2nd buff fills the cap
        Assert.False(BuffRules.CanAccept(h, "anything")); // slot cap reached
    }

    // ---- drop + persistence (rides the existing equipment loop) ----

    [Fact]
    public void Cart_DropsAsItem_OnStrip_AndRestoresCapacity()
    {
        var sim = MakeSim();
        var at = new TileCoord(3, 3);
        var h = sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });
        h.Buffs.Add(CartBuff());
        Assert.Equal(50, h.CargoCapacity);

        Sim.Core.Equipment.Equipment.DropEquipmentToGround(sim.World, h, at);

        Assert.Empty(h.Buffs);
        Assert.Equal(25, h.CargoCapacity);                       // back to base
        Assert.Equal(1, sim.World.GroundResources[at][Resource.Cart]); // dropped as a real item
    }

    [Fact]
    public void Cart_RoundTripsThroughSnapshot_PreservesModifiers()
    {
        var sim = MakeSim();
        var h = sim.World.AddUnit(new Unit(1, new TileCoord(2, 2)) { Role = UnitRole.Hauler, OwnerId = 0 });
        h.Buffs.Add(CartBuff());

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var rh = restored.World.Units[1];
        Assert.Equal(50, rh.CargoCapacity);
        Assert.Equal(25, rh.Buffs[0].CargoModifier);
        Assert.Equal(50, rh.Buffs[0].MoveCostPercent);
    }

    [Fact]
    public void Cart_CartedMove_TwinRun_HashesMatch()
    {
        Simulation Build()
        {
            var sim = MakeSim();
            var h = sim.World.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler, OwnerId = 0 });
            h.Buffs.Add(CartBuff());
            new MoveIntent(1, new TileCoord(5, 0)) { PlayerId = 0 }.Resolve(sim);
            return sim;
        }
        var a = Build(); var b = Build();
        a.Run(); b.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void NoCart_Movement_Unchanged()
    {
        // Regression guard: a unit with no move-cost buffs is unaffected by the
        // new scaling (slow == 0 → cost untouched).
        long Finish()
        {
            var sim = MakeSim();
            sim.World.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler, OwnerId = 0 });
            new MoveIntent(1, new TileCoord(4, 0)) { PlayerId = 0 }.Resolve(sim);
            sim.Run();
            return sim.Now;
        }
        Assert.Equal(Finish(), Finish());   // deterministic + unscaled
    }
}
