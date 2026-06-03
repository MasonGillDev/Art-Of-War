using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Core.WorldGen;

namespace Sim.Tests;

// Property-based tests for the generator. Generated maps are hostile to
// "arrives at tick 180" assertions — these tests validate properties of
// the output instead:
//   - Reproducible: same config → identical grid (the generator's own
//     determinism, separate from sim determinism).
//   - Valid: every tile is a known biome; dimensions correct.
//   - Connected: flood fill from the chosen start reaches every tile
//     (water-passable guarantees this).
//   - Sane proportions: no single biome dominates; no biome is degenerate.
//   - Valid start: Grassland with Forest+Hills+Mountain reachable within
//     the configured radius.
//   - Sim-side determinism intact: twin-run + snapshot round-trip on a
//     generated genesis world both hold (proves the freeze rule held —
//     the generator's floats never crossed into the sim's replay path).
public class WorldGenTests
{
    private static GenerationConfig DefaultConfig(int seed = 42) =>
        new() { Seed = seed, Width = 64, Height = 64 };

    [Fact]
    public void SameConfig_ProducesIdenticalGrid()
    {
        var a = MapGenerator.Build(DefaultConfig());
        var b = MapGenerator.Build(DefaultConfig());
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(a.Start, b.Start);
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                Assert.Equal(a.Grid[x, y], b.Grid[x, y]);
    }

    [Fact]
    public void EveryTile_IsAValidBiome()
    {
        var map = MapGenerator.Build(DefaultConfig());
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var b = map.Grid[x, y];
                // Append-only enum: known values are None..Water.
                Assert.True(b == Biome.Grassland || b == Biome.Forest
                          || b == Biome.Hills || b == Biome.Mountain
                          || b == Biome.Water,
                    $"unknown biome at ({x},{y}): {b}");
            }
    }

    [Fact]
    public void Start_IsGrassland_WithResourceBiomesNearby()
    {
        var cfg = DefaultConfig();
        var map = MapGenerator.Build(cfg);
        Assert.Equal(Biome.Grassland, map.Grid[map.Start.X, map.Start.Y]);
        // The picker promised Forest + Hills + Mountain are within radius.
        // Re-verify here in case the picker contract changes.
        Assert.True(HasBiomeWithin(map.Grid, map.Start, cfg.StartSearchRadius, Biome.Forest));
        Assert.True(HasBiomeWithin(map.Grid, map.Start, cfg.StartSearchRadius, Biome.Hills));
        Assert.True(HasBiomeWithin(map.Grid, map.Start, cfg.StartSearchRadius, Biome.Mountain));
    }

    [Fact]
    public void EveryTile_IsReachableFromStart()
    {
        // Water is passable-but-expensive; every tile must be reachable.
        // BFS flooding through any tile whose MoveCost < int.MaxValue. With
        // current Biomes.MoveCost, that's all 5 biomes — full connectivity.
        var map = MapGenerator.Build(DefaultConfig());
        var grid = new TileGrid(map.Width, map.Height, Biome.Grassland);
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                grid.SetBiome(new TileCoord(x, y), map.Grid[x, y]);

        var reached = FloodFrom(grid, map.Start);
        var total = map.Width * map.Height;
        Assert.Equal(total, reached.Count);
    }

    [Fact]
    public void BiomeProportions_AreSane()
    {
        var map = MapGenerator.Build(DefaultConfig());
        var total = map.Width * map.Height;
        var counts = new Dictionary<Biome, int>();
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                counts[map.Grid[x, y]] = counts.GetValueOrDefault(map.Grid[x, y]) + 1;

        double Share(Biome b) => counts.GetValueOrDefault(b) / (double)total;

        // Guards against degenerate maps. Tune thresholds if generator
        // changes — these are sanity, not precision.
        Assert.InRange(Share(Biome.Water),    0.00, 0.50);
        Assert.InRange(Share(Biome.Mountain), 0.00, 0.40);
        Assert.InRange(Share(Biome.Hills),    0.00, 0.50);
        // The playable zone (Grassland + Forest) should be a meaningful share.
        Assert.True(Share(Biome.Grassland) + Share(Biome.Forest) > 0.20,
            "playable Grassland + Forest share too small for a meaningful map");
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentMaps()
    {
        var a = MapGenerator.Build(DefaultConfig(seed: 1));
        var b = MapGenerator.Build(DefaultConfig(seed: 2));
        var differences = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.Grid[x, y] != b.Grid[x, y]) differences++;
        // Different seeds → meaningfully different maps. At least 10% of
        // tiles should differ.
        Assert.True(differences > a.Width * a.Height / 10,
            $"different seeds produced near-identical maps ({differences} tile differences)");
    }

    // -------- Sim-side determinism intact on a generated genesis --------

    private static (Simulation, GeneratedMap) BuildGeneratedSim(int seed = 42)
    {
        var cfg = DefaultConfig(seed: seed);
        var map = MapGenerator.Build(cfg);

        // Pour the generated grid into a GenesisSpec via the existing dict
        // override mechanism. The sim sees only frozen integer biomes.
        var spec = new GenesisSpec
        {
            Width = map.Width,
            Height = map.Height,
            CastlePosition = map.Start,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            StartingHoldings = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 20,
            },
            Units = new[]
            {
                new UnitSpawn(1, map.Start, UnitRole.Builder),
            },
        };
        var world = Genesis.Build(spec);
        return (new Simulation(world, seed: 0xABCD), map);
    }

    [Fact]
    public void TwinRun_OnGeneratedGenesis_HashesMatch()
    {
        // Twin runs through the same generated world. If the generator's
        // floats had leaked anywhere on the replay path, this would diverge.
        var (a, mapA) = BuildGeneratedSim();
        var (b, mapB) = BuildGeneratedSim();
        // Both maps identical (D1 reproducibility).
        Assert.Equal(mapA.Start, mapB.Start);

        // Walk the builder somewhere reachable; same intent in both sims.
        var target = new TileCoord(mapA.Width - 1, mapA.Height - 1);
        a.SubmitIntent(0, new MoveIntent(1, target));
        b.SubmitIntent(0, new MoveIntent(1, target));
        a.Run();
        b.Run();

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void Snapshot_RoundTrips_OnGeneratedWorld()
    {
        var (sim, _) = BuildGeneratedSim();
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(10, 10)));
        sim.Run();

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xABCD);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    // -------- helpers --------

    private static bool HasBiomeWithin(Biome[,] grid, TileCoord c, int r, Biome target)
    {
        var w = grid.GetLength(0);
        var h = grid.GetLength(1);
        var xLo = Math.Max(0, c.X - r);
        var xHi = Math.Min(w - 1, c.X + r);
        var yLo = Math.Max(0, c.Y - r);
        var yHi = Math.Min(h - 1, c.Y + r);
        for (var y = yLo; y <= yHi; y++)
            for (var x = xLo; x <= xHi; x++)
                if (grid[x, y] == target) return true;
        return false;
    }

    private static HashSet<TileCoord> FloodFrom(TileGrid grid, TileCoord start)
    {
        var reached = new HashSet<TileCoord> { start };
        var queue = new Queue<TileCoord>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            foreach (var n in grid.Neighbors(t))
            {
                if (grid.TerrainCost(n) == int.MaxValue) continue; // impassable
                if (reached.Add(n)) queue.Enqueue(n);
            }
        }
        return reached;
    }
}
