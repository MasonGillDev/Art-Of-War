using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M7 Phase A: per-unit Health + Buffs scaffolding + EffectivePower
// rollup.
//
//   * AddUnit auto-fills Health from UnitCombatCatalog if not set.
//   * EffectivePower sums catalog BasePower + active buff modifiers.
//   * ForcePower aggregates per owner across units on a tile.
//   * Snapshot round-trips Health + Buffs (FormatVersion 4).
public class CombatStatsTests
{
    private static GameWorld MakeWorld()
    {
        return Genesis.Build(new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(2, 2),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(5, 5), UnitRole.Builder),
                        new UnitSpawn(2, new TileCoord(5, 5), UnitRole.Hauler),
                    },
                },
                new FactionStartSpec
                {
                    OwnerId = 1,
                    CastlePosition = new TileCoord(15, 15),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(3, new TileCoord(5, 5), UnitRole.Builder, OwnerId: 1),
                    },
                },
            },
        });
    }

    [Fact]
    public void Genesis_InitializesHealth_FromCatalog()
    {
        var world = MakeWorld();
        foreach (var u in world.Units.Values)
            Assert.Equal(UnitCombatCatalog.Spec(u.Role).BaseHealth, u.Health);
    }

    [Fact]
    public void AddUnit_AutoFillsHealth_WhenZeroDefault()
    {
        var world = MakeWorld();
        var u = world.AddUnit(new Unit(99, new TileCoord(0, 0)) { Role = UnitRole.Scout });
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Scout).BaseHealth, u.Health);
    }

    [Fact]
    public void EffectivePower_BaselineMatchesCatalog()
    {
        var world = MakeWorld();
        var u = world.Units[1];
        Assert.Equal(UnitCombatCatalog.Spec(u.Role).BasePower, CombatRules.EffectivePower(u, now: 0));
    }

    [Fact]
    public void EffectivePower_SumsBuffModifiers()
    {
        var world = MakeWorld();
        var u = world.Units[1];
        var basePower = UnitCombatCatalog.Spec(u.Role).BasePower;
        u.Buffs.Add(new Buff(Kind: "test", PowerModifier: 3, HealthModifier: 0, ExpiresAt: null));
        u.Buffs.Add(new Buff(Kind: "test", PowerModifier: 2, HealthModifier: 0, ExpiresAt: null));
        Assert.Equal(basePower + 5, CombatRules.EffectivePower(u, now: 0));
    }

    [Fact]
    public void EffectivePower_FloorsAtZero()
    {
        var world = MakeWorld();
        var u = world.Units[1];
        u.Buffs.Add(new Buff(Kind: "debuff", PowerModifier: -100, HealthModifier: 0, ExpiresAt: null));
        Assert.Equal(0, CombatRules.EffectivePower(u, now: 0));
    }

    [Fact]
    public void ForcePower_SumsAcrossUnitsOnTile_PerOwner()
    {
        var world = MakeWorld();
        // Three units on tile (5,5): unit 1 (owner 0), unit 2 (owner 0), unit 3 (owner 1).
        var tile = new TileCoord(5, 5);
        Assert.Equal(2, CombatRules.ForcePower(world, ownerId: 0, tile, now: 0)); // 1+1
        Assert.Equal(1, CombatRules.ForcePower(world, ownerId: 1, tile, now: 0));
    }

    [Fact]
    public void EffectivePower_FiltersExpiredBuffs()
    {
        // Boundary convention: a buff expiring AT `now` is already
        // inactive (ExpiresAt <= now filters). Null = permanent.
        var world = MakeWorld();
        var u = world.Units[1];
        var basePower = UnitCombatCatalog.Spec(u.Role).BasePower;
        const long now = 100;
        u.Buffs.Add(new Buff("expired", PowerModifier: 10, HealthModifier: 0, ExpiresAt: now - 1));
        u.Buffs.Add(new Buff("expiring-now", PowerModifier: 10, HealthModifier: 0, ExpiresAt: now));
        u.Buffs.Add(new Buff("active", PowerModifier: 3, HealthModifier: 0, ExpiresAt: now + 1));
        u.Buffs.Add(new Buff("permanent", PowerModifier: 2, HealthModifier: 0, ExpiresAt: null));

        Assert.Equal(basePower + 3 + 2, CombatRules.EffectivePower(u, now));
    }

    [Fact]
    public void EffectivePower_IsPureRead_NoMutation()
    {
        // The expiry filter must never prune — EffectivePower sits
        // behind the pure-read wall. 100 reads leave the hash unchanged
        // (including the expired buff still being present in the list).
        var world = MakeWorld();
        var u = world.Units[1];
        u.Buffs.Add(new Buff("expired", PowerModifier: 10, HealthModifier: 0, ExpiresAt: 1));
        var sim = new Simulation(world, seed: 0xC0F);

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
            CombatRules.EffectivePower(u, now: 50);
        Assert.Equal(before, Snapshot.Hash(sim));
        Assert.Single(u.Buffs);
    }

    [Fact]
    public void Snapshot_RoundTrips_HealthAndBuffs()
    {
        var world = MakeWorld();
        // Damage and buff a unit so we capture non-default values.
        world.Units[1].Health = 3;
        world.Units[1].Buffs.Add(new Buff("armor", PowerModifier: 0, HealthModifier: 5, ExpiresAt: 100));

        var sim = new Simulation(world, seed: 0xC0F);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xC0F);

        Assert.Equal(3, restored.World.Units[1].Health);
        Assert.Single(restored.World.Units[1].Buffs);
        Assert.Equal("armor", restored.World.Units[1].Buffs[0].Kind);
        Assert.Equal(5, restored.World.Units[1].Buffs[0].HealthModifier);
        Assert.Equal(100L, restored.World.Units[1].Buffs[0].ExpiresAt);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
