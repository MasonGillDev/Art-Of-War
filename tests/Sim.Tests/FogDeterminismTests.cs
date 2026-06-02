using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M3 Phase F: THE headline determinism test.
//
// Run a scenario with no views computed, hash it.
// Run the same scenario computing player views aggressively at every event,
// hash it. The hashes must be identical.
//
// That single equality IS "fog never touches the sim." Views are pure reads;
// they may not affect what the sim resolves. If a future change ever leaks
// view computation into sim state, this test fails immediately.
public class FogDeterminismTests
{
    private static GenesisSpec MakeFogScenarioSpec() => new()
    {
        Width = 20,
        Height = 20,
        CastlePosition = new TileCoord(5, 5),
        StartingHoldings = new SortedDictionary<Resource, int>
        {
            [Resource.Wood] = 50,
        },
        Units = new[]
        {
            new UnitSpawn(1, new TileCoord(5, 5), UnitRole.Builder),
            new UnitSpawn(2, new TileCoord(5, 5), UnitRole.Scout),
            new UnitSpawn(3, new TileCoord(5, 5), UnitRole.Hauler, CargoCapacity: 5),
        },
    };

    private static Simulation RunScenario(bool spamViews)
    {
        var world = Genesis.Build(MakeFogScenarioSpec());
        var sim = new Simulation(world, seed: 0xF06);

        // M0 movement + M1 site placement + M2 production lite, all
        // single-player. The scenario doesn't matter — only that it
        // generates events. Views must not affect any outcome.
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(15, 15)));
        sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(18, 2)));
        sim.SubmitIntent(0, new MoveIntent(3, new TileCoord(2, 18)));

        if (spamViews)
        {
            // Compute views every 5 ticks for 200 ticks.
            for (var t = 5L; t <= 200; t += 5)
            {
                sim.Run(until: t);
                _ = View.VisibleTiles(world, 0);
                _ = View.BuildPlayerView(world, 0);
            }
        }
        sim.Run();
        return sim;
    }

    [Fact]
    public void ViewsOff_HashEquals_ViewsOn()
    {
        var simNoViews = RunScenario(spamViews: false);
        var simWithViews = RunScenario(spamViews: true);
        Assert.Equal(Snapshot.Hash(simNoViews), Snapshot.Hash(simWithViews));
    }

    [Fact]
    public void SnapshotRoundTrips_OwnersAndExplored()
    {
        // Twin-run + serialize/restore on a world that exercises owners,
        // explored sets, and the M2 road condition layer all together.
        var sim = RunScenario(spamViews: false);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF06);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        // Spot-check: explored set survived.
        Assert.True(restored.World.Explored.ContainsKey(0));
        Assert.True(restored.World.Explored[0].Count > 0);
        // Spot-check: player 0 still in registry.
        Assert.True(restored.World.Players.ContainsKey(0));
        // Spot-check: every unit's OwnerId round-tripped.
        foreach (var (id, u) in sim.World.Units)
            Assert.Equal(u.OwnerId, restored.World.Units[id].OwnerId);
    }
}
