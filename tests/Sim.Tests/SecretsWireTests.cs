using System.Text.Json;
using Sim.Core.Caches;
using Sim.Core.Engine;
using Sim.Core.Scouting;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M38 Phase D1 (docs/scouting-secrets.md): the chart and idol circles reach
// their owner only; a chart entry carries a hint, never the secret's kind or
// loot; an idol out of sight is not on the wire, and is an ordinary structure
// in sight; projecting is a pure read.
public class SecretsWireTests
{
    private static (Simulation sim, ViewProjector projector, Castle castle) Match()
    {
        var build = WorldFactory.Build(new ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = true });
        var sim = new Simulation(build.Spec, seed: 0x3A1E);
        var castle = sim.World.Structures.Values.OfType<Castle>().First(c => c.OwnerId == 0);
        return (sim, new ViewProjector(build), castle);
    }

    [Fact]
    public void TheChart_ReachesItsOwner_Only_AsAHint()
    {
        var (sim, projector, castle) = Match();
        var far = new TileCoord(castle.At.X + 30, castle.At.Y);
        var cache = sim.World.AddStructure(new Cache(far) { OwnerId = CacheConstants.OwnerId });
        cache.Deposit(Sim.Core.World.Resource.Sword, 3);
        sim.World.Charts[0] = new SortedDictionary<TileCoord, ChartEntry>(TileOrder.Instance)
        {
            [far] = new ChartEntry { Tile = far, Hint = SecretHint.Glint, SeenTick = 5 },
        };

        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var theirs = projector.Project(sim, sim.Now, playerId: 1, reveal: false);

        var e = Assert.Single(mine.Chart);
        Assert.Equal((far.X, far.Y, (int)SecretHint.Glint, (int)ChartState.Known, 5L),
            (e.X, e.Y, e.Hint, e.State, e.SeenTick));
        Assert.Empty(theirs.Chart);
        // Out of sight, the cache itself is not on the wire, and nothing says what it holds.
        Assert.DoesNotContain(mine.Structures, s => s.X == far.X && s.Y == far.Y);
    }

    [Fact]
    public void AnIdolsCircle_ReachesItsOwner_WithTimeLeft_AndShowsWhatIsInIt()
    {
        var (sim, projector, castle) = Match();
        var at = castle.At with { X = castle.At.X + 1 };
        sim.World.Structures.Remove(at);
        sim.World.AddStructure(new Idol(at, IdolKind.Lesser) { OwnerId = CacheConstants.OwnerId });
        var unit = sim.World.Units.Values.First(u => u.OwnerId == 0);
        unit.Position = at;

        sim.SubmitIntent(sim.Now, new ActivateIdolIntent(unit.Id) { PlayerId = 0 });
        sim.Run(until: sim.Now);
        var g = Assert.Single(sim.World.VisionGrants.Values);

        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var row = Assert.Single(mine.VisionGrants);
        Assert.Equal((g.Center.X, g.Center.Y, g.Radius, g.EndsTick - sim.Now), (row.X, row.Y, row.Radius, row.TicksLeft));
        Assert.Contains(mine.Visible, t => t.X == g.Center.X && t.Y == g.Center.Y);
        Assert.Empty(projector.Project(sim, sim.Now, playerId: 1, reveal: false).VisionGrants);
    }

    [Fact]
    public void ProjectingSecrets_IsAPureRead()
    {
        var (sim, projector, castle) = Match();
        var far = new TileCoord(castle.At.X + 30, castle.At.Y);
        sim.World.Charts[0] = new SortedDictionary<TileCoord, ChartEntry>(TileOrder.Instance)
        {
            [far] = new ChartEntry { Tile = far, Hint = SecretHint.StoneFigure, SeenTick = 1 },
        };
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++) projector.Project(sim, sim.Now + i, playerId: 0, reveal: false);
        Assert.Equal(before, Snapshot.Hash(sim));

        var json = JsonSerializer.Serialize(projector.Project(sim, sim.Now, playerId: 0, reveal: false).Chart);
        Assert.DoesNotContain("Kind", json);
    }
}
