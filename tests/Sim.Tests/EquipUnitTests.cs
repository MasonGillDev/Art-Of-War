using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// EquipUnitIntent — consume an item from owned storage on the unit's
// tile, grant the catalog buff (docs/equipment-model.md). All
// expectations derive from EquipmentCatalog / UnitCombatCatalog /
// BuffRules — never hard-coded.
public class EquipUnitTests
{
    private static Simulation MakeSim(int w = 8, int h = 8)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        return new Simulation(world, seed: 1);
    }

    // A unit of `role` standing on an owned storage stocked with the items.
    private static (Simulation sim, Unit unit, StorageStructure storage) MakeEquipScenario(
        UnitRole role, params (Resource item, int count)[] stock)
    {
        var sim = MakeSim();
        var storage = (StorageStructure)sim.World.AddStructure(new Barracks(new TileCoord(2, 2)));
        foreach (var (item, count) in stock) storage.Deposit(item, count);
        var unit = sim.World.AddUnit(new Unit(1, storage.At) { Role = role });
        return (sim, unit, storage);
    }

    [Fact]
    public void EquipSword_OnSoldier_AddsBuff_ConsumesItem()
    {
        var (sim, soldier, storage) = MakeEquipScenario(UnitRole.Soldier, (Resource.Sword, 1));
        var spec = EquipmentCatalog.Spec(Resource.Sword);

        var outcome = new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied);
        var buff = Assert.Single(soldier.Buffs);
        Assert.Equal(spec.BuffKind, buff.Kind);
        Assert.Equal(spec.PowerModifier, buff.PowerModifier);
        Assert.Null(buff.ExpiresAt);
        Assert.Equal(0, storage.AmountOf(Resource.Sword));
    }

    [Fact]
    public void EquipSword_IncreasesEffectivePower()
    {
        var (sim, soldier, _) = MakeEquipScenario(UnitRole.Soldier, (Resource.Sword, 1));
        new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim);

        Assert.Equal(
            UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower
                + EquipmentCatalog.Spec(Resource.Sword).PowerModifier,
            CombatRules.EffectivePower(soldier, sim.Now));
    }

    // -------- Role gate matrix --------

    [Theory]
    [InlineData(UnitRole.Soldier, Resource.Sword, true)]
    [InlineData(UnitRole.Soldier, Resource.Bow, false)]
    [InlineData(UnitRole.Soldier, Resource.Shield, true)]
    [InlineData(UnitRole.Archer, Resource.Sword, false)]
    [InlineData(UnitRole.Archer, Resource.Bow, true)]
    [InlineData(UnitRole.Archer, Resource.Shield, true)]
    [InlineData(UnitRole.Farmer, Resource.Sword, false)]
    [InlineData(UnitRole.Hauler, Resource.Shield, false)]
    public void Equip_RoleGate(UnitRole role, Resource item, bool allowed)
    {
        var (sim, unit, _) = MakeEquipScenario(role, (item, 1));
        var outcome = new EquipUnitIntent(unit.Id, item) { PlayerId = 0 }.Resolve(sim);
        Assert.Equal(allowed, outcome.IsApplied);
        if (!allowed) Assert.Empty(unit.Buffs);
    }

    [Fact]
    public void Equip_CatalogHasNoCivilianRoles()
    {
        // Structural pin behind the role-gate matrix: every AllowedRoles
        // set contains only military roles.
        foreach (var item in new[] { Resource.Sword, Resource.Bow, Resource.Shield })
            foreach (var role in EquipmentCatalog.Spec(item).AllowedRoles)
                Assert.Contains(role, new[] { UnitRole.Soldier, UnitRole.Archer });
    }

    // -------- Loadout: 2 slots, distinct kinds --------

    [Fact]
    public void Equip_SwordPlusShield_BothApply_PowerAndHealth()
    {
        var (sim, soldier, _) = MakeEquipScenario(
            UnitRole.Soldier, (Resource.Sword, 1), (Resource.Shield, 1));
        var healthBefore = soldier.Health;
        var sword = EquipmentCatalog.Spec(Resource.Sword);
        var shield = EquipmentCatalog.Spec(Resource.Shield);

        Assert.True(new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new EquipUnitIntent(soldier.Id, Resource.Shield) { PlayerId = 0 }.Resolve(sim).IsApplied);

        Assert.Equal(2, soldier.Buffs.Count);
        Assert.Equal(
            UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower + sword.PowerModifier + shield.PowerModifier,
            CombatRules.EffectivePower(soldier, sim.Now));
        Assert.Equal(healthBefore + shield.HealthModifier, soldier.Health);
    }

    [Fact]
    public void Equip_SecondShield_Rejected_DuplicateKind()
    {
        // Duplicate Kind rejects even with a slot free — loadout, not stack.
        var (sim, archer, storage) = MakeEquipScenario(UnitRole.Archer, (Resource.Shield, 2));
        Assert.True(new EquipUnitIntent(archer.Id, Resource.Shield) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var outcome = new EquipUnitIntent(archer.Id, Resource.Shield) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Single(archer.Buffs);
        Assert.Equal(1, storage.AmountOf(Resource.Shield)); // second shield not consumed
    }

    [Fact]
    public void Equip_BeyondSlotCap_Rejected()
    {
        // Fill the loadout to BuffRules.MaxBuffsPerUnit with filler buffs
        // of distinct kinds, then any equip must reject on the cap.
        var (sim, soldier, storage) = MakeEquipScenario(UnitRole.Soldier, (Resource.Sword, 1));
        for (var i = soldier.Buffs.Count; i < BuffRules.MaxBuffsPerUnit; i++)
            soldier.Buffs.Add(new Buff($"filler-{i}", 0, 0, null));

        var outcome = new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Equal(BuffRules.MaxBuffsPerUnit, soldier.Buffs.Count);
        Assert.Equal(1, storage.AmountOf(Resource.Sword));
    }

    [Fact]
    public void EquipShield_RaisesHealthByModifier()
    {
        var (sim, soldier, _) = MakeEquipScenario(UnitRole.Soldier, (Resource.Shield, 1));
        var before = soldier.Health;

        new EquipUnitIntent(soldier.Id, Resource.Shield) { PlayerId = 0 }.Resolve(sim);

        Assert.Equal(before + EquipmentCatalog.Spec(Resource.Shield).HealthModifier, soldier.Health);
    }

    // -------- Storage gates --------

    [Fact]
    public void Equip_FromForwardStockpile_Applied()
    {
        // Any owned StorageStructure works — haul swords to a forward
        // stockpile, equip at the front (decision c, docs/equipment-model.md).
        var sim = MakeSim();
        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(5, 5)));
        stockpile.Deposit(Resource.Sword, 1);
        var soldier = sim.World.AddUnit(new Unit(1, stockpile.At) { Role = UnitRole.Soldier });

        var outcome = new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied);
        Assert.Single(soldier.Buffs);
    }

    [Fact]
    public void Equip_EnemyStorage_Rejected()
    {
        var sim = MakeSim();
        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(5, 5)) { OwnerId = 1 });
        stockpile.Deposit(Resource.Sword, 1);
        var soldier = sim.World.AddUnit(new Unit(1, stockpile.At) { Role = UnitRole.Soldier });

        Assert.False(new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void Equip_EmptyStorage_Rejected()
    {
        var (sim, soldier, _) = MakeEquipScenario(UnitRole.Soldier /* no stock */);
        Assert.False(new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void Equip_NoStorageOnTile_Rejected()
    {
        var sim = MakeSim();
        var soldier = sim.World.AddUnit(new Unit(1, new TileCoord(3, 3)) { Role = UnitRole.Soldier });
        Assert.False(new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void Equip_NotIdle_Rejected()
    {
        var (sim, soldier, _) = MakeEquipScenario(UnitRole.Soldier, (Resource.Sword, 1));
        soldier.TrySetActivity(Activity.Hauling);
        Assert.False(new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    // -------- Fairness + persistence --------

    [Fact]
    public void Equip_SameTickContention_FirstSubmittedWins_BothOrders()
    {
        // Two soldiers, one sword. Submission order decides; swapping the
        // order swaps the winner.
        for (var swap = 0; swap < 2; swap++)
        {
            var sim = MakeSim();
            var barracks = sim.World.AddStructure(new Barracks(new TileCoord(2, 2)));
            barracks.Deposit(Resource.Sword, 1);
            var s1 = sim.World.AddUnit(new Unit(1, barracks.At) { Role = UnitRole.Soldier });
            var s2 = sim.World.AddUnit(new Unit(2, barracks.At) { Role = UnitRole.Soldier });

            var (first, second) = swap == 0 ? (s1, s2) : (s2, s1);
            sim.SubmitIntent(0, new EquipUnitIntent(first.Id, Resource.Sword));
            sim.SubmitIntent(0, new EquipUnitIntent(second.Id, Resource.Sword));
            sim.Run();

            Assert.Single(first.Buffs);
            Assert.Empty(second.Buffs);
            Assert.Equal(0, barracks.AmountOf(Resource.Sword));
        }
    }

    [Fact]
    public void EquippedUnit_SnapshotRoundTrip_PreservesLoadoutAndPower()
    {
        var (sim, soldier, _) = MakeEquipScenario(
            UnitRole.Soldier, (Resource.Sword, 1), (Resource.Shield, 1));
        new EquipUnitIntent(soldier.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim);
        new EquipUnitIntent(soldier.Id, Resource.Shield) { PlayerId = 0 }.Resolve(sim);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        var ru = restored.World.Units[soldier.Id];

        Assert.Equal(2, ru.Buffs.Count);
        Assert.Equal(soldier.Health, ru.Health);
        Assert.Equal(
            CombatRules.EffectivePower(soldier, sim.Now),
            CombatRules.EffectivePower(ru, restored.Now));
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
