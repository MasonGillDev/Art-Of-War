namespace Sim.Core.Caches;

// M23 — genesis loot-cache scatter (docs/loot-caches.md). DETERMINISTIC:
// candidate tiles are enumerated in canonical (y, x) order, selected by a
// partial Fisher-Yates draw over the sim's seeded Rng, and loot is rolled from
// the same Rng. The FOG GATE — caches spawn only on tiles NO player has ever
// seen — reads the union of every player's genesis Explored set (the
// starting-vision discs); the entire rest of the map is fair game, which at
// genesis is the overwhelming majority.
//
// Runs once in the Simulation spec-ctor AFTER the genesis lifespan rolls, so
// when Count == 0 it is a pure no-op that consumes no Rng and leaves existing
// scenarios bit-identical. Only the resulting Cache structures persist (the
// snapshot serializes them); restore never re-scatters.
public static class CacheScatter
{
    // A cache's primary stack is one of these; gear is rolled separately.
    private static readonly Resource[] PrimaryResources =
        { Resource.Wood, Resource.Stone, Resource.Ore, Resource.Food };
    private static readonly Resource[] GearResources =
        { Resource.Sword, Resource.Bow, Resource.Shield };

    public static void Scatter(GameWorld world, Rng rng, CacheConfig config)
    {
        if (config.Count <= 0) return;

        // The fog gate: every tile any player has revealed at genesis.
        var seen = new HashSet<TileCoord>();
        foreach (var set in world.Explored.Values)
            seen.UnionWith(set);

        // Eligible candidates in canonical (y, x) order: in-bounds passable
        // land (not Water — a foot unit can't reach mid-lake; Mountain IS
        // allowed, thematic peak-treasure), structure-free, and unseen by all.
        var grid = world.Grid;
        var candidates = new List<TileCoord>();
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var t = new TileCoord(x, y);
                if (seen.Contains(t)) continue;
                if (world.Structures.ContainsKey(t)) continue;
                var biome = grid.BiomeAt(t);
                if (biome == Biome.Water || biome == Biome.None) continue;
                candidates.Add(t);
            }

        // Partial Fisher-Yates over the seeded Rng: pick min(Count, available)
        // distinct tiles, then roll each cache's loot.
        var n = Math.Min(config.Count, candidates.Count);
        for (var i = 0; i < n; i++)
        {
            var j = i + rng.NextInt(candidates.Count - i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            var cache = new Cache(candidates[i]) { OwnerId = CacheConstants.OwnerId };
            RollLootInto(cache, rng, config);
            world.AddStructure(cache);
        }
    }

    // Order of Rng draws is fixed: primary resource, primary amount, gear
    // chance, (maybe) gear pick. Deposited straight into the cache.
    private static void RollLootInto(Cache cache, Rng rng, CacheConfig config)
    {
        var primary = PrimaryResources[rng.NextInt(PrimaryResources.Length)];
        var span = config.MaxResourceAmount - config.MinResourceAmount + 1;
        var amount = config.MinResourceAmount + (span > 0 ? rng.NextInt(span) : 0);
        cache.Deposit(primary, amount);

        if (rng.NextInt(100) < config.GearChancePercent)
        {
            var gear = GearResources[rng.NextInt(GearResources.Length)];
            cache.Deposit(gear, 1);
        }
    }
}
