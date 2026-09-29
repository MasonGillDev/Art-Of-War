namespace Sim.Core.Scouting;

// Append-only enum (serialized). An idol's grade decides how long, and how
// wide, the sight it grants.
public enum IdolKind : byte
{
    Lesser  = 1,
    Greater = 2,
}

// M38 — idol knobs (docs/scouting-secrets.md). Genesis-set, then immutable,
// snapshotted (v38): the scatter reads Count/spacing once at genesis, and an
// activation mid-game reads the grade table.
//
// Count defaults to 0 so existing scenarios place no idols and draw no Rng
// (their hashes stay bit-identical); the host opts in (--idols N, default 20).
public readonly record struct IdolConfig(
    int Count,
    // Every Nth idol placed is a Greater one (the rest Lesser).
    int GreaterEvery,
    long LesserTicks,
    int LesserRadius,
    long GreaterTicks,
    int GreaterRadius,
    // Placement: never within this many tiles (Chebyshev) of a castle, and
    // never within MinSpacing of another idol.
    int MinDistanceFromCastle,
    int MinSpacing)
{
    // Explicit, like RoyaltyConfig: `new IdolConfig()` on a record struct with
    // optional primary-ctor parameters would be all zeros, not these defaults.
    public IdolConfig() : this(
        Count: 0,
        GreaterEvery: 4,
        LesserTicks: 2 * Time.Day,
        LesserRadius: 6,
        GreaterTicks: 5 * Time.Day,
        GreaterRadius: 10,
        MinDistanceFromCastle: 12,
        MinSpacing: 10)
    { }

    public long TicksFor(IdolKind kind) => kind == IdolKind.Greater ? GreaterTicks : LesserTicks;
    public int RadiusFor(IdolKind kind) => kind == IdolKind.Greater ? GreaterRadius : LesserRadius;
}

// M38 — an activated idol's gift: the owner sees a circle of the world, fog
// and all lifted, until EndsTick. Real sight: View.VisibleTiles, View.Sees and
// BanditRules.IsSeenByAnyPlayer all include it.
//
// ANCHOR: (EndsTick, EndSeq) is its VisionGrantExpiryEvent, regenerated on
// restore. Mutated only by Idols (grant, expire).
public sealed class VisionGrant
{
    public int GrantId { get; init; }
    public int OwnerId { get; init; }
    public TileCoord Center { get; init; }
    public int Radius { get; init; }
    public long EndsTick { get; init; }
    public long EndSeq { get; internal set; }

    public bool Covers(TileCoord t)
    {
        var dx = t.X - Center.X;
        var dy = t.Y - Center.Y;
        return dx * dx + dy * dy <= Radius * Radius;
    }
}

// M38 — idols (docs/scouting-secrets.md): statues in the fog. A unit standing
// on one activates it; its owner is granted a random circle of sight over
// ground they have never explored, for the idol's time; the idol crumbles.
//
// SINGLE MUTATION POINT for GameWorld.VisionGrants / NextVisionGrantId
// (Snapshot restore aside): Activate (from ActivateIdolIntent) and Expire (from
// VisionGrantExpiryEvent). Placement is IdolScatter, at genesis only.
public static class Idols
{
    internal static void Activate(Simulation sim, Unit unit, Idol idol)
    {
        var world = sim.World;
        var cfg = world.IdolConfig;
        var radius = Math.Max(1, cfg.RadiusFor(idol.Grade));
        var center = PickCenter(sim, unit.OwnerId);

        var grant = new VisionGrant
        {
            GrantId = world.NextVisionGrantId++,
            OwnerId = unit.OwnerId,
            Center = center,
            Radius = radius,
            EndsTick = sim.Now + Math.Max(1, cfg.TicksFor(idol.Grade)),
        };
        world.VisionGrants[grant.GrantId] = grant;
        grant.EndSeq = sim.Schedule(grant.EndsTick, new VisionGrantExpiryEvent(grant.GrantId));

        // The ground under the circle is explored from now on, as by any eye.
        Sight.Reveal(world, unit.OwnerId, center, radius, sim.Now);
        Sight.AfterReveal(sim, unit.OwnerId, center, radius);

        // Single use: the statue crumbles. The activator, standing on it, sees
        // it go (a charted idol is struck).
        world.Structures.Remove(idol.At);
        Charts.OnSecretGone(sim, idol.At);
        // Anyone else on the way to it has nothing to wake.
        Sim.Core.Intents.GoalRules.OnStructureRemoved(sim, idol.At, "the idol crumbled");
    }

    internal static void Expire(Simulation sim, VisionGrant grant) =>
        sim.World.VisionGrants.Remove(grant.GrantId);

    // Somewhere the activator has never explored, on land; failing that (a
    // player who has seen the whole map), anywhere on land. Drawn from the
    // sim's RNG: nobody can predict it, and replay reproduces it.
    private static TileCoord PickCenter(Simulation sim, int ownerId)
    {
        var world = sim.World;
        var grid = world.Grid;
        world.Explored.TryGetValue(ownerId, out var explored);
        var wild = new List<TileCoord>();
        var land = new List<TileCoord>();
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var t = new TileCoord(x, y);
                if (grid.BiomeAt(t) is Biome.Water or Biome.None) continue;
                land.Add(t);
                if (explored is null || !explored.Contains(t)) wild.Add(t);
            }
        var pool = wild.Count > 0 ? wild : land;
        return pool.Count == 0 ? new TileCoord(0, 0) : pool[sim.Rng.NextInt(pool.Count)];
    }

    // Any grant of `playerId`'s covering `tile`? Pure read.
    public static bool GrantSees(GameWorld world, int playerId, TileCoord tile)
    {
        foreach (var g in world.VisionGrants.Values)
            if (g.OwnerId == playerId && g.Covers(tile)) return true;
        return false;
    }
}

// M38 — a vision grant ran out. Fenced on the grant's (EndsTick, EndSeq).
public sealed class VisionGrantExpiryEvent : ScheduledEvent
{
    public int GrantId { get; }
    public VisionGrantExpiryEvent(int grantId) { GrantId = grantId; }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.VisionGrants.TryGetValue(GrantId, out var g) || g.EndsTick != At || g.EndSeq != Seq)
        {
            Outcome = IntentOutcome.Reject($"stale vision grant expiry {GrantId}");
            return;
        }
        Idols.Expire(sim, g);
    }

    public override string Describe() => $"VisionGrantExpiry(grant={GrantId})";
}

// M38 — a unit activates an idol (docs/scouting-secrets.md): at once if it is
// standing on it, else it walks there first (a goal, like a loot errand) and
// activates on arrival. Anyone may: idols are nobody's, first come, first
// served.
public sealed class ActivateIdolIntent : Intent
{
    public int UnitId { get; }
    // Which idol. Null = the one under their feet.
    public TileCoord? IdolTile { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ActivateIdolIntent(int unitId, TileCoord? idolTile = null)
    {
        UnitId = unitId;
        IdolTile = idolTile;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        var tile = IdolTile ?? unit.Position;
        if (!world.Structures.TryGetValue(tile, out var s) || s is not Idol idol)
            return IntentOutcome.Reject($"no idol at {tile.X},{tile.Y}");

        if (unit.Position == tile)
        {
            Idols.Activate(sim, unit, idol);
            return IntentOutcome.Applied;
        }
        return Sim.Core.Intents.GoalRules.Begin(sim, unit, new GoalPlan(GoalKind.ActivateIdol, tile))
            ? IntentOutcome.Applied
            : IntentOutcome.Reject($"unit {UnitId} cannot reach the idol at {tile.X},{tile.Y}");
    }

    public override string Describe() => IdolTile is { } t
        ? $"ActivateIdol(unit={UnitId} @ {t.X},{t.Y})"
        : $"ActivateIdol(unit={UnitId})";
}

// M38 — genesis idol scatter. Same fog rule as CacheScatter (only ground no
// player has seen at genesis), plus a castle distance floor and a spacing
// rule so twenty idols do not clump. Runs once in the Simulation spec-ctor
// AFTER the cache scatter; Count == 0 draws no Rng.
public static class IdolScatter
{
    public static void Scatter(GameWorld world, Rng rng, IdolConfig cfg)
    {
        if (cfg.Count <= 0) return;

        var seen = new HashSet<TileCoord>();
        foreach (var set in world.Explored.Values) seen.UnionWith(set);
        var castles = world.Structures.Values.Where(s => s.Kind == StructureKind.Castle).Select(s => s.At).ToList();

        var grid = world.Grid;
        var candidates = new List<TileCoord>();
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var t = new TileCoord(x, y);
                if (seen.Contains(t) || world.Structures.ContainsKey(t)) continue;
                if (grid.BiomeAt(t) is Biome.Water or Biome.None) continue;
                if (castles.Any(c => Math.Max(Math.Abs(c.X - x), Math.Abs(c.Y - y)) < cfg.MinDistanceFromCastle)) continue;
                candidates.Add(t);
            }

        // Partial Fisher-Yates in canonical order, keeping a pick only if it
        // clears the spacing rule against the idols already placed.
        var placed = new List<TileCoord>();
        for (var i = 0; i < candidates.Count && placed.Count < cfg.Count; i++)
        {
            var j = i + rng.NextInt(candidates.Count - i);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            var t = candidates[i];
            if (placed.Any(p => Math.Max(Math.Abs(p.X - t.X), Math.Abs(p.Y - t.Y)) < cfg.MinSpacing)) continue;
            placed.Add(t);
            var grade = cfg.GreaterEvery > 0 && placed.Count % cfg.GreaterEvery == 0 ? IdolKind.Greater : IdolKind.Lesser;
            world.AddStructure(new Idol(t, grade) { OwnerId = Sim.Core.Caches.CacheConstants.OwnerId });
        }
    }
}
