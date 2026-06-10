using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Military milestone — Barracks structure + Soldier/Archer roles
// (docs/military-training.md). All expectations derive from
// StructureCatalog / UnitCombatCatalog values, never hard-coded.
public class MilitaryTrainingTests
{
    private static Simulation MakeSim(int w = 8, int h = 8)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        return new Simulation(world, seed: 1);
    }

    private static Unit AddUnit(Simulation sim, int id, TileCoord pos, UnitRole role)
    {
        var u = new Unit(id, pos) { Role = role };
        sim.World.AddUnit(u);
        return u;
    }

    // -------- Barracks: buildable structure --------

    [Fact]
    public void PlaceSiteIntent_CanPlaceBarracks()
    {
        var sim = MakeSim();
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Barracks));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);
        Assert.IsType<ConstructionSite>(sim.World.Structures[new TileCoord(2, 2)]);
    }

    [Fact]
    public void BarracksBuildComplete_CreatesStorageStructure()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.Barracks));
        var spec = StructureCatalog.Spec(StructureKind.Barracks);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        AddUnit(sim, 1, siteTile, UnitRole.Builder);

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        var built = Assert.IsType<Barracks>(sim.World.Structures[siteTile]);
        Assert.Equal(spec.StorageCapacity, built.Capacity);
    }

    [Fact]
    public void HaulSword_IntoBarracks_DepositsToHoldings()
    {
        // Equipment items are Resource values, so the existing haul flow
        // moves them into the new structure with zero special cases.
        var sim = MakeSim();
        var castle = sim.World.AddStructure(new Castle(new TileCoord(0, 0)));
        var barracks = sim.World.AddStructure(new Barracks(new TileCoord(4, 0)));
        castle.Deposit(Resource.Sword, 3);
        AddUnit(sim, 1, new TileCoord(0, 0), UnitRole.Hauler);

        sim.SubmitIntent(0, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(4, 0), Resource.Sword));
        sim.Run();

        Assert.Equal(0, castle.AmountOf(Resource.Sword));
        Assert.Equal(3, barracks.AmountOf(Resource.Sword));
    }

    [Fact]
    public void Barracks_SnapshotRoundTrip_PreservesHoldings()
    {
        var sim = MakeSim();
        var barracks = sim.World.AddStructure(new Barracks(new TileCoord(2, 2)) { OwnerId = 0 });
        barracks.Deposit(Resource.Wood, 10);
        barracks.Deposit(Resource.Ore, 5);
        barracks.Deposit(Resource.Sword, 2);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);

        var rb = Assert.IsType<Barracks>(restored.World.Structures[new TileCoord(2, 2)]);
        Assert.Equal(10, rb.AmountOf(Resource.Wood));
        Assert.Equal(5, rb.AmountOf(Resource.Ore));
        Assert.Equal(2, rb.AmountOf(Resource.Sword));
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    // -------- Training routing (RoleTrainerCatalog) --------

    // Genesis-built single-faction world with one adult citizen standing
    // on a trainer structure. Mirrors TrainUnitTests.MakeSchoolAndCitizen.
    private static (Simulation sim, Unit citizen) MakeTrainerAndCitizen(
        StructureKind trainerKind, int trainerOwner = 0)
    {
        var spec = new GenesisSpec
        {
            Width = 5, Height = 5,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(2, 2), UnitRole.None,
                            OwnerId: 0, StartingAgeYears: 30),
                    },
                },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(4, 4) },
            },
        };
        var sim = new Simulation(spec, seed: 0x4127);
        Structure trainer = trainerKind switch
        {
            StructureKind.Barracks => new Barracks(new TileCoord(2, 2)) { OwnerId = trainerOwner },
            StructureKind.School => new School(new TileCoord(2, 2)) { OwnerId = trainerOwner },
            _ => throw new InvalidOperationException($"unexpected trainer kind {trainerKind}"),
        };
        sim.World.AddStructure(trainer);
        return (sim, sim.World.Units[1]);
    }

    [Theory]
    [InlineData(UnitRole.Soldier)]
    [InlineData(UnitRole.Archer)]
    public void TrainMilitaryRole_OnOwnBarracks_FlipsRole(UnitRole role)
    {
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks);
        var outcome = new TrainUnitIntent(citizen.Id, role) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        Assert.Equal(role, citizen.Role);
    }

    [Fact]
    public void TrainSoldier_OnSchool_Rejected()
    {
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.School);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Soldier) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.Equal(UnitRole.None, citizen.Role);
    }

    [Fact]
    public void TrainBuilder_OnBarracks_Rejected()
    {
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Builder) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.Equal(UnitRole.None, citizen.Role);
    }

    [Fact]
    public void TrainSoldier_OnEnemyBarracks_Rejected()
    {
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks, trainerOwner: 1);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Soldier) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void TrainBoat_StillRejected()
    {
        // Boat maps to no trainer in RoleTrainerCatalog — the original
        // "boats are dock-produced" rule survives the routing change.
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Boat) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    // -------- Health delta on role change --------

    [Fact]
    public void TrainToSoldier_AdjustsHealthByBaseDelta()
    {
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks);
        var before = citizen.Health;
        var delta = UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth
                  - UnitCombatCatalog.Spec(UnitRole.None).BaseHealth;

        new TrainUnitIntent(citizen.Id, UnitRole.Soldier) { PlayerId = 0 }.Resolve(sim);

        Assert.Equal(before + delta, citizen.Health);
    }

    [Fact]
    public void RetrainWoundedSoldier_ToFarmer_KeepsAbsoluteDamage_ClampsAtOne()
    {
        // Wounds persist absolutely across retrains; the clamp stops a
        // downward delta from killing. Both expectations catalog-derived.
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks);
        new TrainUnitIntent(citizen.Id, UnitRole.Soldier) { PlayerId = 0 }.Resolve(sim);
        var school = sim.World.AddStructure(new School(new TileCoord(3, 3)) { OwnerId = 0 });

        var soldierBase = UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
        var farmerBase = UnitCombatCatalog.Spec(UnitRole.Farmer).BaseHealth;

        // Lightly wounded: damage 3 → expect (farmerBase - 3) after retrain.
        citizen.Health = soldierBase - 3;
        citizen.Position = school.At;
        new TrainUnitIntent(citizen.Id, UnitRole.Farmer) { PlayerId = 0 }.Resolve(sim);
        Assert.Equal(UnitRole.Farmer, citizen.Role);
        Assert.Equal(farmerBase - 3, citizen.Health);

        // Critically wounded: more damage than farmerBase → clamp at 1.
        citizen.Position = new TileCoord(2, 2); // back to barracks
        new TrainUnitIntent(citizen.Id, UnitRole.Soldier) { PlayerId = 0 }.Resolve(sim);
        citizen.Health = 1; // soldier at death's door
        citizen.Position = school.At;
        new TrainUnitIntent(citizen.Id, UnitRole.Farmer) { PlayerId = 0 }.Resolve(sim);
        Assert.Equal(UnitRole.Farmer, citizen.Role);
        Assert.Equal(1, citizen.Health);
    }

    // -------- Retrain strips equipment --------

    [Fact]
    public void Retrain_StripsEquipment_DropsItemsToTile_ReversesHealthModifier()
    {
        var (sim, citizen) = MakeTrainerAndCitizen(StructureKind.Barracks);
        new TrainUnitIntent(citizen.Id, UnitRole.Soldier) { PlayerId = 0 }.Resolve(sim);

        // Hand-add a full catalog loadout (sword + shield); the intent
        // path is pinned end-to-end in EquipUnitTests / EquipmentCombatTests.
        var sword = EquipmentCatalog.Spec(Resource.Sword);
        var shield = EquipmentCatalog.Spec(Resource.Shield);
        citizen.Buffs.Add(new Buff(sword.BuffKind, sword.PowerModifier, sword.HealthModifier, null));
        citizen.Buffs.Add(new Buff(shield.BuffKind, shield.PowerModifier, shield.HealthModifier, null));
        citizen.Health += shield.HealthModifier;
        var healthyWithShield = citizen.Health;

        new TrainUnitIntent(citizen.Id, UnitRole.Archer) { PlayerId = 0 }.Resolve(sim);

        Assert.Equal(UnitRole.Archer, citizen.Role);
        Assert.Empty(citizen.Buffs);
        var pile = sim.World.GroundResources[citizen.Position];
        Assert.Equal(1, pile[Resource.Sword]);
        Assert.Equal(1, pile[Resource.Shield]);
        // Shield's HealthModifier reversed, then the Soldier→Archer base
        // delta applied — all catalog-derived.
        var expected = healthyWithShield - shield.HealthModifier
                     + UnitCombatCatalog.Spec(UnitRole.Archer).BaseHealth
                     - UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
        Assert.Equal(expected, citizen.Health);
    }

    // -------- Soldier / Archer catalog rows --------

    [Fact]
    public void SpawnedSoldier_HealthFromCatalog()
    {
        var sim = MakeSim();
        var soldier = AddUnit(sim, 1, new TileCoord(1, 1), UnitRole.Soldier);
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth, soldier.Health);
    }

    [Fact]
    public void SpawnedArcher_HealthFromCatalog()
    {
        var sim = MakeSim();
        var archer = AddUnit(sim, 1, new TileCoord(1, 1), UnitRole.Archer);
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Archer).BaseHealth, archer.Health);
    }
}
