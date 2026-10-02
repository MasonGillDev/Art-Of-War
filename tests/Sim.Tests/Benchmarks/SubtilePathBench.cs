using System.Diagnostics;
using Sim.Core.Battlefields;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;
using Xunit.Abstractions;

namespace Sim.Tests.Benchmarks;

// M43 (docs/m43-status.md): what does the PRODUCTION subtile pathfinder cost on a
// real-size map? Runs only with AOW_BENCH=1 (dotnet test -c Release --filter
// SubtilePathBench). Compares, for the same start and goal subtiles:
//   whole    — SubtilePathfinder.Find(useCorridor: false): A* over every subtile;
//   corridor — SubtilePathfinder.Find: the shape-aware tile route, then A* over its
//              tiles plus a one-tile margin, the whole grid if that finds nothing.
// Two maps: the default 128×128 with the AI players' structures, and the same with
// long wall lines added (a foreign owner's, so the mover must go round their ends).
// Step cost: a quarter of the foot hop, plus fords. No units.
public class SubtilePathBench
{
    private readonly ITestOutputHelper _out;
    public SubtilePathBench(ITestOutputHelper output) => _out = output;

    private static bool Land(GameWorld w, TileCoord t) =>
        w.Grid.BiomeAt(t) != Biome.Water && w.Grid.TerrainCost(t) < Biomes.Impassable && !w.Structures.ContainsKey(t);

    // Wall lines of a foreign owner: `count` lines of up to `length` tiles, horizontal
    // and vertical alternately, each stopped by water or a structure.
    private static int AddWallLines(GameWorld w, int owner, int count, int length, Random rng)
    {
        var placed = 0;
        for (var i = 0; i < count; i++)
        {
            var start = new TileCoord(rng.Next(10, w.Grid.Width - 10), rng.Next(10, w.Grid.Height - 10));
            var horizontal = i % 2 == 0;
            for (var k = 0; k < length; k++)
            {
                var t = horizontal ? new TileCoord(start.X + k, start.Y) : new TileCoord(start.X, start.Y + k);
                if (!w.Grid.InBounds(t) || !Land(w, t)) break;
                w.AddStructure(new Wall(t) { OwnerId = owner });
                placed++;
            }
        }
        return placed;
    }

    // Worn roads: `count` straight runs of `length` links, alternately along rows and columns, at
    // full wear. Cheap steps lower the A* floor (Sim.Core SubtileStepRules.CheapestStep), which is
    // what this map measures.
    private static int AddRoads(GameWorld w, int count, int length, Random rng)
    {
        var placed = 0;
        var width = w.Grid.Width * Subtile.Size; var height = w.Grid.Height * Subtile.Size;
        for (var i = 0; i < count; i++)
        {
            var x = rng.Next(20, width - 20 - length); var y = rng.Next(20, height - 20 - length);
            var east = i % 2 == 0;
            for (var k = 0; k < length; k++)
            {
                var owner = new WorldSubtile(east ? x + k : x, east ? y : y + k);
                w.Roads[Sim.Core.Roads.SubtileLink.FromOwner(owner, east ? Sim.Core.Roads.SubtileLink.Axis.East : Sim.Core.Roads.SubtileLink.Axis.South)] =
                    new Sim.Core.Roads.RoadState(Sim.Core.Roads.RoadConstants.CONDITION_MAX, 0);
                placed++;
            }
        }
        return placed;
    }

    private static string Stats(List<double> v) { v.Sort(); return $"median {v[v.Count / 2]:F1} ms, p90 {v[(int)(v.Count * 0.9)]:F1}, max {v[^1]:F1}"; }

    private void Run(string map, GameWorld world, int mover)
    {
        var land = new List<TileCoord>();
        for (var y = 0; y < world.Grid.Height; y++)
            for (var x = 0; x < world.Grid.Width; x++)
                if (Land(world, new TileCoord(x, y))) land.Add(new TileCoord(x, y));
        var m = new StepMover(mover, Traversal.Foot, false);
        var rng = new Random(1);
        var sw = new Stopwatch();
        foreach (var (label, lo, hi) in new[] { ("medium 10-30 tiles", 10, 30), ("long 60+ tiles", 60, 999) })
        {
            var pairs = new List<(TileCoord, TileCoord)>();
            while (pairs.Count < 60)
            {
                var a = land[rng.Next(land.Count)]; var b = land[rng.Next(land.Count)];
                var d = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
                if (d >= lo && d <= hi) pairs.Add((a, b));
            }
            var whole = new List<double>(); var corr = new List<double>();
            long wholeNodes = 0, corrNodes = 0;
            var found = 0; var missed = 0; var fellBack = 0; var worse = new List<double>();
            foreach (var (a, b) in pairs)
            {
                var s = WorldSubtile.Of(a, new Subtile(1, 1));
                var e = WorldSubtile.Of(b, new Subtile(2, 2));
                var ws = new SubtilePathStats(); var cs = new SubtilePathStats();
                sw.Restart(); var full = SubtilePathfinder.Find(world, m, s, e, stats: ws, useCorridor: false); whole.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); var c = SubtilePathfinder.Find(world, m, s, e, stats: cs); corr.Add(sw.Elapsed.TotalMilliseconds);
                wholeNodes += ws.Expanded; corrNodes += cs.Expanded;
                if (cs.FellBack) fellBack++;
                if (full is null) continue;
                found++;
                if (c is null) { missed++; continue; }
                var fc = Cost(world, m, s, full); var cc = Cost(world, m, s, c);
                worse.Add((double)cc / fc - 1);
            }
            _out.WriteLine($"[{map}] {label}: {found}/{pairs.Count} reachable");
            _out.WriteLine($"  whole    : {Stats(whole)}; {wholeNodes / pairs.Count:N0} subtiles expanded on average");
            _out.WriteLine($"  corridor : {Stats(corr)}; {corrNodes / pairs.Count:N0} expanded on average; fell back to the whole grid {fellBack} times; found nothing {missed} times");
            if (worse.Count > 0)
                _out.WriteLine($"  corridor path costs more than the best by: median {worse.OrderBy(v => v).ElementAt(worse.Count / 2):P1}, max {worse.Max():P1}");
        }
    }

    private static int Cost(GameWorld w, StepMover m, WorldSubtile from, List<WorldSubtile> path)
    {
        var total = 0; var at = from;
        foreach (var s in path) { total += SubtileStepRules.StepCost(w, m.Traversal, at, s, 0); at = s; }
        return total;
    }

    [Fact]
    public void SubtilePathfinding_OnADefaultMap()
    {
        if (Environment.GetEnvironmentVariable("AOW_BENCH") != "1") return;
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 128, MapHeight = 128, MapSeed = 7, AiPlayers = 3 });
        var sim = new Simulation(build.Spec, seed: 1);
        var world = sim.World;
        _out.WriteLine($"map {world.Grid.Width}x{world.Grid.Height} tiles = {world.Grid.Width * 4}x{world.Grid.Height * 4} subtiles, {world.Structures.Count} structures");
        Run("default map", world, mover: 0);

        // The same map with long walls of a foreign owner across it.
        var walls = AddWallLines(world, owner: 1, count: 24, length: 30, new Random(5));
        _out.WriteLine($"added {walls} wall tiles in 24 lines");
        Run("with wall lines", world, mover: 0);

        // And with worn roads across it (cheap steps weaken the heuristic).
        var links = AddRoads(world, count: 120, length: 90, new Random(9));
        _out.WriteLine($"added {links} worn road links in 120 runs");
        Run("with wall lines and roads", world, mover: 0);
    }
}
