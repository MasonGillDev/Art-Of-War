using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Rivers;
using Sim.Core.Vision;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server;

namespace Sim.Tests;

// Rivers (docs/rivers.md): a per-tile EDGE mask; a hop pays the crossing
// surcharge iff the shared edge carries a river; along the bank is free.
// Every number here derives from the constants it tests — retune the knobs
// and this file does not move.
public class RiversTests
{
    private static readonly int G = Biomes.MoveCost(Biome.Grassland);
    private static readonly int X = RiverConstants.CrossingCost;

    // A 6x6 grassland world with a river running north–south along the
    // boundary between columns 2 and 3 (east edge of column 2, west edge of
    // column 3), the full height of the map.
    private static Simulation RiverWorld(int size = 6, int fromY = 0, int toY = int.MaxValue)
    {
        var rivers = new Dictionary<TileCoord, RiverEdge>();
        for (var y = fromY; y < Math.Min(size, toY); y++)
        {
            rivers[new TileCoord(2, y)] = RiverEdge.East;
            rivers[new TileCoord(3, y)] = RiverEdge.West;
        }
        var spec = new GenesisSpec
        {
            Width = size, Height = size,
            Rivers = rivers,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(2, 2), UnitRole.Scout) },
                },
            },
        };
        return new Simulation(spec, seed: 11);
    }

    // ---- the crossing rule ------------------------------------------------

    [Fact]
    public void Hop_AcrossRiverEdge_PaysCrossingCost_BothWays()
    {
        var sim = RiverWorld();
        var w = sim.World;
        Assert.Equal(G + X, MovementCost.ExecutionCost(w, new(2, 2), new(3, 2), sim.Now));
        Assert.Equal(G + X, MovementCost.ExecutionCost(w, new(3, 2), new(2, 2), sim.Now));
    }

    [Fact]
    public void Hop_AlongTheBank_PaysPlainTerrain()
    {
        var sim = RiverWorld();
        var w = sim.World;
        // Both tiles carry the river on their east edge; walking N/S between
        // them never crosses it.
        Assert.Equal(G, MovementCost.ExecutionCost(w, new(2, 2), new(2, 3), sim.Now));
        Assert.Equal(G, MovementCost.ExecutionCost(w, new(2, 3), new(2, 2), sim.Now));
        // And stepping AWAY from the river is free too.
        Assert.Equal(G, MovementCost.ExecutionCost(w, new(2, 2), new(1, 2), sim.Now));
    }

    [Fact]
    public void PlanCost_AgreesWithExecutionCost_OnTheRiverTerm()
    {
        var sim = RiverWorld();
        var w = sim.World;
        var visible = View.VisibleTiles(w, 0);
        Assert.Equal(G + X, MovementCost.PlanCost(w, new(2, 2), new(3, 2), 0, visible, sim.Now));
        Assert.Equal(G,     MovementCost.PlanCost(w, new(2, 2), new(2, 3), 0, visible, sim.Now));
    }

    [Fact]
    public void RoadOnTheFarBank_DoesNotReduceTheCrossing()
    {
        var sim = RiverWorld();
        var w = sim.World;
        w.Roads[new TileCoord(3, 2)] = new Sim.Core.Roads.RoadState(Sim.Core.Roads.RoadConstants.CONDITION_MAX, 0);
        var roadCost = Sim.Core.Roads.Road.EffectiveCost(w, new(3, 2), sim.Now);
        Assert.True(roadCost < G);
        Assert.Equal(roadCost + X, MovementCost.ExecutionCost(w, new(2, 2), new(3, 2), sim.Now));
    }

    [Fact]
    public void Boats_IgnoreRivers()
    {
        // A water world with a river edge on it (nonsense geographically, but
        // the rule is the rule): Water traversal reads only the boat table.
        var rivers = new Dictionary<TileCoord, RiverEdge>
        {
            [new TileCoord(1, 1)] = RiverEdge.East,
            [new TileCoord(2, 1)] = RiverEdge.West,
        };
        var spec = new GenesisSpec
        {
            Width = 4, Height = 4, DefaultBiome = Biome.Water, Rivers = rivers,
            FactionStarts = new[] { new FactionStartSpec { CastlePosition = new TileCoord(0, 0) } },
        };
        var sim = new Simulation(spec, seed: 1);
        Assert.Equal(BoatMovementCost.WaterCost,
            MovementCost.ExecutionCost(sim.World, new(1, 1), new(2, 1), sim.Now, Traversal.Water));
    }

    [Fact]
    public void EdgeBetween_NonAdjacentTiles_IsNoEdge()
    {
        Assert.Equal(RiverEdge.None, River.EdgeBetween(new(2, 2), new(2, 2)));
        Assert.Equal(RiverEdge.None, River.EdgeBetween(new(2, 2), new(3, 3)));
        Assert.Equal(RiverEdge.None, River.EdgeBetween(new(2, 2), new(5, 2)));
        Assert.Equal(RiverEdge.North, River.EdgeBetween(new(2, 2), new(2, 1)));
        Assert.Equal(RiverEdge.East,  River.EdgeBetween(new(2, 2), new(3, 2)));
        Assert.Equal(RiverEdge.South, River.EdgeBetween(new(2, 2), new(2, 3)));
        Assert.Equal(RiverEdge.West,  River.EdgeBetween(new(2, 2), new(1, 2)));
    }

    // ---- pathfinding --------------------------------------------------------

    [Fact]
    public void AStar_DetoursAroundTheRiver_WhenTheDetourIsCheaper()
    {
        // River between columns 2|3 from y=0..3 only; rows 4,5 are open. From
        // (2,3) to (3,3): straight across costs G + X; around the end costs
        // 3G (down, across, up). The detour wins iff X > 2G — which the
        // default knob satisfies, and the test says so rather than assuming.
        Assert.True(X > 2 * G, "CrossingCost must exceed two grassland hops for this scenario");
        var sim = RiverWorld(size: 6, toY: 4);
        var w = sim.World;
        var path = Pathfinding.FindPath(w.Grid, new(2, 3), new(3, 3),
            MovementCost.Planner(w, 0, View.VisibleTiles(w, 0), sim.Now));
        Assert.NotNull(path);
        Assert.Equal(4, path!.Count);
        Assert.Contains(new TileCoord(2, 4), path);
        Assert.Contains(new TileCoord(3, 4), path);
    }

    [Fact]
    public void AStar_Fords_WhenTheDetourIsDearer()
    {
        // Full-height river: no way around. The path crosses exactly once.
        var sim = RiverWorld();
        var w = sim.World;
        var path = Pathfinding.FindPath(w.Grid, new(0, 2), new(5, 2),
            MovementCost.Planner(w, 0, View.VisibleTiles(w, 0), sim.Now));
        Assert.NotNull(path);
        Assert.Equal(6, path!.Count);
        var crossings = 0;
        for (var i = 1; i < path.Count; i++)
            if (River.Crosses(w.Grid, path[i - 1], path[i])) crossings++;
        Assert.Equal(1, crossings);
    }

    [Fact]
    public void MoveIntent_HopAcrossRiver_TakesLonger()
    {
        // The unit at (2,2) walks to (3,2): the arrival is scheduled G + X
        // ticks out, not G. The river is real travel time, not a route hint.
        var sim = RiverWorld();
        var unit = sim.World.Units[1];
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(3, 2)) { PlayerId = 0 });
        sim.Run(until: 0);
        Assert.Equal(G + X, unit.NextArrivalTick);
    }

    // ---- pure-read wall --------------------------------------------------------

    [Fact]
    public void RiverReads_ArePureReads_NoMutation()
    {
        var sim = RiverWorld();
        var w = sim.World;
        var before = Snapshot.Hash(sim);
        var visible = View.VisibleTiles(w, 0);
        for (var i = 0; i < 100; i++)
        {
            River.Crosses(w.Grid, new(2, 2), new(3, 2));
            River.CrossingCostFor(w.Grid, new(3, 2), new(2, 2));
            MovementCost.PlanCost(w, new(2, 2), new(3, 2), 0, visible, sim.Now);
            Pathfinding.FindPath(w.Grid, new(0, 0), new(5, 5), MovementCost.Planner(w, 0, visible, sim.Now));
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // ---- genesis + snapshot -------------------------------------------------

    [Fact]
    public void Genesis_RejectsAnAsymmetricMask()
    {
        var spec = new GenesisSpec
        {
            Width = 4, Height = 4,
            Rivers = new Dictionary<TileCoord, RiverEdge> { [new TileCoord(1, 1)] = RiverEdge.East },
            FactionStarts = new[] { new FactionStartSpec { CastlePosition = new TileCoord(0, 0) } },
        };
        Assert.Throws<InvalidOperationException>(() => Genesis.Build(spec));
    }

    [Fact]
    public void Genesis_RejectsARiverOnTheMapBorder()
    {
        var spec = new GenesisSpec
        {
            Width = 4, Height = 4,
            Rivers = new Dictionary<TileCoord, RiverEdge> { [new TileCoord(0, 1)] = RiverEdge.West },
            FactionStarts = new[] { new FactionStartSpec { CastlePosition = new TileCoord(0, 0) } },
        };
        Assert.Throws<InvalidOperationException>(() => Genesis.Build(spec));
    }

    [Fact]
    public void Snapshot_RoundTripsTheMask_AndHashesIt()
    {
        var sim = RiverWorld();
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 11);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(RiverEdge.East, restored.World.Grid.RiverEdgesAt(new(2, 2)));
        Assert.Equal(RiverEdge.West, restored.World.Grid.RiverEdgesAt(new(3, 2)));
        Assert.Equal(RiverEdge.None, restored.World.Grid.RiverEdgesAt(new(0, 0)));

        // Two worlds differing only in rivers must not hash equal — the
        // crossing fee is a function of the mask.
        var dry = RiverWorld(toY: 0);
        Assert.NotEqual(Snapshot.Hash(sim), Snapshot.Hash(dry));
    }

    // ---- generation ----------------------------------------------------------

    private static GenerationConfig Cfg(int seed) => new() { Seed = seed, Width = 64, Height = 64 };

    [Theory]
    [InlineData(42)] [InlineData(7)] [InlineData(1151)] [InlineData(299501)] [InlineData(3)]
    public void Generated_MaskIsSymmetric_AndOffTheBorder(int seed)
    {
        var map = MapGenerator.Build(Cfg(seed));
        // Genesis.Build is the validator; a throw here is the failure.
        var grid = Genesis.Build(new GenesisSpec
        {
            Width = map.Width, Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            Rivers = MapGenerator.ToRiverOverrides(map),
            FactionStarts = new[] { new FactionStartSpec { CastlePosition = map.Start } },
        }).Grid;
        Assert.Equal(map.Rivers[10, 10], grid.RiverEdgesAt(new(10, 10)));
    }

    [Theory]
    [InlineData(42)] [InlineData(7)] [InlineData(1151)] [InlineData(299501)] [InlineData(3)]
    public void Generated_RiversExist_AndNeverTouchWater(int seed)
    {
        var map = MapGenerator.Build(Cfg(seed));
        var riverTiles = 0;
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                if (map.Rivers[x, y] == RiverEdge.None) continue;
                riverTiles++;
                Assert.NotEqual(Biome.Water, map.Grid[x, y]);
            }
        Assert.True(riverTiles > 0, $"seed {seed} carved no rivers");
    }

    [Theory]
    [InlineData(42)] [InlineData(7)] [InlineData(1151)] [InlineData(299501)] [InlineData(3)]
    public void Generated_EveryRiver_ReachesTheSea(int seed)
    {
        // Walk the corner graph of river segments; every connected component
        // must contain a corner that touches a Water tile (a mouth).
        var map = MapGenerator.Build(Cfg(seed));
        var segments = new HashSet<((int, int), (int, int))>();
        var corners = new HashSet<(int, int)>();
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var m = map.Rivers[x, y];
                // Only two of the four bits define segments (the neighbour holds the mirror).
                if ((m & RiverEdge.North) != 0) Add(segments, corners, (x, y), (x + 1, y));
                if ((m & RiverEdge.West)  != 0) Add(segments, corners, (x, y), (x, y + 1));
                if ((m & RiverEdge.South) != 0) Add(segments, corners, (x, y + 1), (x + 1, y + 1));
                if ((m & RiverEdge.East)  != 0) Add(segments, corners, (x + 1, y), (x + 1, y + 1));
            }
        var adj = new Dictionary<(int, int), List<(int, int)>>();
        foreach (var (a, b) in segments)
        {
            adj.TryAdd(a, new()); adj.TryAdd(b, new());
            adj[a].Add(b); adj[b].Add(a);
        }
        bool TouchesWater((int x, int y) c)
        {
            for (var oy = -1; oy <= 0; oy++)
                for (var ox = -1; ox <= 0; ox++)
                {
                    int tx = c.x + ox, ty = c.y + oy;
                    if (tx < 0 || ty < 0 || tx >= map.Width || ty >= map.Height) return true; // off-map drains too
                    if (map.Grid[tx, ty] == Biome.Water) return true;
                }
            return false;
        }
        var seen = new HashSet<(int, int)>();
        var components = 0;
        foreach (var start in corners)
        {
            if (!seen.Add(start)) continue;
            components++;
            var stack = new Stack<(int, int)>(); stack.Push(start);
            var reachesSea = false;
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (TouchesWater(c)) reachesSea = true;
                foreach (var n in adj[c]) if (seen.Add(n)) stack.Push(n);
            }
            Assert.True(reachesSea, $"seed {seed}: river component at corner {start} never reaches water");
        }
        Assert.True(components > 0);
    }

    private static void Add(HashSet<((int, int), (int, int))> segs, HashSet<(int, int)> corners,
                            (int, int) a, (int, int) b)
    {
        segs.Add((a, b)); corners.Add(a); corners.Add(b);
    }

    [Fact]
    public void Generated_SameConfig_SameRivers()
    {
        var a = MapGenerator.Build(Cfg(42));
        var b = MapGenerator.Build(Cfg(42));
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                Assert.Equal(a.Rivers[x, y], b.Rivers[x, y]);
    }

    [Fact]
    public void Generated_RiverCountZero_CarvesNothing()
    {
        var map = MapGenerator.Build(new GenerationConfig { Seed = 42, Width = 64, Height = 64, RiverCount = 0 });
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                Assert.Equal(RiverEdge.None, map.Rivers[x, y]);
    }

    [Fact]
    public void GeneratedWorld_TwinRun_HashesEqual()
    {
        Simulation Run()
        {
            var map = MapGenerator.Build(Cfg(42));
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
                        UnitSpawns = new[] { new UnitSpawn(1, map.Start, UnitRole.Scout) },
                    },
                },
            }, seed: 5);
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(map.Width - 3, map.Height - 3)) { PlayerId = 0 });
            sim.Run(until: 2000);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    // ---- wire ------------------------------------------------------------------

    [Fact]
    public void WorldDto_River_MirrorsTheGeneratedMask()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 0 });
        var sim = new Simulation(build.Spec, seed: 1);
        var dto = new ViewProjector(build).BuildWorldDto();
        Assert.Equal(dto.Width * dto.Height, dto.River.Length);
        var any = false;
        for (var y = 0; y < dto.Height; y++)
            for (var x = 0; x < dto.Width; x++)
            {
                var expected = (int)sim.World.Grid.RiverEdgesAt(new(x, y));
                Assert.Equal(expected, dto.River[y * dto.Width + x]);
                if (expected != 0) any = true;
            }
        Assert.True(any);
    }
}
