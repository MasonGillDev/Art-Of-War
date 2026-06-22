using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M24 Phase A — structure HP plumbing. Pins:
//   1. StructureCatalog.BaseHealth feeds Structure.Health on AddStructure
//      (the same shape as UnitCombatCatalog → Unit.Health).
//   2. A spec BaseHealth of 0 (Cache, Canal) keeps Health = 0 — the
//      indestructible sentinel the combat round will gate on in Phase C.
//   3. A partially damaged structure round-trips its DAMAGED HP through
//      Snapshot.Serialize / Restore (auto-init is a no-op on restore).
// Siege damage, rubble, defeat are in Phases B–D.
public class StructureHealthTests
{
    [Fact]
    public void AddStructure_AutoFillsHealthFromCatalog()
    {
        var world = new GameWorld(new TileGrid(4, 4, Biome.Grassland));
        var castle = world.AddStructure(new Castle(new TileCoord(0, 0)));
        var stockpile = world.AddStructure(new Stockpile(new TileCoord(1, 0)));
        var house = world.AddStructure(new House(new TileCoord(2, 0)));

        Assert.Equal(StructureCatalog.Spec(StructureKind.Castle).BaseHealth, castle.Health);
        Assert.Equal(StructureCatalog.Spec(StructureKind.Stockpile).BaseHealth, stockpile.Health);
        Assert.Equal(StructureCatalog.Spec(StructureKind.House).BaseHealth, house.Health);
        Assert.True(castle.Health > 0);
    }

    [Fact]
    public void IndestructibleKinds_StayAtZeroHealth()
    {
        var world = new GameWorld(new TileGrid(4, 4, Biome.Grassland));
        var cache = world.AddStructure(new Cache(new TileCoord(0, 0)));

        Assert.Equal(0, StructureCatalog.Spec(StructureKind.Cache).BaseHealth);
        Assert.Equal(0, cache.Health);
    }

    [Fact]
    public void DamagedHealth_RoundTripsThroughSnapshot()
    {
        var grid = new TileGrid(4, 4, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var castle = world.AddStructure(new Castle(new TileCoord(0, 0)) { OwnerId = 0 });
        var fullHealth = castle.Health;
        castle.Health = fullHealth - 137;          // damaged mid-siege

        var sim = new Simulation(world, seed: 1);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);

        var restoredCastle = (Castle)restored.World.Structures[new TileCoord(0, 0)];
        Assert.Equal(fullHealth - 137, restoredCastle.Health);
        // Auto-init must NOT have re-armed the restored castle to full HP.
        Assert.NotEqual(fullHealth, restoredCastle.Health);
    }

    [Fact]
    public void FormatVersion_IsBumpedForSiegeHp()
    {
        // Pin the bump so a stray rollback shows up loudly in CI rather than
        // silently shipping an HP-less snapshot. See Snapshot.cs version log.
        Assert.True(Snapshot.FormatVersion >= 19,
            "M24 (sieges) bumped the snapshot format to 19; do not roll back.");
    }
}
