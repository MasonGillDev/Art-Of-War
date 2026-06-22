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

    // M24 — siege HP. Sentinel 0 at construction is auto-filled from
    // StructureCatalog.Spec(Kind).BaseHealth in GameWorld.AddStructure,
    // mirroring Unit.Health / UnitCombatCatalog. A spec BaseHealth of 0
    // (Cache, Canal, Rubble) keeps Health = 0 forever and the combat
    // round treats the structure as INDESTRUCTIBLE (skipped). Snapshot
    // reads the persisted value BEFORE AddStructure runs, so a partially
    // damaged structure restores at its damaged HP. See
    // docs/sieges-and-conquest.md.
    public int Health { get; set; }

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

// M19 — Castle is a FOOD HOME (Sim.Core.Food.IFoodHome): the uncapped
// mess hall for everyone not housed elsewhere. Its consumption rate is
// the owner's population MINUS every housed resident (derived in
// FoodConsumption.ResidentsOf).
public sealed class Castle : StorageStructure, Sim.Core.Food.IFoodHome
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
    // Cleared by CargoTransfer.DepositInto (food path) when a deposit
    // pays FoodDebt all the way back to zero.
    public long? FamineStartTick { get; set; }

    // Famine DEBT (2026-06-11 rework, docs/food-consumption.md). Every
    // meal the larder can't cover lands here instead of being forgiven:
    // the castle's effective food level is Holdings[Food] − FoodDebt
    // (negative on the HUD during famine). Deposits pay the debt FIRST;
    // famine ends — and starvation deaths stop — only when the debt is
    // repaid in full. Invariant: FoodDebt > 0 ⇔ FamineStartTick set
    // (both mutate only in FoodConsumption.CatchUp and
    // CargoTransfer.DepositInto).
    public int FoodDebt { get; set; }

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
    // Cleared when the famine debt is fully repaid (CargoTransfer food
    // path) or when no citizens remain to kill. Fenced like the famine
    // check.
    public long? NextStarvationDeathTick { get; set; }
    public long? NextStarvationDeathSeq { get; set; }

    public Castle(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.Castle).StorageCapacity) { }
}

public sealed class Stockpile : StorageStructure
{
    public override StructureKind Kind => StructureKind.Stockpile;
    public Stockpile(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.Stockpile).StorageCapacity) { }
}

// Military training + equipment crafting (docs/military-training.md,
// docs/equipment-model.md). Storage holds craft inputs and finished
// weapons; TrainUnitIntent resolves Soldier/Archer here via
// RoleTrainerCatalog; CraftEquipmentIntent converts holdings in place.
public sealed class Barracks : StorageStructure
{
    public override StructureKind Kind => StructureKind.Barracks;
    public Barracks(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.Barracks).StorageCapacity) { }
}

// M23 — a loot cache: an UNOWNED StorageStructure holding a scattered bundle
// of resources / gear, discovered in the fog and looted with LootCacheIntent
// (cargo-capped; removed when empty). Genesis scatters them
// (Sim.Core.Caches.CacheScatter); they are never player-built. Capacity comes
// from the catalog like the other storage kinds so the snapshot's
// capacity-drift check holds — it is a stable ceiling for any rolled loot
// bundle, not a gameplay number. See docs/loot-caches.md.
public sealed class Cache : StorageStructure
{
    public override StructureKind Kind => StructureKind.Cache;
    public Cache(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.Cache).StorageCapacity) { }
}

// One class for all four extractor kinds. The Phase-A deferral note that
// lived here ("extractors operate over a working radius of biome tiles...
// the snapshot format WILL change") was paid off by M15: claiming kinds
// (LumberCamp, Farm — Spec.ClaimCount > 0) work an explicit set of
// CLAIMED tiles (docs/extraction-claims.md); FormatVersion bumped to 10.
// Quarry/Mine remain own-tile-only until their ladder extension.
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

    // M15 — extraction claims (docs/extraction-claims.md). The tiles this
    // extractor works: the degradation footprint, the exclusion territory,
    // and the production-taper input. ALWAYS kept in canonical (y, x)
    // order — writers re-sort; the snapshot writes verbatim. Empty for
    // non-claiming kinds (Spec.ClaimCount == 0) and for hand-built
    // fixtures until ArmIfDormant's lazy auto-claim fills it. Get-only
    // list (the Unit.Buffs pattern): mutation = contents only, from the
    // four audited writers (PlaceSiteIntent via the site,
    // BuildCompleteEvent transfer-copy, ArmIfDormant lazy fill, restore).
    public List<TileCoord> ClaimTiles { get; } = new();

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
        if (Spec.ClaimCount > 0)
        {
            // M15 lazy auto-claim: hand-built extractors (test fixtures,
            // dev tooling) have no intent-time claim — fill it here with
            // the same deterministic selector PlaceSiteIntent uses. Safe:
            // ArmIfDormant only runs inside event/intent resolution against
            // a COMPLETE world, never during snapshot restore (restored
            // extractors carry their claims and skip this). See
            // docs/extraction-claims.md.
            if (ClaimTiles.Count == 0
                && Claims.AutoSelect(sim.World, At, Spec, sim.Now) is { } auto)
                ClaimTiles.AddRange(auto);
            // Decline to arm when the claim is unfillable or fully out of
            // band: arming would insta-dormant on the first tick, and each
            // spurious transition pair drops sub-period recovery carry on
            // the claim tiles. The haul-pickup / assign-workers caller gets
            // a clean no-op instead.
            if (Claims.InBandClaimCount(sim.World, this, sim.Now) == 0) return;
        }
        // M9: catch up the worked tiles using the PRE-START rate. TickArmed
        // is still false at this point, so the catch-up's rate derivation
        // correctly EXCLUDES this extractor (claims without TickArmed
        // contribute no rate); after the catch-up the new (higher) rate
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

    // M12 — Dock-only: the water tile that the finished Dock's `Slip`
    // gets initialised with. Carried through from PlaceSiteIntent and
    // read by BuildCompleteEvent when the dock instance is constructed.
    // Null for all non-Dock targets. Settable for snapshot restore.
    public TileCoord? DockSlip { get; set; }

    // M15 — claiming targets only (LumberCamp/Farm): the claim reserved at
    // PLACEMENT, so two in-flight sites can't promise the same land.
    // BuildCompleteEvent COPIES it onto the finished Extractor. Canonical
    // (y, x) order, same discipline as Extractor.ClaimTiles. Empty for
    // non-claiming targets.
    public List<TileCoord> ClaimTiles { get; } = new();

    // M21 — Canal targets only: the ordered path of land tiles this build
    // floods into Water on completion (docs/canals.md). The anchor tile
    // (`At`, where materials haul and builders gather) is path[0]. A canal is
    // priced and timed PER TILE — the ctor scales Required and
    // BuildDurationTicks by the path length. Empty for every other target;
    // also serves as the reservation source (CanalReservation scans it so no
    // other build/claim can land on a tile a canal already promised).
    public List<TileCoord> CanalPath { get; } = new();

    public ConstructionSite(TileCoord at, StructureKind targetKind,
        IReadOnlyList<TileCoord>? canalPath = null)
        : base(at)
    {
        var spec = StructureCatalog.Spec(targetKind);
        if (!spec.IsPlayerBuildable)
            throw new InvalidOperationException($"{targetKind} is not player-buildable.");
        TargetKind = targetKind;
        // Canal cost/time scale with the number of tiles dug. A canal MUST
        // carry a non-empty path (PlaceCanalIntent supplies it); any other
        // kind ignores the parameter.
        var scale = 1;
        if (targetKind == StructureKind.Canal)
        {
            if (canalPath is null || canalPath.Count == 0)
                throw new InvalidOperationException(
                    "Canal ConstructionSite requires a non-empty path (use PlaceCanalIntent).");
            CanalPath.AddRange(canalPath);
            scale = canalPath.Count;
        }
        Required = new SortedDictionary<Resource, int>();
        foreach (var (r, n) in spec.BuildCost) Required[r] = n * scale;
        BuildDurationTicks = spec.BuildDurationTicks * scale;
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

// M24 — Rubble. What's left when a structure is razed (Castle, Barracks,
// etc.). Indestructible (BaseHealth = 0), no holdings, no production, no
// owner (OwnerId = OwnerIds.Destroyed, the -3 sentinel — never a living
// player, so no iteration over "player N's structures" picks it up). Its
// only mechanical effect is OCCUPYING THE TILE: PlaceSiteIntent and
// PlaceCanalIntent reject any target tile that already holds rubble.
// See docs/sieges-and-conquest.md.
public sealed class Rubble : Structure
{
    public override StructureKind Kind => StructureKind.Rubble;
    public Rubble(TileCoord at) : base(at) { }
}

// Training — School. A unit standing on this tile can issue
// TrainUnitIntent to flip its UnitRole. No production, no storage; the
// structure just exists as a placeable seam for training intents.
public sealed class School : Structure
{
    public override StructureKind Kind => StructureKind.School;
    public School(TileCoord at) : base(at) { }
}

// M20 — Lodge. The intelligence structure. No production, no storage; like
// School it is a placeable seam — its presence (completed, owned) is what
// gates DispatchScoutIntent. See docs/m20-scouting-reports-spec.md.
public sealed class Lodge : Structure
{
    public override StructureKind Kind => StructureKind.Lodge;
    public Lodge(TileCoord at) : base(at) { }
}

// M12 — Dock. Built on a land tile 4-adjacent to at least one Water
// tile. The Water tile chosen at build-time is the dock's "slip": new
// boats spawn there (Phase C production-job) and embarking units must
// be on this Dock's tile with the boat on its slip.
public sealed class Dock : Structure
{
    public override StructureKind Kind => StructureKind.Dock;
    public TileCoord Slip { get; }
    public Dock(TileCoord at, TileCoord slip) : base(at) { Slip = slip; }

    // M12 Phase C — boat-production anchor.
    //
    // After BuildCompleteEvent fires on this dock, a
    // BoatProductionTickEvent is scheduled at sim.Now + spec
    // ProductionPeriodTicks; ProductionArmed flips to true and
    // NextProductionTickSeq stashes the Seq for snapshot recovery.
    // When the tick fires:
    //   * Slip free? Spawn a Boat unit on Slip, reschedule next tick,
    //     keep ProductionArmed = true.
    //   * Slip occupied? ProductionArmed = false, NextProductionTickSeq
    //     = null. The MoveArrivalEvent slip-clear hook re-arms.
    public bool ProductionArmed { get; set; }
    public long? NextProductionTickSeq { get; set; }
    // The tick the LAST production fired (or the dock's build-complete
    // tick before any boat is produced). Snapshot regen uses
    // LastProductionTick + spec.ProductionPeriodTicks to reconstruct
    // the next-event time when ProductionArmed is true.
    public long LastProductionTick { get; set; }
}
