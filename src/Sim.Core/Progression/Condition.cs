namespace Sim.Core.Progression;

// Append-only enum. How the world stands right now, read live: nothing about a
// gauge is copied into the ledger.
public enum ProgressGauge : byte
{
    Population    = 1,
    TilesExplored = 2,
}

// M37 — when a milestone fires (docs/progression.md). A small closed set of
// typed conditions, read against the player's ledger and the world. Pure
// reads: Holds never writes.
public abstract record Condition
{
    public abstract bool Holds(GameWorld world, Player player, ProgressLedger ledger);

    // The sum of these counters has reached N ("soldiers or archers trained").
    public static Condition AtLeast(long n, params ProgressKey[] keys) => new CountAtLeast(keys, n);
    public static Condition Gauge(ProgressGauge gauge, long n) => new GaugeAtLeast(gauge, n);
    public static Condition All(params Condition[] parts) => new AllOf(parts);
    public static Condition Any(params Condition[] parts) => new AnyOf(parts);
    public static Condition Fired(int milestoneId) => new HasFired(milestoneId);
}

public sealed record CountAtLeast(IReadOnlyList<ProgressKey> Keys, long N) : Condition
{
    public override bool Holds(GameWorld world, Player player, ProgressLedger ledger)
    {
        long sum = 0;
        foreach (var k in Keys) sum += ledger.Count(k);
        return sum >= N;
    }
}

public sealed record GaugeAtLeast(ProgressGauge Which, long N) : Condition
{
    public override bool Holds(GameWorld world, Player player, ProgressLedger ledger) =>
        Read(world, player, Which) >= N;

    public static long Read(GameWorld world, Player player, ProgressGauge gauge) => gauge switch
    {
        ProgressGauge.Population    => player.PopulationCount,
        ProgressGauge.TilesExplored => world.Explored.TryGetValue(player.Id, out var set) ? set.Count : 0,
        _ => 0,
    };
}

public sealed record AllOf(IReadOnlyList<Condition> Parts) : Condition
{
    public override bool Holds(GameWorld world, Player player, ProgressLedger ledger)
    {
        foreach (var c in Parts) if (!c.Holds(world, player, ledger)) return false;
        return true;
    }
}

public sealed record AnyOf(IReadOnlyList<Condition> Parts) : Condition
{
    public override bool Holds(GameWorld world, Player player, ProgressLedger ledger)
    {
        foreach (var c in Parts) if (c.Holds(world, player, ledger)) return true;
        return false;
    }
}

public sealed record HasFired(int MilestoneId) : Condition
{
    public override bool Holds(GameWorld world, Player player, ProgressLedger ledger) =>
        ledger.HasFired(MilestoneId);
}
