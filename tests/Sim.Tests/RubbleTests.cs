using Sim.Core.Canals;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// M24 Phase B — Rubble StructureKind + placement gate. Pins:
//   1. Rubble has Health = 0 (indestructible; the combat round will skip it
//      in Phase C, the same way it skips Cache / Canal).
//   2. Rubble is NOT player-buildable.
//   3. PlaceSiteIntent rejects when the target tile holds rubble — falls out
//      of the existing "tile already has a structure" guard for free.
//   4. PlaceCanalIntent rejects when any path tile holds rubble (same guard).
//   5. Rubble round-trips through Snapshot.Serialize / Restore.
public class RubbleTests
{
    [Fact]
    public void Rubble_IsIndestructible_AndHasDestroyedOwner()
    {
        var world = new GameWorld(new TileGrid(4, 4, Biome.Grassland));
        var r = world.AddStructure(
            new Rubble(new TileCoord(1, 1)) { OwnerId = SiegeConstants.RubbleOwnerId });

        Assert.Equal(0, StructureCatalog.Spec(StructureKind.Rubble).BaseHealth);
        Assert.Equal(0, r.Health);
        Assert.False(StructureCatalog.Spec(StructureKind.Rubble).IsPlayerBuildable);
        Assert.Equal(SiegeConstants.RubbleOwnerId, r.OwnerId);
    }

    [Fact]
    public void PlaceSite_OnRubbleTile_Rejected()
    {
        var sim = new Simulation(new GameWorld(new TileGrid(6, 6, Biome.Grassland)), seed: 1);
        var t = new TileCoord(2, 2);
        sim.World.AddStructure(
            new Rubble(t) { OwnerId = SiegeConstants.RubbleOwnerId });

        sim.SubmitIntent(0, new PlaceSiteIntent(t, StructureKind.Stockpile));
        sim.Run();

        Assert.IsType<Rubble>(sim.World.Structures[t]);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void PlaceCanal_ThroughRubble_Rejected()
    {
        // Lay out a water source next to a land path that includes a rubble pile.
        var grid = new TileGrid(8, 4, Biome.Grassland);
        grid.SetBiome(new TileCoord(0, 1), Biome.Water);
        var sim = new Simulation(new GameWorld(grid), seed: 1);
        var rubbleTile = new TileCoord(2, 1);
        sim.World.AddStructure(
            new Rubble(rubbleTile) { OwnerId = SiegeConstants.RubbleOwnerId });

        var path = new List<TileCoord>
        {
            new(1, 1),         // 4-adjacent to water at (0,1)
            new(2, 1),         // rubble lives here — must reject
            new(3, 1),
        };
        sim.SubmitIntent(0, new PlaceCanalIntent(path));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        // Nothing got placed at the anchor — fail-clean.
        Assert.False(sim.World.Structures.ContainsKey(new TileCoord(1, 1)));
        Assert.IsType<Rubble>(sim.World.Structures[rubbleTile]);
    }

    [Fact]
    public void Rubble_RoundTripsThroughSnapshot()
    {
        var grid = new TileGrid(4, 4, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(
            new Rubble(new TileCoord(1, 1)) { OwnerId = SiegeConstants.RubbleOwnerId });

        var sim = new Simulation(world, seed: 1);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var rr = (Rubble)restored.World.Structures[new TileCoord(1, 1)];
        Assert.Equal(SiegeConstants.RubbleOwnerId, rr.OwnerId);
        Assert.Equal(0, rr.Health);
    }
}
