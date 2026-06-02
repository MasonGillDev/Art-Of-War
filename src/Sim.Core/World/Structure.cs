namespace Sim.Core.World;

public abstract class Structure
{
    public TileCoord At { get; }
    public abstract StructureKind Kind { get; }
    protected Structure(TileCoord at) { At = at; }
}

// A bounded resource container. Castle and Stockpile both layer on this — they
// differ only in capacity and lose-condition status (the latter not modeled yet).
public abstract class StorageStructure : Structure
{
    public SortedDictionary<Resource, int> Holdings { get; } = new();
    public int Capacity { get; }
    protected StorageStructure(TileCoord at, int capacity) : base(at) { Capacity = capacity; }

    public int AmountOf(Resource r) =>
        Holdings.TryGetValue(r, out var v) ? v : 0;

    public int TotalHeld()
    {
        var total = 0;
        foreach (var v in Holdings.Values) total += v;
        return total;
    }

    public int FreeSpace() => Math.Max(0, Capacity - TotalHeld());

    // Deposits up to FreeSpace and returns the amount actually accepted. Caller
    // is responsible for handling overflow (e.g. leaving the rest on the unit).
    public int Deposit(Resource r, int amount)
    {
        if (amount <= 0 || r == Resource.None) return 0;
        var accepted = Math.Min(amount, FreeSpace());
        if (accepted == 0) return 0;
        Holdings.TryGetValue(r, out var current);
        Holdings[r] = current + accepted;
        return accepted;
    }

    // Withdraws up to `amount` and returns what was taken. Less than requested
    // means the holding had less; caller decides what to do with a partial pickup.
    public int Withdraw(Resource r, int amount)
    {
        if (amount <= 0 || r == Resource.None) return 0;
        if (!Holdings.TryGetValue(r, out var have) || have == 0) return 0;
        var taken = Math.Min(amount, have);
        var remaining = have - taken;
        if (remaining == 0) Holdings.Remove(r);
        else Holdings[r] = remaining;
        return taken;
    }
}

public sealed class Castle : StorageStructure
{
    public override StructureKind Kind => StructureKind.Castle;
    public Castle(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.Castle).StorageCapacity) { }
}

public sealed class Stockpile : StorageStructure
{
    public override StructureKind Kind => StructureKind.Stockpile;
    public Stockpile(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.Stockpile).StorageCapacity) { }
}

// PHASE-A PLACEHOLDER. One class for all four extractor kinds today.
//
// This will almost certainly split or grow at Phase D when production behavior
// lands. Farm and probably the other natural-resource extractors operate over
// a *working radius* of biome tiles, not just their own tile — that's a
// structural difference, not a numerical one, and a single parametrized class
// can't model it cleanly.
//
// The snapshot format below WILL change when this is revised. That's a known
// cost of the deferral; we accepted it to keep Phase A moving.
public sealed class Extractor : Structure
{
    public override StructureKind Kind => _kind;
    private readonly StructureKind _kind;
    public StructureSpec Spec { get; }

    // Id-sorted so snapshot canonicalization is order-stable.
    public SortedSet<int> Workers { get; } = new();
    public int Buffer { get; set; }
    public long LastProductionTick { get; set; }
    // True iff a ProductionTick is currently scheduled. When the buffer fills
    // or workers leave, the next tick fires, finds nothing to do, sets this
    // false, and does NOT reschedule. A haul-pickup re-arms by scheduling a
    // tick and setting this true again.
    public bool TickArmed { get; set; }

    public Extractor(StructureKind kind, TileCoord at) : base(at)
    {
        _kind = kind;
        Spec = StructureCatalog.Spec(kind);
        if (Spec.RequiredBiome == Biome.None)
            throw new InvalidOperationException($"{kind} is not an extractor kind.");
    }

    public int FreeBuffer() => Math.Max(0, Spec.BufferCap - Buffer);
    public bool BufferFull() => Buffer >= Spec.BufferCap;
}

// Accepts deposits until requirements met + builder count present, then
// progresses through `BuildDurationTicks`. Pauses if builder count drops below
// requirement mid-build; resumes when it returns.
public sealed class ConstructionSite : Structure
{
    public override StructureKind Kind => StructureKind.ConstructionSite;
    public StructureKind TargetKind { get; }
    public SortedDictionary<Resource, int> Required { get; }
    public SortedDictionary<Resource, int> Delivered { get; } = new();
    public int BuildDurationTicks { get; }
    public int RequiredBuilderCount { get; }

    // Progress accumulated while the build was active. Capped at BuildDurationTicks.
    public long ProgressTicks { get; set; }
    public bool BuildPaused { get; set; }
    // Mirrors Extractor.TickArmed semantics — see comment there.
    public bool TickArmed { get; set; }

    public ConstructionSite(TileCoord at, StructureKind targetKind)
        : base(at)
    {
        var spec = StructureCatalog.Spec(targetKind);
        if (!spec.IsPlayerBuildable)
            throw new InvalidOperationException($"{targetKind} is not player-buildable.");
        TargetKind = targetKind;
        Required = new SortedDictionary<Resource, int>();
        foreach (var (r, n) in spec.BuildCost) Required[r] = n;
        BuildDurationTicks = spec.BuildDurationTicks;
        RequiredBuilderCount = spec.RequiredBuilderCount;
    }

    public int Outstanding(Resource r)
    {
        Required.TryGetValue(r, out var req);
        Delivered.TryGetValue(r, out var got);
        return Math.Max(0, req - got);
    }

    public bool MaterialsMet()
    {
        foreach (var (r, need) in Required)
        {
            Delivered.TryGetValue(r, out var got);
            if (got < need) return false;
        }
        return true;
    }

    public int Deposit(Resource r, int amount)
    {
        if (amount <= 0 || !Required.ContainsKey(r)) return 0;
        var need = Outstanding(r);
        var accepted = Math.Min(amount, need);
        if (accepted == 0) return 0;
        Delivered.TryGetValue(r, out var current);
        Delivered[r] = current + accepted;
        return accepted;
    }
}

// Reserved for the fog milestone. Kept as a placeholder type so the
// StructureKind enum value has a corresponding class (snapshot serializer can
// dispatch on Kind without a missing case).
public sealed class Tower : Structure
{
    public override StructureKind Kind => StructureKind.Tower;
    public Tower(TileCoord at) : base(at) { }
}
