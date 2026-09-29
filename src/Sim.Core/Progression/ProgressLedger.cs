namespace Sim.Core.Progression;

// Append-only enum (serialized). What a counter counts; ProgressKey.Sub says
// which role / structure kind / resource.
public enum ProgressStat : byte
{
    // Sub = (int)UnitRole. Every finished training, retrains included.
    RoleTrained        = 1,
    // Sub = (int)StructureKind. Every build that stands up a structure.
    StructureCompleted = 2,
    // Sub = (int)Resource. Units of output a refiner made.
    ResourceRefined    = 3,
    // Sub = the milestone that raised the threat. Every raider dead, or fled
    // empty-handed.
    ThreatRepelled     = 4,
    // Sub = the milestone that raised the threat. A raider got away with loot.
    ThreatLost         = 5,
    // Sub = the milestone that raised the camp. Credited to every owner
    // standing on the camp when it fell.
    CampRazed          = 6,
}

// One counter's name: a stat and its subject (0 when the stat has none).
public readonly record struct ProgressKey(ProgressStat Stat, int Sub = 0) : IComparable<ProgressKey>
{
    public int CompareTo(ProgressKey other)
    {
        var c = Stat.CompareTo(other.Stat);
        return c != 0 ? c : Sub.CompareTo(other.Sub);
    }

    public static ProgressKey Trained(UnitRole role) => new(ProgressStat.RoleTrained, (int)role);
    public static ProgressKey Completed(StructureKind kind) => new(ProgressStat.StructureCompleted, (int)kind);
    public static ProgressKey Refined(Resource resource) => new(ProgressStat.ResourceRefined, (int)resource);
    public static ProgressKey Repelled(int milestoneId) => new(ProgressStat.ThreatRepelled, milestoneId);
    public static ProgressKey Lost(int milestoneId) => new(ProgressStat.ThreatLost, milestoneId);
    public static ProgressKey CampRazed(int milestoneId) => new(ProgressStat.CampRazed, milestoneId);
}

// M37 — one player's progress (docs/progression.md): counters that only go up,
// and the milestones that have fired. A counter records that something
// HAPPENED, so "trained 6 soldiers" stays true after those soldiers die, and a
// milestone can be neither dodged nor re-armed by disbanding.
//
// Present only on enrolled (human) players; Player.Progress is null for the
// rest. Mutated ONLY through Progression.Bump and Progression.Fire (and
// Snapshot on restore). Sorted collections, so the snapshot writes it in one
// canonical order.
public sealed class ProgressLedger
{
    internal SortedDictionary<ProgressKey, long> Counts { get; } = new();
    internal SortedSet<int> Fired { get; } = new();

    public long Count(ProgressKey key) => Counts.TryGetValue(key, out var n) ? n : 0;
    public bool HasFired(int milestoneId) => Fired.Contains(milestoneId);
    public IReadOnlyCollection<int> FiredIds => Fired;
    public IEnumerable<KeyValuePair<ProgressKey, long>> AllCounts => Counts;
}
