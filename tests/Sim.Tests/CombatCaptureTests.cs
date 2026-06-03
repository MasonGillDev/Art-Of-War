using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M7 Phase E: capture economy. Cargo from a dying laden unit drops to the
// tile (world.GroundResources), and another unit can haul from that pile.
public class CombatCaptureTests
{
    private static Simulation MakeContestedScenario(TileCoord tile)
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Combat = new CombatConfig(RoundIntervalTicks: 10),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return new Simulation(world, seed: 0xCAB);
    }

    [Fact]
    public void LadenUnitDies_CargoDropsToTile()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile);

        // Laden hauler from owner 0.
        var hauler = sim.World.AddUnit(new Unit(100, tile)
            { Role = UnitRole.Hauler, CargoCapacity = 5, OwnerId = 0 });
        hauler.CargoResource = Resource.Wood;
        hauler.CargoAmount = 5;
        hauler.Health = 1;

        // Overwhelming ambush from owner 1.
        for (var i = 0; i < 5; i++)
            sim.World.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 100);

        Assert.False(sim.World.Units.ContainsKey(100), "hauler should be dead");
        Assert.True(sim.World.GroundResources.TryGetValue(tile, out var pile),
            "cargo should have dropped to the tile");
        Assert.Equal(5, pile[Resource.Wood]);
    }

    [Fact]
    public void MultipleDeathsOnSameTile_AccumulateGroundPile()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile);

        for (var id = 100; id <= 102; id++)
        {
            var u = sim.World.AddUnit(new Unit(id, tile)
                { Role = UnitRole.Hauler, CargoCapacity = 5, OwnerId = 0 });
            u.CargoResource = Resource.Wood;
            u.CargoAmount = 3;
            u.Health = 1;
        }
        for (var i = 0; i < 6; i++)
            sim.World.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 200);

        Assert.True(sim.World.GroundResources.TryGetValue(tile, out var pile));
        Assert.Equal(9, pile[Resource.Wood]); // 3 × 3 = 9
    }

    [Fact]
    public void HaulFromGround_PicksUpLooseCargo()
    {
        // Hand-place a ground pile and a hauler; haul it to a stockpile.
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Hauler, CargoCapacity: 5),
                    },
                },
            },
        };
        var world = Genesis.Build(spec);
        var loosePile = new TileCoord(5, 5);
        world.GroundResources[loosePile] = new SortedDictionary<Resource, int> { [Resource.Wood] = 4 };
        var dest = new TileCoord(0, 0); // Castle tile (storage).

        var sim = new Simulation(world, seed: 0xCAB);
        sim.SubmitIntent(0, new HaulIntent(1, loosePile, dest, Resource.Wood));
        sim.Run();

        // Ground pile reduced (or removed if drained).
        Assert.False(world.GroundResources.ContainsKey(loosePile),
            "ground pile should be empty and removed after haul drained it");
        // Castle accumulated the wood.
        var castle = (Castle)world.Structures[dest];
        Assert.Equal(4, castle.AmountOf(Resource.Wood));
    }

    [Fact]
    public void GroundResources_SnapshotRoundTrip()
    {
        var sim = MakeContestedScenario(new TileCoord(0, 0));
        sim.World.GroundResources[new TileCoord(5, 5)] = new SortedDictionary<Resource, int>
        {
            [Resource.Wood] = 7,
            [Resource.Stone] = 3,
        };
        sim.World.GroundResources[new TileCoord(7, 2)] = new SortedDictionary<Resource, int>
        {
            [Resource.Food] = 11,
        };

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xCAB);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(7, restored.World.GroundResources[new TileCoord(5, 5)][Resource.Wood]);
        Assert.Equal(3, restored.World.GroundResources[new TileCoord(5, 5)][Resource.Stone]);
        Assert.Equal(11, restored.World.GroundResources[new TileCoord(7, 2)][Resource.Food]);
    }
}
