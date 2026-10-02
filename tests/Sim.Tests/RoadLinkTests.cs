using Sim.Core.Battlefields;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server;

namespace Sim.Tests;

// M43 step 3 (docs/subtile-movement.md, "roads live on subtiles"): a road is a set of
// worn LINKS between neighbouring subtiles. Traffic wears the steps a unit takes and a
// worn step is cheaper, so trails form where people really walk. These replace the M2/M34
// traffic, emergence, pathfinding and arc tests; the cost and decay maths (RoadCostTests,
// RoadDecayTests) keep their own files.
public class RoadLinkTests
{
    // A one-row world: every walk runs along subtile row y = 1 (the centre-first row a
    // unit is seated on), so the links walked are easy to name.
    private static (Simulation sim, GameWorld world) Strip(int tiles = 5)
    {
        var grid = new TileGrid(tiles, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        return (new Simulation(world, seed: 1), world);
    }

    private static SubtileLink Link(int x, int y = 1) => SubtileLink.FromOwner(new WorldSubtile(x, y), SubtileLink.Axis.East);

    // The steps of a walk from the seat (0, 1) to the centre-out subtile (1, 1) of tile `tile`.
    private static int WalkLength(int tile) => tile * Subtile.Size + 1;

    // ---- the link ------------------------------------------------------------------------

    [Fact]
    public void ALink_IsCanonicalAndSymmetric_AndOnlyJoinsNeighbours()
    {
        var a = new WorldSubtile(3, 5);
        var b = new WorldSubtile(4, 5);
        Assert.Equal(SubtileLink.Between(a, b), SubtileLink.Between(b, a));
        Assert.Equal(a, SubtileLink.Between(b, a).A);                          // the west subtile owns it
        var south = SubtileLink.Between(new WorldSubtile(3, 6), new WorldSubtile(3, 5));
        Assert.Equal(new WorldSubtile(3, 5), south.A);
        Assert.Equal(SubtileLink.Axis.South, south.Direction);
        Assert.False(SubtileLink.TryBetween(a, a, out _));
        Assert.False(SubtileLink.TryBetween(a, new WorldSubtile(5, 6), out _));   // a diagonal is no link
        Assert.True(SubtileLink.Between(a, b).Touches(new TileCoord(0, 1)));      // it crosses the edge between two tiles
        Assert.True(SubtileLink.Between(a, b).Touches(new TileCoord(1, 1)));
        Assert.False(SubtileLink.Between(a, b).Touches(new TileCoord(2, 1)));
    }

    [Fact]
    public void CreditingANonAdjacentPair_CreditsNothing()
    {
        var (_, world) = Strip();
        Road.CreditTraffic(world, new WorldSubtile(1, 1), new WorldSubtile(1, 1), 0);
        Road.CreditTraffic(world, new WorldSubtile(1, 1), new WorldSubtile(2, 2), 0);
        Assert.Empty(world.Roads);
    }

    // ---- traffic wears the steps walked ---------------------------------------------------

    [Fact]
    public void AWalk_WearsEveryLinkItSteppedOn_OnceEach()
    {
        var (sim, world) = Strip();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run();

        Assert.Equal(new TileCoord(4, 0), world.Units[1].Position);
        var steps = WalkLength(4);
        for (var x = 0; x < steps; x++)
        {
            Assert.True(world.Roads.ContainsKey(Link(x)), $"link ({x},1)-({x + 1},1) should be worn");
            Assert.Equal(RoadConstants.BASE_GAIN, world.Roads[Link(x)].Condition);
        }
        Assert.Equal(steps, world.Roads.Count);
    }

    [Fact]
    public void ARetaskedWalk_WearsNoLinkTwice()
    {
        // Retasking to the same destination replaces the walk from where the unit stands: the
        // steps the first walk took are worn once, the rest by the second, none twice.
        var (sim, world) = Strip();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run(until: 40);
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run();

        foreach (var (link, road) in world.Roads)
            Assert.True(road.Condition <= RoadConstants.BASE_GAIN + 1, $"{link.A}-{link.B} worn too often: {road.Condition}");
    }

    [Fact]
    public void ARoad_MakesTheSameWalkFaster_ByTheDerivedStepCosts()
    {
        long Walk(bool road)
        {
            var (sim, world) = Strip();
            if (road)
                for (var x = 0; x < WalkLength(4); x++)
                    world.Roads[Link(x)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
            world.AddUnit(new Unit(1, new TileCoord(0, 0)));
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
            sim.Run(until: 1);   // credit wear happens after the first step; read the plan's price now
            var expected = 0L;
            var at = new WorldSubtile(0, 1);
            foreach (var step in world.Units[1].SubtileRoute!.Prepend(at).Zip(world.Units[1].SubtileRoute!, (a, b) => (a, b)))
                expected += SubtileStepRules.StepCost(world, Traversal.Foot, step.a, step.b, 0);
            return expected;
        }

        var raw = Walk(road: false);
        var roaded = Walk(road: true);
        Assert.True(roaded < raw, $"a worn road should be faster: road={roaded}, raw={raw}");
        var plain = (int)SubtileStepRules.StepTicks(new GameWorld(new TileGrid(5, 1, Biome.Grassland)), new TileCoord(1, 0));
        var worn = Math.Max(RoadConstants.MIN_COST, plain - (int)((long)plain * RoadConstants.MAX_REDUCTION_PERCENT / 100L));
        Assert.Equal(WalkLength(4) * plain, raw);
        Assert.Equal(WalkLength(4) * worn, roaded);
    }

    [Fact]
    public void AWalkOnARoad_TakesExactlyTheSumOfItsStepCosts()
    {
        var (sim, world) = Strip();
        for (var x = 0; x < WalkLength(4); x++) world.Roads[Link(x)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run();
        var plain = (int)SubtileStepRules.StepTicks(world, new TileCoord(1, 0));
        var worn = Math.Max(RoadConstants.MIN_COST, plain - (int)((long)plain * RoadConstants.MAX_REDUCTION_PERCENT / 100L));
        Assert.Equal(WalkLength(4) * worn, sim.Now);
        Assert.True(sim.Now >= WalkLength(4) * RoadConstants.MIN_COST);
    }

    // ---- roads steer the path ---------------------------------------------------------------

    [Fact]
    public void ARoad_IsPreferredOverAShorterRawRoute()
    {
        // A forest band across the way: through it is shorter, but a worn road round it is cheaper.
        var grid = new TileGrid(8, 3, Biome.Grassland);
        for (var y = 0; y < 3; y++) grid.SetBiome(new TileCoord(4, y), Biome.Forest);
        var world = new GameWorld(grid);
        var mover = new StepMover(0, Traversal.Foot, false);
        var start = WorldSubtile.Of(new TileCoord(0, 1), new Subtile(1, 1));
        var goal = WorldSubtile.Of(new TileCoord(7, 1), new Subtile(1, 1));

        var raw = SubtilePathfinder.Find(world, mover, start, goal)!;
        Assert.DoesNotContain(raw, s => s.Y == 1);                         // straight through row 5 (subtile row 5), not up on the road row

        // A worn road along the top row (subtile row 1) and its ramps at both ends.
        for (var x = 0; x < 8 * Subtile.Size - 1; x++) world.Roads[Link(x, 1)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        for (var y = 1; y < start.Y; y++)
        {
            world.Roads[SubtileLink.FromOwner(new WorldSubtile(start.X, y), SubtileLink.Axis.South)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
            world.Roads[SubtileLink.FromOwner(new WorldSubtile(goal.X, y), SubtileLink.Axis.South)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        }
        var roaded = SubtilePathfinder.Find(world, mover, start, goal)!;
        Assert.Contains(roaded, s => s.Y == 1);                            // it climbs onto the road
    }

    [Fact]
    public void PathSearches_ArePureReads_AcrossARoadSet()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = new GameWorld(grid);
        for (var i = 0; i < 5; i++) world.Roads[Link(i, i)] = new RoadState(300 + 100 * i, 7);
        var sim = new Simulation(world, seed: 1);
        var before = Snapshot.Hash(sim);
        var mover = new StepMover(0, Traversal.Foot, false);
        var a = WorldSubtile.Of(new TileCoord(0, 0), new Subtile(1, 1));
        var b = WorldSubtile.Of(new TileCoord(7, 7), new Subtile(1, 1));
        for (var i = 0; i < 100; i++) SubtilePathfinder.Find(world, mover, a, b);
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // ---- emergence: one walker can't make a road, sustained traffic does --------------------

    [Fact]
    public void ASingleWalk_ThenLongSilence_LeavesNoRoad()
    {
        var (sim, world) = Strip(3);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(2, 0)));
        sim.Run();
        var link = Link(3);   // the step across the edge into tile 1
        Assert.True(world.Roads.ContainsKey(link));
        // Decay is 1 per DECAY_PERIOD; the credit is BASE_GAIN, so BASE_GAIN periods erase it.
        Road.CatchUpDecay(world, link, now: sim.Now + (long)RoadConstants.BASE_GAIN * RoadConstants.DECAY_PERIOD + 1);
        Assert.False(world.Roads.ContainsKey(link));
    }

    [Fact]
    public void SustainedTraffic_BuildsALastingRoad()
    {
        var (_, world) = Strip(3);
        var link = Link(3);
        long now = 0;
        for (var i = 0; i < 20; i++)
        {
            Road.CreditTraffic(world, link, now);
            now += RoadConstants.DECAY_PERIOD;
        }
        Assert.True(world.Roads[link].Condition > RoadConstants.BASE_GAIN,
            $"sustained traffic should exceed single-traversal level; got {world.Roads[link].Condition}");
    }

    [Fact]
    public void TwoStepsOnTheSameLinkTheSameTick_TheSecondSeesTheFirstsGain()
    {
        var (_, world) = Strip();
        var link = Link(3);
        Road.CreditTraffic(world, link, 100);
        var afterFirst = world.Roads[link].Condition;
        Road.CreditTraffic(world, link, 100);
        var secondGain = world.Roads[link].Condition - afterFirst;
        Assert.True(secondGain < afterFirst, "the second same-tick gain should be smaller (diminishing returns)");
        Assert.True(secondGain >= RoadConstants.GAIN_FLOOR);
    }

    // ---- water clears the links ---------------------------------------------------------------

    [Fact]
    public void AFloodedTile_LosesEveryLinkTouchingIt_AndNoOther()
    {
        var (_, world) = Strip(6);
        var flooded = new TileCoord(2, 0);
        var inside = Link(2 * Subtile.Size + 1);              // both ends inside tile 2
        var acrossEdge = Link(3 * Subtile.Size - 1);           // out of tile 2 into tile 3
        var beside = Link(4 * Subtile.Size + 1);               // tile 4 only
        foreach (var l in new[] { inside, acrossEdge, beside }) world.Roads[l] = new RoadState(500, 0);
        Road.RemoveOnTile(world, flooded);
        Assert.False(world.Roads.ContainsKey(inside));
        Assert.False(world.Roads.ContainsKey(acrossEdge));
        Assert.True(world.Roads.ContainsKey(beside));
    }

    // ---- persistence and determinism -----------------------------------------------------------

    [Fact]
    public void ARoadedWorld_RoundTripsAMidWalkSnapshot()
    {
        var (sim, world) = Strip(8);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(7, 0)));
        sim.Run(until: 100);
        Assert.True(world.Roads.Count > 0);
        Assert.True(world.Units[1].IsWalking);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(world.Roads.Count, restored.World.Roads.Count);
        sim.Run();
        restored.Run();
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void GeneratedWorld_WithTraffic_TwinRunHashesEqual()
    {
        Simulation Run()
        {
            var map = MapGenerator.Build(new GenerationConfig { Seed = 42, Width = 64, Height = 64 });
            var sim = new Simulation(new GenesisSpec
            {
                Width = map.Width, Height = map.Height,
                Biomes = MapGenerator.ToBiomeOverrides(map),
                Rivers = MapGenerator.ToRiverOverrides(map),
                FactionStarts = new[]
                {
                    new FactionStartSpec
                    {
                        CastlePosition = map.Start,
                        UnitSpawns = new[]
                        {
                            new UnitSpawn(1, map.Start, UnitRole.Scout),
                            new UnitSpawn(2, map.Start, UnitRole.Scout),
                        },
                    },
                },
            }, seed: 5);
            // Feet never wade: send each scout to the farthest land tile toward a corner that it can
            // actually walk to (the map is an island in an ocean).
            foreach (var (id, corner) in new[] { (1, new TileCoord(map.Width - 1, map.Height - 1)), (2, new TileCoord(0, map.Height - 1)) })
            {
                var scout = sim.World.Units[id];
                var goal = Enumerable.Range(0, map.Width * map.Height)
                    .Select(i => new TileCoord(i % map.Width, i / map.Width))
                    .Where(t => sim.World.Grid.BiomeAt(t) != Biome.Water && sim.World.Grid.TerrainCost(t) < Biomes.Impassable)
                    .OrderBy(t => Math.Abs(t.X - corner.X) + Math.Abs(t.Y - corner.Y)).ThenBy(t => t.Y).ThenBy(t => t.X)
                    .First(t => TestMarch.PathTo(sim.World, scout, t) is { Count: > 0 });
                sim.SubmitIntent(0, new MoveIntent(id, goal) { PlayerId = 0 });
            }
            sim.Run(until: 3000);
            return sim;
        }
        var x = Run();
        var y = Run();
        Assert.Equal(Snapshot.Hash(x), Snapshot.Hash(y));
        Assert.NotEmpty(x.World.Roads);
        Assert.All(x.World.Roads.Keys, e => Assert.Equal(1, Math.Abs(e.A.X - e.B.X) + Math.Abs(e.A.Y - e.B.Y)));
    }

    [Fact]
    public void TheWire_ShipsALink_WhenEitherEndsTileIsExplored()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7 });
        var sim = new Simulation(build.Spec, seed: 9);
        var projector = new ViewProjector(build);
        var world = sim.World;

        // Two land tiles far from anyone's sight, and the link across the edge between them.
        var a = new TileCoord(40, 40);
        var b = new TileCoord(41, 40);
        world.Grid.SetBiome(a, Biome.Grassland);
        world.Grid.SetBiome(b, Biome.Grassland);
        var owner = new WorldSubtile(a.X * Subtile.Size + 3, a.Y * Subtile.Size + 1);   // the last subtile of a, row 1
        world.Roads[SubtileLink.FromOwner(owner, SubtileLink.Axis.East)] = new RoadState(400, 0);
        var explored = world.Explored.TryGetValue(0, out var set) ? set : world.Explored[0] = new HashSet<TileCoord>();
        explored.Remove(a); explored.Remove(b);

        Sim.Server.Wire.RoadDto? Find(Sim.Server.Wire.RoadDto[] roads) =>
            roads.FirstOrDefault(r => r.X == owner.X && r.Y == owner.Y && r.Axis == (int)SubtileLink.Axis.East);

        Assert.Null(Find(projector.ProjectV2(sim, sim.Now, 0, reveal: false).Roads));
        explored.Add(b);   // only the far end
        var seen = Find(projector.ProjectV2(sim, sim.Now, 0, reveal: false).Roads);
        Assert.NotNull(seen);
        Assert.Equal(400, seen!.Condition);
    }
}
