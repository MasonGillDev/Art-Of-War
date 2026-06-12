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
        var spec = BuildSpec(map, opts.AiPlayers);
        return new WorldBuild(spec, map, elevation, cfg);
    }

    private static GenesisSpec BuildSpec(GeneratedMap map, int aiPlayers)
    {
        var start = map.Start;
        var nextId = 1;

        // M17 — every faction (human and AI) gets the IDENTICAL start:
        // fairness includes the opening. Loadout rationale: a lumber camp
        // costs wood, so running dry before building one strands you; food
        // must cover the M13 drain until a farm is up AND delivering back
        // to the castle — 14 citizens eat 56/game-day, the bootstrap is
        // ~2-3 game-days at march pace, 200 ≈ 3.6 game-days of runway.
        FactionStartSpec MakeFaction(int ownerId, TileCoord castleAt)
        {
            TileCoord Clamp(int x, int y) => new(
                Math.Clamp(x, 0, map.Width - 1),
                Math.Clamp(y, 0, map.Height - 1));

            // Two of each role — units to drive every initial task (build,
            // haul, work each extractor, scout), gridded around the castle
            // so they don't all stack on one tile.
            var roster = new[]
            {
                UnitRole.Builder, UnitRole.Hauler, UnitRole.Lumberjack,
                UnitRole.Quarryman, UnitRole.Miner, UnitRole.Farmer, UnitRole.Scout,
            };
            var spawns = new List<UnitSpawn>();
            var slot = 0;
            foreach (var role in roster)
                for (var copy = 0; copy < 2; copy++)
                {
                    var dx = (slot % 5) - 2;   // -2..2 across
                    var dy = (slot / 5) - 1;   // a few rows around the castle
                    // STAGGERED ages 18..40 (deterministic by slot). A
                    // uniform-age roster hits a synchronized fertility
                    // cliff — every founder ages past MaxFertileAge the
                    // same game-day and births stop dead until the native
                    // generation matures (the M17 balance lab caught the
                    // population sawtooth). A spread roster breeds in
                    // overlapping waves.
                    var age = 18 + slot * 22 / 13;
                    spawns.Add(new UnitSpawn(nextId++, Clamp(castleAt.X + dx, castleAt.Y + dy), role,
                        OwnerId: ownerId, StartingAgeYears: age));
                    slot++;
                }
            return new FactionStartSpec
            {
                OwnerId = ownerId,
                CastlePosition = castleAt,
                CastleHoldings = new SortedDictionary<Resource, int>
                {
                    [Resource.Wood] = 70,
                    [Resource.Stone] = 50,
                    [Resource.Food] = 200,
                },
                UnitSpawns = spawns.ToArray(),
            };
        }

        var factions = new List<FactionStartSpec> { MakeFaction(0, start) };

        // M17 — N full AI factions (the token "neutral scout" faction is
        // retired; AI players are the "other" now). Castles placed on
        // grassland a real march away from the player and from each other;
        // perfect fair-start placement stays deferred to M11 Phase 2.
        var castles = new List<TileCoord> { start };
        for (var i = 0; i < aiPlayers; i++)
        {
            if (FindAiStart(map, castles) is not { } aiCastle)
            {
                Console.WriteLine($"WARN: no viable start for AI faction {i + 1} — skipping.");
                continue;
            }
            castles.Add(aiCastle);
            factions.Add(MakeFaction(i + 1, aiCastle));
        }

        var spec = new GenesisSpec
        {
            Width = map.Width,
            Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            FactionStarts = factions,
        };

        Console.WriteLine($"Generated {map.Width}x{map.Height} continent (seed {map.Seed}); " +
            $"castle start at ({start.X},{start.Y}); factions: {factions.Count}.");
        return spec;
    }

    // M17 — pick a grassland castle tile for an AI faction: as far from the
    // player as the map allows (preferring ~half-map separation, walking
    // inward if the continent is small), and at least MinSeparation from
    // every already-placed castle. Deterministic: ring-perimeter scan in
    // (dist, y, x) order, same shape as FindGrasslandNear.
    private const int MinSeparation = 24;

    private static TileCoord? FindAiStart(GeneratedMap map, List<TileCoord> castles)
    {
        var origin = castles[0];   // the player start anchors the search
        var preferred = Math.Min(64, Math.Max(map.Width, map.Height) / 2);
        for (var r = preferred; r >= MinSeparation; r -= 4)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue; // perimeter only
            int x = origin.X + dx, y = origin.Y + dy;
            if (x < 0 || x >= map.Width || y < 0 || y >= map.Height) continue;
            if (map.Grid[x, y] != Biome.Grassland) continue;
            var clear = true;
            foreach (var c in castles)
                if (Math.Max(Math.Abs(c.X - x), Math.Abs(c.Y - y)) < MinSeparation) { clear = false; break; }
            if (!clear) continue;
            // A castle TILE isn't a START — the faction needs farmable
            // land in walking range. Under the 18-mouths-per-farm economy
            // (2026-06-11) a start whose nearest grassland sits 10 tiles
            // out dies to haul distance (the balance lab watched faction 1
            // starve on exactly such a spot). Demand a real meadow:
            // enough grassland within ring 6 for several farms + claims.
            if (GrasslandWithin(map, x, y, radius: 6) < 40) continue;
            return new TileCoord(x, y);
        }
        return null;
    }

    private static int GrasslandWithin(GeneratedMap map, int cx, int cy, int radius)
    {
        var count = 0;
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(map.Height - 1, cy + radius); y++)
        for (var x = Math.Max(0, cx - radius); x <= Math.Min(map.Width - 1, cx + radius); x++)
            if (map.Grid[x, y] == Biome.Grassland) count++;
        return count;
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
