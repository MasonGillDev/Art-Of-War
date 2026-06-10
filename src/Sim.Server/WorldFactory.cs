using Sim.Core.World;
using Sim.Core.WorldGen;

namespace Sim.Server;

// The genesis SPEC (not a pre-built world) plus the artifacts the view projector needs
// to rebuild the client's heightmap: the continuous elevation field (quantized to ints)
// and the generation config (for the waterline). We hand the spec to the spec-aware
// Simulation ctor so genesis units get their lifespans rolled (death-by-age) — the plain
// (GameWorld, seed) ctor is for Snapshot.Restore/tests and would leave them immortal.
public sealed record WorldBuild(GenesisSpec Spec, GeneratedMap Map, int[,] Elevation, GenerationConfig Config);

// Builds the initial world from options: a REAL procedurally generated continent
// (Perlin + Whittaker, frozen integer biomes), the player's start loadout, and a
// neutral second faction so fog has something to reveal. Touches only Sim.Core
// public APIs.
public static class WorldFactory
{
    public static WorldBuild Build(ServerOptions opts)
    {
        var cfg = new GenerationConfig
        {
            Seed = opts.MapSeed,
            Width = opts.MapWidth,    // continent size (--width / --height)
            Height = opts.MapHeight,
            Frequency = 0.02,         // feature SCALE is fixed → a bigger map = a genuinely larger continent
            WaterMax = 0.30,          // proportion of water
            // Scale the start search with the map so a valid start still exists on big maps.
            StartSearchRadius = Math.Max(28, Math.Max(opts.MapWidth, opts.MapHeight) / 4),
        };
        var map = MapGenerator.Build(cfg);
        // Regenerate the CONTINUOUS elevation field the classifier used (NoiseField is
        // public — no core changes) so the client can build a smooth heightmap whose
        // slopes line up with the biome bands.
        var elevation = QuantizeElevation(NoiseField.Generate(cfg.Seed + cfg.ElevationSeedOffset, cfg));
        var spec = BuildSpec(map);
        return new WorldBuild(spec, map, elevation, cfg);
    }

    private static GenesisSpec BuildSpec(GeneratedMap map)
    {
        var start = map.Start;

        TileCoord Clamp(int x, int y) => new(
            Math.Clamp(x, 0, map.Width - 1),
            Math.Clamp(y, 0, map.Height - 1));

        // Two of each role, so the player has the units to drive every initial task
        // (build, haul, work each extractor, scout). Laid out in a grid around the
        // castle so they don't all stack on one tile.
        var roster = new[]
        {
            UnitRole.Builder, UnitRole.Hauler, UnitRole.Lumberjack,
            UnitRole.Quarryman, UnitRole.Miner, UnitRole.Farmer, UnitRole.Scout,
        };
        var unitSpawns = new List<UnitSpawn>();
        var nextId = 1;
        var slot = 0;
        foreach (var role in roster)
            for (var copy = 0; copy < 2; copy++)
            {
                var dx = (slot % 5) - 2;   // -2..2 across
                var dy = (slot / 5) - 1;   // a few rows around the castle
                unitSpawns.Add(new UnitSpawn(nextId++, Clamp(start.X + dx, start.Y + dy), role));
                slot++;
            }

        // M6: each faction has its own start (castle + holdings + units).
        var factions = new List<FactionStartSpec>
        {
            new()
            {
                OwnerId = 0,
                CastlePosition = start,
                // Generous starting stock so the player can bootstrap the economy without
                // soft-locking: a lumber camp costs wood, so running dry before building one
                // strands you. Food covers the M13 consumption drain until a farm is up.
                CastleHoldings = new SortedDictionary<Resource, int>
                {
                    [Resource.Wood] = 70,
                    [Resource.Stone] = 50,
                    [Resource.Food] = 40,
                },
                UnitSpawns = unitSpawns.ToArray(),
            },
        };

        // Optional neutral second faction ~12 tiles out, so fog/combat have an "other".
        if (FindGrasslandNear(map, start, 12) is { } enemyTile)
        {
            factions.Add(new FactionStartSpec
            {
                OwnerId = 1,
                CastlePosition = enemyTile,
                CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Wood] = 20 },
                UnitSpawns = new[] { new UnitSpawn(99, enemyTile, UnitRole.Scout, OwnerId: 1) },
            });
        }

        var spec = new GenesisSpec
        {
            Width = map.Width,
            Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            FactionStarts = factions,
        };

        Console.WriteLine($"Generated {map.Width}x{map.Height} continent (seed {map.Seed}); castle start at ({start.X},{start.Y}).");
        return spec;
    }

    // Nearest Grassland tile on the Chebyshev ring at `dist` (then a few rings out)
    // from origin — a plausible spot for the neutral scout. Null if none found.
    private static TileCoord? FindGrasslandNear(GeneratedMap map, TileCoord origin, int dist)
    {
        for (var r = dist; r <= dist + 10; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // ring perimeter only
            int x = origin.X + dx, y = origin.Y + dy;
            if (x < 0 || x >= map.Width || y < 0 || y >= map.Height) continue;
            if (map.Grid[x, y] == Biome.Grassland) return new TileCoord(x, y);
        }
        return null;
    }

    // Quantize the continuous [0,1] elevation field to integers in [0,1000] — keeps the
    // wire integer-friendly (no float/locale issues) and is plenty for a heightmap. The
    // client divides back by 1000.
    private static int[,] QuantizeElevation(double[,] field)
    {
        int w = field.GetLength(0), h = field.GetLength(1);
        var q = new int[w, h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            q[x, y] = (int)Math.Round(Math.Clamp(field[x, y], 0.0, 1.0) * 1000.0);
        return q;
    }
}
