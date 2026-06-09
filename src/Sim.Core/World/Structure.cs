namespace Sim.Core.World;

public abstract class Structure
{
    public TileCoord At { get; }
    public abstract StructureKind Kind { get; }
    // Player who owns this structure. Set at construction (genesis Castle:
    // player 0; built structures: inherited from the ConstructionSite via
    // BuildCompleteEvent, which gets it from PlaceSiteIntent). Defaults to
    // 0 for single-player scenarios.
    public int OwnerId { get; init; } = 0;
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

    // M13 — food consumption anchor. Holdings[Food] is reduced by
    // FoodConsumption.CatchUp ONLY; LastFoodConsumedTick advances by
    // completed periods (M9 carry-remainder discipline). Catch-up runs
    // at every rate-changing event (population add/remove, food deposit
    // here) so the rate between any two catch-ups is constant.
    public long LastFoodConsumedTick { get; set; }

    // M13 Phase C — famine anchor. Set by FoodConsumption.CatchUp at the
    // exact meal-boundary tick where food first failed to feed everyone.
    // Cleared by HaulDepositEvent.Apply (food path) when a deposit brings
    // Holdings[Food] back above 0.
    public long? FamineStartTick { get; set; }

    // M13 Phase C — predicted-next-dry-out anchor. The FamineCheckEvent
    // fences on (At == NextFamineCheckTick, Seq == NextFamineCheckSeq).
    // Both fields are set together by FoodConsumption.OnRateOrFoodChanged
    // and cleared together at fire time (after a successful match).
    public long? NextFamineCheckTick { get; set; }
    public long? NextFamineCheckSeq { get; set; }

    // M13 Phase D — next-scheduled starvation death anchor. Set when
    // FoodConsumption.CatchUp transitions to famine (first death scheduled
    // at FamineStartTick + StarvationStartDelay) and on each subsequent
    // StarvationDeathEvent firing (next at now + StarvationDeathInterval).
    // Cleared when famine ends (HaulDepositEvent food path) or when
    // population hits zero. Fenced like the famine check.
    public long? NextStarvationDeathTick { get; set; }
    public long? NextStarvationDeathSeq { get; set; }

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

    // M4 Phase A — Seq of the currently-scheduled ProductionTickEvent. Lets
    // RegenerateQueue rebuild the queued tick with its original Seq so
    // same-tick ordering survives recovery (M1 Phase F fairness contract).
    // Null iff TickArmed is false.
    public long? NextProductionTickSeq { get; set; }

    public Extractor(StructureKind kind, TileCoord at) : base(at)
    {
        _kind = kind;
        Spec = StructureCatalog.Spec(kind);
        if (Spec.RequiredBiome == Biome.None)
            throw new InvalidOperationException($"{kind} is not an extractor kind.");
    }

    public int FreeBuffer() => Math.Max(0, Spec.BufferCap - Buffer);
    public bool BufferFull() => Buffer >= Spec.BufferCap;

    // Centralizes the production re-arm rule so it lives in one place. Called
    // by AssignWorkersIntent (worker count crossed 0→1+) and, in Phase E, by
    // haul-pickup (free buffer space appeared).
    //
    // Idempotent: if already armed or conditions aren't met, does nothing.
    // Lives in Sim.Core.World; consumes Sim.Core.Logistics — that's a tiny
    // namespace coupling we accept for the convenience of one source of truth.
    internal void ArmIfDormant(Simulation sim)
    {
        if (TickArmed) return;
        if (Workers.Count == 0) return;
        if (BufferFull()) return;
        // M9: catch up tiles in radius using the PRE-START rate. TickArmed is
        // still false at this point, so the catch-up's rate scan correctly
        // EXCLUDES this extractor; after the catch-up the new (higher) rate
        // kicks in for future reads/transitions. See
        // BiomeDegradation.OnProductionTransition.
        Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
            sim.World, this, sim.Now, sim.World.BiomeDegradationConfig);
        TickArmed = true;
        NextProductionTickSeq = sim.Schedule(
            sim.Now + Spec.ProductionPeriodTicks,
            new ProductionTickEvent(At));
    }
}

// A construction-in-progress. Three life stages:
//
//   PENDING: site placed, materials/builders incomplete.
//     LastActiveAtTick == null, BuildPaused == false, ProgressTicks == 0.
//
//   ACTIVE: prereqs met, a BuildCompleteEvent is scheduled for
//     ScheduledCompletion. ProgressTicks holds work done in *prior* active
//     runs; live progress is implicit (Now - LastActiveAtTick).
//     LastActiveAtTick == sim.Now-when-started, BuildPaused == false,
//     ScheduledCompletion != null.
//
//   PAUSED: prereqs were broken mid-build. The scheduled BuildCompleteEvent
//     still sits in the queue but will fence out on fire (its At !=
//     ScheduledCompletion, which is null). ProgressTicks holds all work done
//     across active runs.
//     LastActiveAtTick == null, BuildPaused == true,
//     ScheduledCompletion == null.
//
// Pause/resume works without dequeuing because BuildCompleteEvent revalidates
// on fire — the "fencing token" pattern. ScheduledCompletion is the token.
public sealed class ConstructionSite : Structure
{
    public override StructureKind Kind => StructureKind.ConstructionSite;
    public StructureKind TargetKind { get; }
    public SortedDictionary<Resource, int> Required { get; }
    public SortedDictionary<Resource, int> Delivered { get; } = new();
    public int BuildDurationTicks { get; }
    public int RequiredBuilderCount { get; }

    // Accumulated build ticks from prior active runs. When ACTIVE, live
    // delta (Now - LastActiveAtTick) is on top of this.
    public long ProgressTicks { get; set; }
    public bool BuildPaused { get; set; }
    public long? LastActiveAtTick { get; set; }
    public long? ScheduledCompletion { get; set; }

    // M4 Phase A — Seq of the currently-scheduled BuildCompleteEvent. Same
    // recovery contract as Extractor.NextProductionTickSeq: lets
    // RegenerateQueue rebuild the queued completion with its original Seq.
    // Null iff ScheduledCompletion is null (paused or pending).
    public long? BuildCompleteSeq { get; set; }

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

    // ---- material accounting ----

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

    // ---- lifecycle / pause-resume ----

    // Walks units on this site's tile, counts those Building this exact site.
    public int BuildersPresent(GameWorld world)
    {
        var count = 0;
        foreach (var u in world.Units.Values)
        {
            if (u.Position != At) continue;
            if (u.Activity != World.Activity.Building) continue;
            if (u.Assignment != At) continue;
            count++;
        }
        return count;
    }

    public bool ConditionsMet(GameWorld world) =>
        MaterialsMet() && BuildersPresent(world) >= RequiredBuilderCount;

    public bool IsActive => LastActiveAtTick is not null;

    // Transition PENDING (or PAUSED) → ACTIVE. Call only when conditions are
    // met. Schedules the BuildCompleteEvent at the projected finish tick.
    public void StartOrResume(Simulation sim)
    {
        if (IsActive) return; // already running; idempotent
        var remaining = BuildDurationTicks - ProgressTicks;
        if (remaining < 0) remaining = 0; // safety; means we'd complete immediately
        LastActiveAtTick = sim.Now;
        BuildPaused = false;
        ScheduledCompletion = sim.Now + remaining;
        BuildCompleteSeq = sim.Schedule(ScheduledCompletion.Value, new BuildCompleteEvent(At));
    }

    // Transition ACTIVE → PAUSED. Banks the live progress, clears the fencing
    // token; any pre-scheduled BuildCompleteEvent now stales out on fire.
    public void Pause(long now)
    {
        if (!IsActive) return;
        ProgressTicks += now - LastActiveAtTick!.Value;
        if (ProgressTicks > BuildDurationTicks) ProgressTicks = BuildDurationTicks;
        LastActiveAtTick = null;
        ScheduledCompletion = null;
        BuildCompleteSeq = null;
        BuildPaused = true;
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
