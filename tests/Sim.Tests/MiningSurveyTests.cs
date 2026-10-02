using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Mining;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M44 — ore veins, surveying and the Mine's vein gate
// (docs/m44-stone-and-ore-spec.md, docs/stone-and-ore-land.md).
// Expectations derive from VeinConfig and the catalog, never hard-coded
// ticks or densities (config knobs get retuned).
public class MiningSurveyTests
{
    private static readonly TileCoord Keep = new(4, 4);
    private static readonly TileCoord Slope = new(14, 6);

    // A meadow with a mountain block (x 12..18, y 2..10), one castle per
    // faction, one Miner per faction at its keep, the whole map explored.
    // Veins are laid by hand (genesis seeding is covered separately).
    private static Simulation BuildWorld(params TileCoord[] veins)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        for (var y = 2; y <= 10; y++)
            for (var x = 12; x <= 18; x++)
                grid.SetBiome(new TileCoord(x, y), Biome.Mountain);
        var world = new GameWorld(grid);
        foreach (var v in veins) world.Veins.Add(v);

        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        world.AddStructure(new Castle(new TileCoord(4, 18)) { OwnerId = 1 });
        world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Miner, OwnerId = 0 });
        world.AddUnit(new Unit(2, new TileCoord(4, 18)) { Role = UnitRole.Miner, OwnerId = 1 });
        world.AddUnit(new Unit(3, Keep) { Role = UnitRole.Farmer, OwnerId = 0 });
        world.NextUnitId = 4;

        foreach (var p in new[] { 0, 1 })
        {
            var explored = new HashSet<TileCoord>();
            for (var y = 0; y < 24; y++)
                for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
            world.Explored[p] = explored;
        }
        return new Simulation(world, seed: 11);
    }

    private static long Plenty(Simulation sim) => sim.Now + 20 * Time.Day;

    // ---- the survey -------------------------------------------------------

    [Fact]
    public void Survey_WalksDigsAndReports_TheNearestVein_ProvesNearerSlopesBarren()
    {
        var near = new TileCoord(15, 7);   // distance 1 from the slope
        var far = new TileCoord(16, 6);    // distance 2
        var sim = BuildWorld(near, far);
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(Plenty(sim));

        var miner = sim.World.Units[1];
        Assert.Null(miner.Survey);
        Assert.Equal(Activity.Idle, miner.Activity);
        Assert.Equal(Slope, miner.Position);
        Assert.True(Veins.Knows(sim.World, 0, near));
        Assert.False(Veins.Knows(sim.World, 0, far), "one sweep reports one vein — the nearest");
        // Only the slope itself was nearer than the find.
        Assert.True(Veins.ProvenBarren(sim.World, 0, Slope));
        Assert.False(Veins.ProvenBarren(sim.World, 0, new TileCoord(13, 6)));
        Assert.Contains(sim.ResolvedLog, e => e is SurveyReportEvent r && r.Vein == near && r.OwnerId == 0);

        // A second sweep of the same slope moves on to the next unknown vein.
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(Plenty(sim));
        Assert.True(Veins.Knows(sim.World, 0, far));
        Assert.False(Veins.ProvenBarren(sim.World, 0, near), "a known vein is not barren");
        Assert.True(Veins.ProvenBarren(sim.World, 0, new TileCoord(13, 6)));
    }

    [Fact]
    public void Survey_TakesSurveyTicks_OnceThere()
    {
        var sim = BuildWorld(new TileCoord(15, 7));
        sim.World.Units[1].Position = Slope;   // already standing on it
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(0);
        var plan = sim.World.Units[1].Survey!;
        Assert.Equal(sim.World.VeinConfig.SurveyTicks, plan.CompleteTick);
        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);

        sim.Run(sim.World.VeinConfig.SurveyTicks - 1);
        Assert.False(Veins.Knows(sim.World, 0, new TileCoord(15, 7)));
        sim.Run(sim.World.VeinConfig.SurveyTicks);
        Assert.True(Veins.Knows(sim.World, 0, new TileCoord(15, 7)));
    }

    [Fact]
    public void Survey_NothingInReach_ProvesTheWholeSweepBarren()
    {
        var sim = BuildWorld(new TileCoord(18, 10));   // out of reach of the slope
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(Plenty(sim));

        var r = sim.World.VeinConfig.SurveyRadius;
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
                Assert.True(Veins.ProvenBarren(sim.World, 0, new TileCoord(Slope.X + dx, Slope.Y + dy)));
        Assert.Contains(sim.ResolvedLog, e => e is SurveyReportEvent { Vein: null, Abandoned: null });
        Assert.Empty(sim.World.KnownVeins);
    }

    [Theory]
    [InlineData(3, 14, 6)]    // a Farmer: only a Miner knows ore
    [InlineData(1, 8, 6)]     // grassland: ore is a mountain resource
    public void Survey_Rejected_ForNonMiners_AndOffTheMountains(int unitId, int x, int y)
    {
        var sim = BuildWorld(new TileCoord(15, 7));
        var outcome = new SurveyIntent(unitId, new TileCoord(x, y)) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.Null(sim.World.Units[unitId].Survey);
    }

    [Fact]
    public void Survey_CountermandedByANewOrder_AbandonsOutLoud_StaleEventNoOps()
    {
        var sim = BuildWorld(new TileCoord(15, 7));
        sim.World.Units[1].Position = Slope;
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(sim.World.VeinConfig.SurveyTicks / 2);   // mid-dig

        sim.SubmitIntent(sim.Now, new MoveIntent(1, Keep) { PlayerId = 0 });
        sim.Run(Plenty(sim));

        Assert.Null(sim.World.Units[1].Survey);
        Assert.Empty(sim.World.KnownVeins);   // the dig's event fired and fenced out
        Assert.Contains(sim.ResolvedLog, e => e is SurveyReportEvent { Abandoned: not null });
        Assert.Equal(Keep, sim.World.Units[1].Position);
    }

    [Fact]
    public void Survey_KnowledgeIsPerFaction()
    {
        var vein = new TileCoord(15, 7);
        var sim = BuildWorld(vein);
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(Plenty(sim));
        Assert.True(Veins.Knows(sim.World, 0, vein));
        Assert.False(Veins.Knows(sim.World, 1, vein));

        // The other faction surveys the same slopes and learns it on its own.
        sim.SubmitIntent(sim.Now, new SurveyIntent(2, Slope) { PlayerId = 1 });
        sim.Run(Plenty(sim));
        Assert.True(Veins.Knows(sim.World, 1, vein));
    }

    // ---- snapshot ------------------------------------------------------------

    [Fact]
    public void Snapshot_MidDig_RoundTrips_AndFinishesIdentically()
    {
        var sim = BuildWorld(new TileCoord(15, 7), new TileCoord(16, 6));
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        // Run until the dig has started, then a little more.
        for (var guard = 0; sim.World.Units[1].Survey is { CompleteTick: null } && guard < 24 * 20; guard++)
            sim.Run(sim.Now + Time.Hour);
        sim.Run(sim.Now + Time.Hour);
        Assert.NotNull(sim.World.Units[1].Survey);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 11);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var end = Plenty(sim);
        sim.Run(end);
        restored.Run(end);
        Assert.True(Veins.Knows(restored.World, 0, new TileCoord(15, 7)));
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    // ---- the Mine's vein gate ------------------------------------------------

    [Fact]
    public void Mine_OnlyOnAKnownVein()
    {
        var vein = new TileCoord(15, 7);
        var sim = BuildWorld(vein);

        var unknown = new PlaceSiteIntent(vein, StructureKind.Mine) { PlayerId = 0 }.Resolve(sim);
        Assert.False(unknown.IsApplied);
        Assert.Contains("survey", unknown.Reason);
        var bareRock = new PlaceSiteIntent(new TileCoord(13, 3), StructureKind.Mine) { PlayerId = 0 }.Resolve(sim);
        Assert.False(bareRock.IsApplied);

        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(Plenty(sim));
        var known = new PlaceSiteIntent(vein, StructureKind.Mine) { PlayerId = 0 }.Resolve(sim);
        Assert.True(known.IsApplied, known.Reason);
    }

    [Fact]
    public void SeeingAMine_TeachesItsVein_ForGood()
    {
        var vein = new TileCoord(15, 7);
        var sim = BuildWorld(vein);
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(Plenty(sim));

        // Faction 1's Miner stands watching the slope when the mine goes up.
        sim.World.Units[2].Position = new TileCoord(14, 8);
        Assert.True(Sim.Core.Vision.View.Sees(sim.World, 1, vein));
        Assert.True(new PlaceSiteIntent(vein, StructureKind.Mine) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(Veins.Knows(sim.World, 1, vein));

        // And it stays known after the mine is gone.
        sim.World.Structures.Remove(vein);
        Assert.True(Veins.Knows(sim.World, 1, vein));
    }

    // ---- the wire -----------------------------------------------------------

    [Fact]
    public void View_CarriesOnlyTheViewersOwnVeins_AndTheMineNeedsOne()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1 });
        var sim = new Simulation(build.Spec, seed: 3);
        var projector = new ViewProjector(build);
        var vein = sim.World.Veins.First();
        Veins.Learn(sim.World, 0, vein);

        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var theirs = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        Assert.Contains(mine.Veins, v => v.X == vein.X && v.Y == vein.Y && !v.Mined);
        Assert.Empty(theirs.Veins);

        var hash = Snapshot.Hash(sim);
        for (var i = 0; i < 50; i++) projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        Assert.Equal(hash, Snapshot.Hash(sim));   // the vein block is a pure read

        var option = projector.BuildWorldDto().Buildable.Single(o => o.Kind == (int)StructureKind.Mine);
        Assert.True(option.RequiresVein);
    }

    // ---- genesis seeding --------------------------------------------------

    [Fact]
    public void Genesis_LaysAboutOneInOneIn_AndEveryRangeHasAVein()
    {
        // The production map size (252x252, the ServerOptions default).
        foreach (var seed in new[] { 233301431, 7, 11, 5555 })
        {
            var build = WorldFactory.Build(ServerOptions.Parse(new[] { "--mapseed", seed.ToString() }));
            var world = Genesis.Build(build.Spec);
            var grid = world.Grid;
            var cfg = world.VeinConfig;

            var mountains = 0;
            for (var y = 0; y < grid.Height; y++)
                for (var x = 0; x < grid.Width; x++)
                    if (grid.BiomeAt(new TileCoord(x, y)) == Biome.Mountain) mountains++;
            Assert.All(world.Veins, v => Assert.Equal(Biome.Mountain, grid.BiomeAt(v)));
            Assert.True(mountains > 0);

            // Density: within a loose band of 1/OneIn (the safety net can only add).
            var share = (double)world.Veins.Count / mountains;
            Assert.InRange(share, 0.8 / cfg.OneIn, 1.0 / cfg.OneIn + 0.15);

            // Safety net: every 8-connected mountain range holds a vein.
            var seen = new HashSet<TileCoord>();
            for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var start = new TileCoord(x, y);
                if (grid.BiomeAt(start) != Biome.Mountain || !seen.Add(start)) continue;
                var hasVein = false;
                var stack = new Stack<TileCoord>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    hasVein |= world.Veins.Contains(t);
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var n = new TileCoord(t.X + dx, t.Y + dy);
                        if (grid.InBounds(n) && grid.BiomeAt(n) == Biome.Mountain && seen.Add(n)) stack.Push(n);
                    }
                }
                Assert.True(hasVein, $"seed {seed}: the range at {start.X},{start.Y} has no vein");
            }
        }
    }

    [Fact]
    public void Genesis_VeinSeeding_DrawsNoSimRng()
    {
        var build = WorldFactory.Build(ServerOptions.Parse(new[] { "--mapseed", "7" }));
        var a = new Simulation(build.Spec, seed: 3);
        var b = new Simulation(build.Spec with { Veins = build.Spec.Veins with { OneIn = 2 } }, seed: 3);
        Assert.NotEqual(a.World.Veins.Count, b.World.Veins.Count);
        Assert.Equal(a.Rng.State, b.Rng.State);
    }
}
