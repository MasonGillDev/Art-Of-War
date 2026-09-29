using Sim.Core;
using Sim.Core.Bandits;
using Sim.Core.Caches;
using Sim.Core.Engine;
using Sim.Core.Scouting;
using Sim.Core.Vision;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M38 Phase C (docs/scouting-secrets.md): idols are scattered in the genesis
// fog; a unit standing on one activates it; its owner sees a random circle of
// ground they had never explored, as real sight, for the grade's time; bandits
// cannot spawn under it; the idol crumbles. Numbers come from IdolConfig.
public class IdolTests
{
    private static readonly TileCoord IdolAt = new(10, 10);

    private static (Simulation sim, Unit unit) OnAnIdol(IdolKind grade = IdolKind.Lesser)
    {
        var world = new GameWorld(new TileGrid(60, 60, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[CacheConstants.OwnerId] = new Player(CacheConstants.OwnerId);
        world.AddStructure(new Idol(IdolAt, grade) { OwnerId = CacheConstants.OwnerId });
        var unit = world.AddUnit(new Unit(1, IdolAt) { OwnerId = 0 });
        // The owner knows only the corner around the idol.
        var known = new HashSet<TileCoord>();
        for (var y = 0; y < 20; y++) for (var x = 0; x < 20; x++) known.Add(new TileCoord(x, y));
        world.Explored[0] = known;
        return (new Simulation(world, seed: 1), unit);
    }

    private static VisionGrant Activate(Simulation sim, Unit unit)
    {
        sim.SubmitIntent(sim.Now, new ActivateIdolIntent(unit.Id) { PlayerId = 0 });
        sim.Run(until: sim.Now);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied, sim.ResolvedLog[^1].Outcome.ToString());
        return Assert.Single(sim.World.VisionGrants.Values);
    }

    [Theory]
    [InlineData(IdolKind.Lesser)]
    [InlineData(IdolKind.Greater)]
    public void Activating_GrantsACircleOfSight_ForTheGradesTime_AndTheIdolCrumbles(IdolKind grade)
    {
        var (sim, unit) = OnAnIdol(grade);
        var cfg = sim.World.IdolConfig;
        var knownBefore = sim.World.Explored[0].ToHashSet();

        var g = Activate(sim, unit);

        Assert.Equal(0, g.OwnerId);
        Assert.Equal(cfg.RadiusFor(grade), g.Radius);
        Assert.Equal(sim.Now + cfg.TicksFor(grade), g.EndsTick);
        Assert.DoesNotContain(g.Center, knownBefore);                 // somewhere new
        Assert.False(sim.World.Structures.ContainsKey(IdolAt));       // single use

        Assert.Contains(g.Center, View.VisibleTiles(sim.World, 0));
        Assert.True(View.Sees(sim.World, 0, g.Center));
        Assert.True(BanditRules.IsSeenByAnyPlayer(sim.World, g.Center));   // no bandit spawns under it
        Assert.Contains(g.Center, sim.World.Explored[0]);                   // and the map remembers it
    }

    [Fact]
    public void TheCircle_ClosesWhenTheTimeRunsOut_ButTheMapRemembers()
    {
        var (sim, unit) = OnAnIdol();
        var g = Activate(sim, unit);

        sim.Run(until: g.EndsTick - 1);
        Assert.Contains(g.Center, View.VisibleTiles(sim.World, 0));

        sim.Run(until: g.EndsTick);
        Assert.Empty(sim.World.VisionGrants);
        Assert.DoesNotContain(g.Center, View.VisibleTiles(sim.World, 0));
        Assert.False(BanditRules.IsSeenByAnyPlayer(sim.World, g.Center));
        Assert.Contains(g.Center, sim.World.Explored[0]);
    }

    [Fact]
    public void OnlySomeoneStandingOnIt_CanActivateIt()
    {
        var (sim, unit) = OnAnIdol();
        unit.Position = new TileCoord(11, 10);
        sim.SubmitIntent(0, new ActivateIdolIntent(unit.Id) { PlayerId = 0 });
        sim.Run(until: 0);
        Assert.False(sim.ResolvedLog[^1].Outcome.IsApplied);
        Assert.True(sim.World.Structures.ContainsKey(IdolAt));
        Assert.Empty(sim.World.VisionGrants);
    }

    [Fact]
    public void SentToAnIdol_AUnitWalksThere_AndWakesItOnArrival()
    {
        var (sim, unit) = OnAnIdol();
        unit.Position = new TileCoord(3, 3);
        sim.SubmitIntent(0, new ActivateIdolIntent(unit.Id, IdolAt) { PlayerId = 0 });
        sim.Run(until: 0);
        Assert.Equal(GoalKind.ActivateIdol, unit.Goal?.Kind);

        // Walk until it gets there (well inside the idol's own duration).
        for (var t = 0; t < 200 && unit.Position != IdolAt; t++) sim.Run(until: sim.Now + Time.Hour);

        Assert.Equal(IdolAt, unit.Position);
        Assert.Null(unit.Goal);
        Assert.Single(sim.World.VisionGrants.Values);
        Assert.False(sim.World.Structures.ContainsKey(IdolAt));
    }

    [Fact]
    public void AnIdol_ChartsAsAStoneFigure_AndIsStruckWhenItCrumbles()
    {
        var (sim, unit) = OnAnIdol();
        sim.World.Charts[0] = new SortedDictionary<TileCoord, ChartEntry>(TileOrder.Instance)
        {
            [IdolAt] = new ChartEntry { Tile = IdolAt, Hint = SecretHint.StoneFigure, SeenTick = 0 },
        };
        Assert.Equal(SecretHint.StoneFigure, Charts.HintFor(StructureKind.Idol));

        Activate(sim, unit);
        Assert.Equal(ChartState.Gone, sim.World.Charts[0][IdolAt].State);
    }

    [Fact]
    public void ASnapshotMidGrant_ExpiresTheSame()
    {
        var (sim, unit) = OnAnIdol(IdolKind.Greater);
        var g = Activate(sim, unit);
        sim.Run(until: sim.Now + Time.Day);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        sim.Run(until: g.EndsTick);
        restored.Run(until: g.EndsTick);
        Assert.Empty(restored.World.VisionGrants);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void TheHost_Scatters20Idols_InTheFog_SpacedAndAwayFromCastles()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 160, MapHeight = 160, MapSeed = 7, AiPlayers = 1, IdolCount = 20 });
        var sim = new Simulation(build.Spec, seed: 1);
        var cfg = sim.World.IdolConfig;
        var idols = sim.World.Structures.Values.OfType<Idol>().ToList();
        var castles = sim.World.Structures.Values.OfType<Castle>().Select(c => c.At).ToList();
        int Cheb(TileCoord a, TileCoord b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        Assert.Equal(cfg.Count, idols.Count);
        Assert.Equal(cfg.Count / cfg.GreaterEvery, idols.Count(i => i.Grade == IdolKind.Greater));
        foreach (var i in idols)
        {
            Assert.All(castles, c => Assert.True(Cheb(c, i.At) >= cfg.MinDistanceFromCastle));
            Assert.All(idols.Where(o => o != i), o => Assert.True(Cheb(o.At, i.At) >= cfg.MinSpacing));
            Assert.All(sim.World.Explored.Values, seen => Assert.DoesNotContain(i.At, seen));
            Assert.NotEqual(Biome.Water, sim.World.Grid.BiomeAt(i.At));
        }

        // A bare ServerOptions (the AI labs) scatters none.
        var lab = new Simulation(WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1 }).Spec, seed: 1);
        Assert.Empty(lab.World.Structures.Values.OfType<Idol>());
        Assert.Equal(20, ServerOptions.Parse(Array.Empty<string>()).IdolCount);
    }
}
