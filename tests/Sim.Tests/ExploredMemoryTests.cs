using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M3 Phase B: explored memory is event-written (the INVERTED pure-read
// wall — sim writes, view reads). Tests pin:
//   - Genesis seeds explored around Castle + spawn units.
//   - Unit walks grow explored at each per-hop arrival.
//   - Explored never shrinks.
//   - Observation-independent (depends only on events, not on view calls).
//   - Snapshot round-trip.
public class ExploredMemoryTests
{
    private static GenesisSpec MakeSpec() => new()
    {
        Width = 20,
        Height = 20,
        FactionStarts = new[]
        {
            new FactionStartSpec
            {
                OwnerId = 0,
                CastlePosition = new TileCoord(5, 5),
                CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Wood] = 10 },
                UnitSpawns = new[]
                {
                    new UnitSpawn(Id: 1, new TileCoord(5, 5), UnitRole.Builder),
                },
            },
        },
    };

    [Fact]
    public void Genesis_RevealsCastleArea_ForOwner()
    {
        var world = Genesis.Build(MakeSpec());
        Assert.True(world.Explored.ContainsKey(0));
        // Castle radius = 5; tile at castle center MUST be in explored.
        Assert.Contains(new TileCoord(5, 5), world.Explored[0]);
        // A tile 5 tiles away (distance == radius) is in (5^2 == 25 == r^2).
        Assert.Contains(new TileCoord(10, 5), world.Explored[0]);
        // A tile 6 tiles away is NOT.
        Assert.DoesNotContain(new TileCoord(11, 5), world.Explored[0]);
    }

    [Fact]
    public void Genesis_DoesNotRevealForOtherPlayers()
    {
        var world = Genesis.Build(MakeSpec());
        // Register a second player (no faction start — bare registry entry).
        // After M6, FactionStart-less players are an unusual configuration;
        // this test is specifically about the per-player explored isolation.
        world.Players[1] = new Player(1);
        Assert.True(world.Explored.ContainsKey(0));
        // Player 1 owns nothing; their explored set is not yet created
        // (or, if created, empty). Either is acceptable — Vision.Reveal
        // only creates the set when called for that player.
        Assert.False(world.Explored.TryGetValue(1, out var set) && set.Count > 0);
    }

    [Fact]
    public void UnitWalk_GrowsExplored()
    {
        var world = Genesis.Build(MakeSpec());
        var beforeCount = world.Explored[0].Count;

        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(unitId: 1, new TileCoord(15, 15)));
        sim.Run();

        var afterCount = world.Explored[0].Count;
        Assert.True(afterCount > beforeCount,
            $"explored should have grown: before={beforeCount}, after={afterCount}");
        // The destination itself must be explored.
        Assert.Contains(new TileCoord(15, 15), world.Explored[0]);
    }

    [Fact]
    public void Explored_NeverShrinks()
    {
        var world = Genesis.Build(MakeSpec());

        // Walk somewhere, then walk back. Explored should only ever grow.
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(15, 15)));
        sim.Run();
        var midCount = world.Explored[0].Count;

        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(0, 0)));
        sim.Run();
        var finalCount = world.Explored[0].Count;

        Assert.True(finalCount >= midCount,
            $"explored shrank: mid={midCount}, final={finalCount}");
        // Both endpoints are in the set.
        Assert.Contains(new TileCoord(15, 15), world.Explored[0]);
        Assert.Contains(new TileCoord(0, 0), world.Explored[0]);
    }

    [Fact]
    public void Sight_Reveal_IsBoundedByGrid()
    {
        // Reveal near a corner — clamping must keep us in-bounds.
        var world = Genesis.Build(MakeSpec());
        var beforeCount = world.Explored[0].Count;
        Sight.Reveal(world, 0, new TileCoord(0, 0), 5);
        Assert.True(world.Explored[0].Count > beforeCount);
        // No negative coordinates leaked in.
        foreach (var t in world.Explored[0])
        {
            Assert.True(t.X >= 0 && t.Y >= 0);
            Assert.True(t.X < world.Grid.Width && t.Y < world.Grid.Height);
        }
    }

    [Fact]
    public void Snapshot_RoundTripsExplored()
    {
        var world = Genesis.Build(MakeSpec());
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(15, 15)));
        sim.Run();

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(world.Explored[0].Count, restored.World.Explored[0].Count);
        foreach (var t in world.Explored[0])
            Assert.Contains(t, restored.World.Explored[0]);
    }

    [Fact]
    public void TwoUnitsArrivingSameTick_ProduceStableExplored()
    {
        // Set-union is order-independent, so even if events fire in either
        // submission order, the final explored set is identical. This pins
        // determinism for the same-tick reveal case.
        Simulation Run()
        {
            var baseSpec = MakeSpec();
            var spec = baseSpec with
            {
                FactionStarts = new[]
                {
                    baseSpec.FactionStarts[0] with
                    {
                        UnitSpawns = new[]
                        {
                            new UnitSpawn(1, new TileCoord(5, 5), UnitRole.Builder),
                            new UnitSpawn(2, new TileCoord(5, 5), UnitRole.Builder),
                        },
                    },
                },
            };
            var world = Genesis.Build(spec);
            var sim = new Simulation(world, seed: 1);
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(15, 5)));
            sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(15, 5)));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }
}
