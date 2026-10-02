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
            // Rivers scale with the coastline-to-interior distance: a bigger
            // continent drains through more of them (docs/rivers.md).
            RiverCount = Math.Max(8, (opts.MapWidth + opts.MapHeight) / 20),
            RiverMaxLength = opts.MapWidth + opts.MapHeight,
        };
        var map = MapGenerator.Build(cfg);
        // Regenerate the CONTINUOUS elevation field the classifier used — the SHAPED
        // field (ocean-border mask included), not the raw noise — so the client builds
        // a heightmap whose waterline and slopes line up with the biome grid.
        var elevation = QuantizeElevation(ContinentShaper.BuildElevation(cfg));
        var spec = BuildSpec(map, opts.AiPlayers, opts.CacheCount, FertilityFor(opts.FertilityGradient), opts.GodMode, opts.Progression, opts.IdolCount, opts.LandingDay);
        return new WorldBuild(spec, map, elevation, cfg);
    }

    // M35 — the played fertility gradient by rung name (docs/environmental-
    // fertility.md; sweep in docs/m35-status.md). "flat" is the config's own
    // identity default; unknown names fail loudly rather than silently flat.
    public static Sim.Core.Biomes.BiomeDegradationConfig FertilityFor(string gradient) => gradient switch
    {
        "flat"   => new Sim.Core.Biomes.BiomeDegradationConfig(),
        "mild"   => new Sim.Core.Biomes.BiomeDegradationConfig() with { WaterFertilityBonus = 1000, DryEdgePenalty = 250, ForestDepthBonusPerRing = 300 },
        "strong" => new Sim.Core.Biomes.BiomeDegradationConfig() with { WaterFertilityBonus = 2500, DryEdgePenalty = 500, ForestDepthBonusPerRing = 600 },
        _ => throw new ArgumentException($"unknown fertility gradient '{gradient}' (flat | mild | strong)", nameof(gradient)),
    };

    // THE HUMAN OPENING (docs/human-opening.md, user 2026-10-01): the roster is
    // the tutorial. Who you start with says what to do first — cut wood, farm,
    // haul — and the House beside the castle says breed. Eleven citizens are
    // untrained hands: the School is something the player finds they need as
    // the kingdom asks for more, not something handed over at tick 0. Two
    // builders, so losing one is not the circular lock (SchoolPosition's doc).
    // Slot 0 is the King, a body of its own. No scouts: those come from the
    // Lodge (M38, docs/scouting-secrets.md).
    internal static readonly UnitRole[] HumanRoster =
    {
        UnitRole.King,
        UnitRole.Builder, UnitRole.Builder,
        UnitRole.Hauler, UnitRole.Hauler,
        UnitRole.Farmer, UnitRole.Farmer,
        UnitRole.Lumberjack,
        UnitRole.None, UnitRole.None, UnitRole.None, UnitRole.None, UnitRole.None, UnitRole.None,
        UnitRole.None, UnitRole.None, UnitRole.None, UnitRole.None, UnitRole.None,
    };

    private static GenesisSpec BuildSpec(GeneratedMap map, int aiPlayers, int cacheCount, Sim.Core.Biomes.BiomeDegradationConfig fertility, bool godMode, bool progression, int idolCount, int landingDay)
    {
        var start = map.Start;
        var nextId = 1;

        // M17 — every AI faction gets the IDENTICAL start: fairness includes
        // the opening. The human seat's roster differs (HumanRoster), but its
        // holdings are the same — 19 mouths make 200 food ≈ 2.6 game-days, a
        // playtest question (docs/human-opening.md). Loadout rationale: a lumber camp
        // costs wood, so running dry before building one strands you; food
        // must cover the M13 drain until a farm is up AND delivering back
        // to the castle — 14 citizens eat 56/game-day, the bootstrap is
        // ~2-3 game-days at march pace, 200 ≈ 3.6 game-days of runway.
        FactionStartSpec MakeFaction(int ownerId, TileCoord castleAt, bool humanSeat = false)
        {
            // A tile a unit can actually stand on: in-bounds and not water/void.
            // Water is exactly what stranded starting units in the sea when a
            // castle sat on the shoreline.
            bool Walkable(int x, int y) =>
                x >= 0 && x < map.Width && y >= 0 && y < map.Height &&
                map.Grid[x, y] is not (Biome.Water or Biome.None);

            // The genesis School (the circular-lock fix — see
            // FactionStartSpec.SchoolPosition), or for a human seat the genesis
            // House (docs/human-opening.md): the first walkable tile ringing the
            // castle. Computed FIRST so unit placement can avoid its tile.
            // Deterministic (dist, y, x) scan.
            TileCoord? FindStartStructureTile()
            {
                for (var r = 1; r <= 3; r++)
                for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    if (Walkable(castleAt.X + dx, castleAt.Y + dy))
                        return new TileCoord(castleAt.X + dx, castleAt.Y + dy);
                }
                return null;
            }
            var startStructureTile = FindStartStructureTile();

            // Walkable land tiles ringing the castle, NEAREST-FIRST, skipping
            // water, the castle tile, and the school/house tile — so starting units
            // never spawn in the sea or buried inside a structure. (The old code
            // gridded a fixed 5×3 block and merely clamped to bounds, which
            // spilled into the water on any coastal start.) Deterministic
            // (dist, y, x) ring scan — the same shape as every search here.
            List<TileCoord> GatherSpawnTiles(int need)
            {
                var tiles = new List<TileCoord>(need);
                var maxR = Math.Max(map.Width, map.Height);
                for (var r = 1; r <= maxR && tiles.Count < need; r++)
                for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = castleAt.X + dx, y = castleAt.Y + dy;
                    if (!Walkable(x, y)) continue;
                    if (x == castleAt.X && y == castleAt.Y) continue;
                    if (startStructureTile is { } s && x == s.X && y == s.Y) continue;
                    tiles.Add(new TileCoord(x, y));
                    if (tiles.Count >= need) return tiles;
                }
                return tiles;
            }

            // AI seats: two of each role — units to drive every initial task
            // (build, haul, work each extractor, scout) — beside a School. Slot 0
            // is crowned (the kingSlot note below), costing one Builder.
            //
            // A HUMAN seat gets HumanRoster beside a House instead
            // (docs/human-opening.md); see that field. The split keeps AI brains
            // and the balance labs' baselines exactly where they were.
            var roster = humanSeat
                ? HumanRoster
                : new[]
                {
                    UnitRole.Builder, UnitRole.Builder, UnitRole.Hauler, UnitRole.Hauler,
                    UnitRole.Lumberjack, UnitRole.Lumberjack, UnitRole.Quarryman, UnitRole.Quarryman,
                    UnitRole.Miner, UnitRole.Miner, UnitRole.Farmer, UnitRole.Farmer,
                    UnitRole.Scout, UnitRole.Scout,
                };
            var need = roster.Length;

            // M31 — WHICH founder wears the crown.
            //
            // The FIRST BUILDER, which is where the crown already sat when royalty
            // was a flag. As a role the king stops doing his trade, so crowning
            // anyone costs the realm something; the question is only what.
            //
            // Two wrong answers came first, and both are worth recording. A
            // FIFTEENTH BODY kept all fourteen workers and broke three balance
            // baselines on food alone — an extra mouth is an extra mouth, exactly as
            // this comment used to warn. Crowning a SCOUT looked cheapest (nothing
            // downstream depends on exploration) and was the worst of the three: it
            // starved faction 1 outright, because TrainRung's pool already excludes
            // Builders and Haulers, so a crowned scout is a TRAINABLE body removed
            // while a crowned builder is not.
            //
            // The cost of crowning a builder is therefore a temporary one the AI
            // already knows how to repair: its builder-floor rung exists precisely
            // to retrain a lost Builder, and the player can do the same.
            //
            // A human seat's king is a body of its own (HumanRoster[0] is the King),
            // so no trade is lost — the human opening has no School to retrain one.
            var kingSlot = 0;
            var spawnTiles = GatherSpawnTiles(need);

            var spawns = new List<UnitSpawn>(need);
            for (var slot = 0; slot < need; slot++)
            {
                var role = slot == kingSlot ? UnitRole.King : roster[slot];
                // STAGGERED ages 18..40 (deterministic by slot). A uniform-age
                // roster hits a synchronized fertility cliff — every founder ages
                // past MaxFertileAge the same game-day and births stop dead until
                // the native generation matures (the M17 balance lab caught the
                // population sawtooth). A spread roster breeds in overlapping waves.
                // (need - 1 is 13 for the AI's fourteen: their ages are unchanged.)
                var age = 18 + slot * 22 / (need - 1);
                // Nearest free land tile; fall back to the castle tile only on a
                // degenerate tiny-island start with too little land (never water).
                var pos = slot < spawnTiles.Count ? spawnTiles[slot] : castleAt;
                spawns.Add(new UnitSpawn(nextId++, pos, role, OwnerId: ownerId, StartingAgeYears: age));
            }

            return new FactionStartSpec
            {
                OwnerId = ownerId,
                CastlePosition = castleAt,
                // M31 — every realm starts with a king, the player's and each
                // rival's alike (docs/king-and-dynasty.md). Symmetry is the
                // point: the King's Buff is real combat power, so a world
                // where only some factions are crowned is quietly unfair.
                //
                // Whose body: see the kingSlot note above. For an AI seat,
                // crowning slot 0 costs the faction one of its two Builders,
                // because a King cannot build — AssignBuildersIntent takes
                // Builders only. That cost is the cheapest of the three that were
                // tried, and it is one the AI's builder-floor rung already knows
                // how to repair.
                KingUnitId = spawns[kingSlot].Id,
                CastleHoldings = new SortedDictionary<Resource, int>
                {
                    [Resource.Wood] = 70,
                    [Resource.Stone] = 50,
                    [Resource.Food] = 200,
                },
                UnitSpawns = spawns.ToArray(),
                SchoolPosition = humanSeat ? null : startStructureTile,
                HousePosition = humanSeat ? startStructureTile : null,
            };
        }

        // God mode is the human seat's alone (docs/god-mode.md), and so is
        // progression (docs/progression.md): AI factions never enrol.
        var factions = new List<FactionStartSpec>
        {
            // M38 — `progression` is the human-seat switch (on for the host, off for
            // the AI labs that drive player 0): the same seat that enrols in
            // progression starts without scouts.
            MakeFaction(0, start, humanSeat: progression) with { GodMode = godMode, Progression = progression },
        };

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
            Rivers = MapGenerator.ToRiverOverrides(map),
            FactionStarts = factions,
            // M23 — scatter loot caches into the fog (never on a tile any
            // faction's starting vision has revealed). docs/loot-caches.md.
            Caches = new Sim.Core.Caches.CacheConfig(Count: cacheCount),
            Idols = new Sim.Core.Scouting.IdolConfig() with { Count = idolCount },
            // M44 — ore veins: about a third of mountain tiles, laid from the
            // map seed (docs/stone-and-ore-land.md).
            Veins = new Sim.Core.Mining.VeinConfig() with { Seed = unchecked((ulong)map.Seed) },
            BiomeDegradation = fertility,
            // Two-act pacing — day X, the end of the prelude (docs/two-act-pacing.md).
            // 0 = a one-act world, which is what every lab's bare ServerOptions gets.
            Landing = Sim.Core.Landing.LandingConfig.OnDay(landingDay),
        };

        Console.WriteLine($"Generated {map.Width}x{map.Height} continent (seed {map.Seed}); " +
            $"castle start at ({start.X},{start.Y}); factions: {factions.Count}; caches: {cacheCount}; " +
            $"river tiles: {spec.Rivers.Count}.");
        LogVeinReach(map, castles);
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

    // M44 — tuning aid: each start's distance to the nearest mountain, so an
    // ore-poor spawn is obvious in the host log (docs/stone-and-ore-land.md;
    // mountains are rare, and start placement does not chase them — the
    // user chose plain vein density over placement rules, 2026-10-01).
    private static void LogVeinReach(GeneratedMap map, List<TileCoord> castles)
    {
        var parts = new List<string>();
        for (var i = 0; i < castles.Count; i++)
            parts.Add($"{i}:{NearestMountain(map, castles[i])}");
        Console.WriteLine($"Nearest mountain per start (tiles): {string.Join(" ", parts)}");
    }

    private static string NearestMountain(GeneratedMap map, TileCoord c)
    {
        var maxR = Math.Max(map.Width, map.Height);
        for (var r = 0; r <= maxR; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = c.X + dx, y = c.Y + dy;
            if (x >= 0 && y >= 0 && x < map.Width && y < map.Height && map.Grid[x, y] == Biome.Mountain)
                return r.ToString();
        }
        return "none";
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
