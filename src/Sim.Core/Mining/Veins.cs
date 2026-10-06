using Sim.Core.World;

namespace Sim.Core.Mining;

// M44 — where the ore is (docs/stone-and-ore-land.md).
//
// Veins are TERRAIN: seeded once at genesis onto Mountain tiles, frozen,
// snapshotted, never re-derived (a restore must not depend on this code
// staying the same — the world-generation freeze rule). They never deplete.
//
// Knowledge of veins is PER FACTION and only grows. Its writers are all
// event-driven (the inverted pure-read wall, architecture §2.3):
//   * SurveyRules.Complete — a Miner's sweep finds one (and proves the
//     slopes nearer than it barren);
//   * OnSight (from Sight.AfterReveal) and OnMineRaised (from
//     PlaceSiteIntent) — a faction that SEES a Mine or Mine site learns the
//     vein under it, for good ("once mined, anyone who can see it knows").
// Everything else here is a pure read.
public static class Veins
{
    // Genesis only: lay the veins for the world's VeinConfig. The one global
    // pass over the grid, run once before tick 0 (not a timer — architecture
    // §4 rule 3 is about periodic sweeps).
    internal static void Seed(GameWorld world)
    {
        var cfg = world.VeinConfig;
        if (cfg.OneIn <= 0)
            throw new InvalidOperationException($"VeinConfig.OneIn must be positive (got {cfg.OneIn}).");
        var grid = world.Grid;

        // 1. Density: about one Mountain tile in OneIn.
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
        {
            if (grid.BiomeAt(new TileCoord(x, y)) != Biome.Mountain) continue;
            if (Hash(cfg.Seed, x, y, DensitySalt) % (ulong)cfg.OneIn == 0)
                world.Veins.Add(new TileCoord(x, y));
        }

        // 2. Safety net: every connected range (8-neighbour) holds at least
        // one vein. A range the hash left empty gets one on its lowest-hash
        // tile (ties by (y, x)), so even a lone peak has ore and the
        // topped-up tile follows no learnable pattern.
        var visited = new HashSet<TileCoord>();
        var stack = new Stack<TileCoord>();
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
        {
            var origin = new TileCoord(x, y);
            if (grid.BiomeAt(origin) != Biome.Mountain || !visited.Add(origin)) continue;
            var hasVein = false;
            var best = origin;
            var bestHash = ulong.MaxValue;
            stack.Push(origin);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (world.Veins.Contains(t)) hasVein = true;
                var h = Hash(cfg.Seed, t.X, t.Y, NetSalt);
                if (h < bestHash || (h == bestHash && TileOrder.Instance.Compare(t, best) < 0))
                {
                    bestHash = h;
                    best = t;
                }
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var n = new TileCoord(t.X + dx, t.Y + dy);
                    if (!grid.InBounds(n) || grid.BiomeAt(n) != Biome.Mountain) continue;
                    if (visited.Add(n)) stack.Push(n);
                }
            }
            if (!hasVein) world.Veins.Add(best);
        }
    }

    // M51 — which ore each vein holds (docs/m51-ore-tiers-spec.md). Genesis
    // only, after the wilderness field is measured: rank the veins by
    // remoteness (a vein no foot reaches is the most remote; ties by (y, x)),
    // the farthest SteelSharePercent hold steel ore, the next IronSharePercent
    // iron ore, the rest copper ore. Ranking the VEINS (not the land bands)
    // gives every tier on every map that has mountains at all. No Rng.
    internal static void AssignOres(GameWorld world)
    {
        var cfg = world.VeinConfig;
        if (cfg.SteelSharePercent < 0 || cfg.IronSharePercent < 0 || cfg.SteelSharePercent + cfg.IronSharePercent > 100)
            throw new InvalidOperationException(
                $"VeinConfig ore shares must be non-negative and sum to at most 100 (steel {cfg.SteelSharePercent}, iron {cfg.IronSharePercent}).");
        var field = world.Wilderness;
        var ranked = world.Veins
            .Select((t, order) => (Tile: t, Minutes: field.MinutesAt(t) ?? int.MaxValue, Order: order))
            .OrderBy(v => v.Minutes).ThenBy(v => v.Order)
            .Select(v => v.Tile)
            .ToList();
        var n = ranked.Count;
        var steelFrom = n - (int)((long)n * cfg.SteelSharePercent / 100);
        var ironFrom = steelFrom - (int)((long)n * cfg.IronSharePercent / 100);
        for (var i = 0; i < n; i++)
            world.VeinOre[ranked[i]] = i >= steelFrom ? Resource.SteelOre
                : i >= ironFrom ? Resource.IronOre
                : Resource.CopperOre;
    }

    // ---- pure reads --------------------------------------------------------

    // M51 — the ore a vein holds; None off a vein.
    public static Resource OreAt(GameWorld world, TileCoord tile) =>
        world.VeinOre.TryGetValue(tile, out var ore) ? ore : Resource.None;

    public static bool IsVein(GameWorld world, TileCoord tile) => world.Veins.Contains(tile);

    public static bool Knows(GameWorld world, int playerId, TileCoord tile) =>
        world.KnownVeins.TryGetValue(playerId, out var known) && known.Contains(tile);

    public static bool ProvenBarren(GameWorld world, int playerId, TileCoord tile) =>
        world.SurveyedBarren.TryGetValue(playerId, out var barren) && barren.Contains(tile);

    // ---- learning a vein by seeing its mine ---------------------------------

    // A reveal made inside the sim (Sight.AfterReveal): every Mine or Mine
    // site standing on a vein inside the disc teaches this faction that vein.
    // Walks only the veins in the disc's bounding rows (a sorted-set view),
    // never the whole map.
    internal static void OnSight(Simulation sim, int playerId, TileCoord center, int radius)
    {
        if (radius <= 0 || playerId == Sim.Core.Bandits.BanditConstants.OwnerId) return;
        var world = sim.World;
        if (world.Veins.Count == 0) return;
        var lo = new TileCoord(int.MinValue, center.Y - radius);
        var hi = new TileCoord(int.MaxValue, center.Y + radius);
        var r2 = radius * radius;
        List<TileCoord>? seen = null;
        foreach (var t in world.Veins.GetViewBetween(lo, hi))
        {
            var dx = t.X - center.X;
            var dy = t.Y - center.Y;
            if (dx * dx + dy * dy > r2) continue;
            if (IsMineAt(world, t)) (seen ??= new List<TileCoord>()).Add(t);
        }
        if (seen is null) return;
        foreach (var t in seen) Learn(world, playerId, t);
    }

    // A Mine site has just gone up on `tile` (PlaceSiteIntent): every faction
    // whose live sight already covers it learns the vein now, rather than at
    // its next reveal. Players in id order.
    internal static void OnMineRaised(Simulation sim, TileCoord tile)
    {
        var world = sim.World;
        foreach (var playerId in world.Players.Keys)
        {
            if (playerId == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
            if (Sim.Core.Vision.View.Sees(world, playerId, tile)) Learn(world, playerId, tile);
        }
    }

    // A mine of any ore (a kind that must stand on a vein), raised or rising.
    // Pure read; the wire's Mined flag reads it too.
    public static bool IsMineAt(GameWorld world, TileCoord tile) =>
        world.Structures.TryGetValue(tile, out var s)
        && StructureCatalog.TryGetSpec(s is ConstructionSite c ? c.TargetKind : s.Kind, out var spec)
        && spec.RequiresVein;

    // ---- the knowledge writers' shared primitives --------------------------

    // Record that `playerId` knows the vein at `tile`. Returns true if it is
    // news. Callers: SurveyRules.Complete and Sight.Reveal only.
    internal static bool Learn(GameWorld world, int playerId, TileCoord tile)
    {
        if (!world.Veins.Contains(tile)) return false;
        if (!world.KnownVeins.TryGetValue(playerId, out var known))
            world.KnownVeins[playerId] = known = new SortedSet<TileCoord>(TileOrder.Instance);
        return known.Add(tile);
    }

    // Record a tile `playerId` has proven holds no vein. Caller:
    // SurveyRules.Complete only.
    internal static void MarkBarren(GameWorld world, int playerId, TileCoord tile)
    {
        if (!world.SurveyedBarren.TryGetValue(playerId, out var barren))
            world.SurveyedBarren[playerId] = barren = new SortedSet<TileCoord>(TileOrder.Instance);
        barren.Add(tile);
    }

    // SplitMix64 finalizer over (seed, x, y, salt) — integer-only, stable
    // across runtimes; the sim Rng is never touched.
    internal static ulong Hash(ulong seed, int x, int y, ulong salt)
    {
        var z = seed ^ salt
            ^ ((ulong)(uint)x * 0x9E3779B97F4A7C15UL)
            ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private const ulong DensitySalt = 0x5645494E5F444E53UL;   // "VEIN_DNS"
    private const ulong NetSalt     = 0x5645494E5F4E4554UL;   // "VEIN_NET"
}
