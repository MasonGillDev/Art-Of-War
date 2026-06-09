namespace Sim.Core.World;

// Append-only enum (serialized).
public enum UnitRole : byte
{
    None = 0,
    Builder = 1,
    Farmer = 2,
    Miner = 3,
    Lumberjack = 4,
    Quarryman = 5,
    Hauler = 6,
    Scout = 7,
    Boat = 8, // M12 — water vehicle (carries passengers + cargo)
}

public sealed class Unit
{
    public int Id { get; }
    public TileCoord Position { get; set; }
    // Init-from-outside, mutate-only-via-SetRoleForTraining. The setter
    // is `init` so object initializers (Snapshot, Genesis, Host, tests)
    // can populate at creation; the backing field is private so the only
    // post-construction writer is SetRoleForTraining below (called from
    // TrainUnitIntent.Resolve).
    private UnitRole _role = UnitRole.None;
    public UnitRole Role { get => _role; init => _role = value; }
    public int CargoCapacity { get; init; } = 1;

    // M12 — per-unit movement domain. Foot is the default for every
    // existing role; boats (Phase C) set Water. Snapshot.WriteUnits
    // serialises this; restore reads it before AddUnit so it persists.
    public Traversal Traversal { get; init; } = Traversal.Foot;

    // M12 — carrier semantics. For boats, PassengerCap is the maximum
    // number of units this hull can hold; Passengers is the live list
    // of embarked unit ids (canonical ascending iteration). Non-boats
    // have cap = 0 and an always-empty list.
    public int PassengerCap { get; init; }
    public SortedSet<int> Passengers { get; } = new();

    // M12 — passenger semantics. While EmbarkedOn is non-null, this
    // unit is off the TileGrid (removed from any per-tile index by
    // EmbarkIntent), invisible to combat, ineligible for solo intents
    // (Phase D audit), and contributes no vision. Cleared by
    // DisembarkIntent (passenger comes back onto the dock tile) or by
    // the carrier dying (drown — Phase E).
    public int? EmbarkedOn { get; set; }

    // Pure-read convenience. Tests + intents check this before applying
    // solo work.
    public bool IsEmbarked => EmbarkedOn is not null;
    // Player who owns this unit. Defaults to 0 for single-player scenarios.
    // Read by Vision (for explored/live-visibility) and by player-view filters.
    public int OwnerId { get; init; } = 0;

    public Activity Activity { get; private set; } = Activity.Idle;
    // The structure tile this unit is currently bound to (Working at an
    // extractor, Building at a construction site). Null when Idle/Moving/Hauling.
    public TileCoord? Assignment { get; private set; }

    // Monotonic counter bumped on every actual activity change. Future-scheduled
    // per-unit events (HaulPickupEvent, HaulDepositEvent) capture the epoch at
    // schedule time and fence on it at fire time — a mismatch means the unit
    // got retasked between scheduling and firing, and the stale event no-ops.
    // Same fencing-token pattern as ConstructionSite.ScheduledCompletion,
    // generalized to per-unit assignments.
    //
    // Byte is fine: race window between schedule and fire is ≪ 256 outstanding
    // events per unit. Wraps cleanly on overflow.
    public byte AssignmentEpoch { get; private set; }

    public Resource CargoResource { get; set; }
    public int CargoAmount { get; set; }

    // ---- M4 in-flight movement anchor (Phase A) ----
    // The committed remaining steps of the current movement chain. Null when
    // not moving. Set by MoveIntent.Resolve at command time, consumed step
    // by step by MoveArrivalEvent.Apply. Stored on the unit so RegenerateQueue
    // (M4 recovery) can rebuild the next MoveArrivalEvent purely from state.
    //
    // Storing the committed path (not recomputing on restore) is deliberate:
    // recomputing against current road conditions could yield a different
    // path than the live sim took, breaking determinism.
    public List<TileCoord>? PathRemaining { get; set; }
    public TileCoord? PathFinalDest { get; set; }
    public long? NextArrivalTick { get; set; }
    public long? NextArrivalSeq  { get; set; }

    // ---- M4 in-flight haul anchor (Phase A) ----
    // Drives the pickup/deposit dispatch at the end of the move chain in
    // place of MoveArrivalEvent.OnFinalArrival. Mutated by haul events;
    // cleared on completion. See HaulPlan.cs.
    public HaulPlan? HaulPlan { get; set; }

    // ---- M5 group membership ----
    // Set when this unit joins a Group via FormGroupIntent; cleared on
    // DisbandGroupIntent (or future Split/Merge). When non-null, solo
    // intents (MoveIntent, HaulIntent, Assign*) reject this unit — the
    // group's collective intents drive it instead. See Groups/Group.cs
    // and docs/architecture.md §8 (M5 entry).
    public int? GroupId { get; set; }

    // ---- M7 combat state ----
    // Current health. Default 0 is a sentinel for "not yet initialized";
    // GameWorld.AddUnit auto-fills from UnitCombatCatalog at insertion
    // time, so callers who hand-construct Units don't need to care.
    // Snapshot.ReadUnits sets Health to the serialized value BEFORE
    // calling AddUnit, so a damaged-to-3 unit restores to 3 (AddUnit's
    // auto-init is a no-op when Health > 0). A unit hitting 0 is removed
    // from world.Units in the same round (CombatRules.OnUnitDeath), so
    // a stored unit with Health == 0 shouldn't exist in practice.
    public int Health { get; set; }

    // M7 scaffolding for armor / training / equipment / temporary effects.
    // Empty today; the EffectivePower rollup reads through it so future
    // buff instances modify combat power without touching the round event.
    public List<Sim.Core.Combat.Buff> Buffs { get; } = new();

    // ---- M8 population state ----
    // The sim tick at which this unit was born. Immutable. Genesis units
    // get a negative BornTick (= -StartingAgeYears * TicksPerYear) so
    // they "age in" at the configured starting age. Hand-constructed
    // units (test fixtures + dev tooling) default to a deeply-negative
    // sentinel so they're effectively "adult-by-default" — the M8
    // training gates pass without each test having to set BornTick.
    // Tests that specifically exercise age behavior go through the
    // spec-aware Simulation ctor (which sets BornTick correctly per
    // FactionStartSpec.StartingAgeYears).
    public long BornTick { get; init; } = long.MinValue / 2;

    // The sim tick at which this unit will die of old age. Set by
    // Population.ScheduleLifespan at unit-creation time (genesis or birth),
    // never re-rolled. DeathSeq is the Seq of the scheduled DeathByAgeEvent
    // — same M4-anchor pattern as NextArrivalTick/Seq for movement.
    public long? DeathTick { get; set; }
    public long? DeathSeq { get; set; }

    public Unit(int id, TileCoord position) { Id = id; Position = position; }

    // The single mutation path for Activity. Intents call this rather than
    // poking the property; the transition table catches illegal hops before
    // they corrupt state. Bumps AssignmentEpoch on actual change so stale
    // per-unit events fire harmless on retasked units.
    public bool TrySetActivity(Activity next, TileCoord? assignment = null)
    {
        if (!ActivityTransitions.CanTransition(Activity, next)) return false;
        var changed = Activity != next;
        Activity = next;
        Assignment = next == Activity.Idle ? null : assignment;
        if (changed) unchecked { AssignmentEpoch++; }
        return true;
    }

    // Explicit epoch bump, independent of activity changes. Called by
    // MoveIntent.Resolve so a fresh move on an already-Idle unit still
    // invalidates the prior move chain's MoveArrivalEvents. Without this,
    // a move-then-move sequence on an Idle unit would leave both chains
    // running interleaved.
    internal void BumpEpoch() { unchecked { AssignmentEpoch++; } }

    // Restore-only. Used by Snapshot.Restore to rebuild a Unit's epoch without
    // running through TrySetActivity's bump logic.
    internal void RestoreAssignmentEpoch(byte epoch) => AssignmentEpoch = epoch;

    // Training — the post-construction mutation site for Role. Called
    // only from TrainUnitIntent.Resolve. The Role property's setter is
    // also internal (Snapshot needs object-initializer access), but this
    // helper is the documented mutation path; future audits can grep for
    // it to verify nothing else flips Role at runtime.
    internal void SetRoleForTraining(UnitRole newRole) => _role = newRole;
}
