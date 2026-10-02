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
    Soldier = 9, // military — melee tank, trained at Barracks
    Archer = 10, // military — glass cannon, trained at Barracks (ranged-from-adjacent deferred)
    Bandit = 11, // M16 — NPC raider; never trainable, spawned only by the bandit driver
    // M31 — the dynasty as ROLES rather than a derived tag.
    //
    // NEITHER IS TRAINABLE, IN EITHER DIRECTION. No citizen is trained into a
    // crown (RoleTrainerCatalog maps both to no trainer) and no monarch is ever
    // trained out of one (TrainUnitIntent refuses a royal subject outright).
    // Royalty is for life; the only thing that changes a royal role is death,
    // through Succession.
    //
    // Their combat numbers are deliberately IDENTICAL to a citizen's: the design
    // is that a king is an ordinary person carrying an extraordinary aura, and
    // giving the body itself better stats would quietly make the crown a
    // combat upgrade on top of the buff it already projects.
    King = 12,
    Heir = 13,
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

    // Derived from Role via UnitCargoCatalog, PLUS any buff cargo modifiers
    // (M-cart: a cart adds carry capacity; rolled up live here, summed across
    // buffs). When TrainUnitIntent flips a citizen to Hauler, the cap jumps to
    // HaulerCapacity automatically — no second mutation, no role/cap drift; a
    // cart buff stacks on top of whatever the role gives. docs/cart.md.
    public int CargoCapacity
    {
        get
        {
            var cap = Sim.Core.Logistics.UnitCargoCatalog.CapacityFor(_role);
            foreach (var b in Buffs) cap += b.CargoModifier;
            return cap < 0 ? 0 : cap;
        }
    }

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

    // Everything this unit carries — any mix of resources under one
    // CargoCapacity (M36, docs/hauling-queue-and-routes.md).
    public CargoHold Cargo { get; } = new();

    // Total units aboard, all resources together.
    public int CargoAmount => Cargo.Total;

    // The resource with the most aboard (None when empty). Exact for every
    // single-resource carrier; for a mixed load it is only a label — code
    // that moves cargo reads Cargo, never this.
    public Resource CargoResource => Cargo.Dominant;

    // ---- Movement (M43: one movement, on subtiles) ----
    // A unit that is going somewhere is WALKING: SubtileRoute (below) holds the steps
    // left, one queued step event walks the next. PathFinalDest is the TILE it was
    // ordered to (null for a route the player drew square by square, which has no
    // errand): what the wire shows as its destination, what a battle or a fight
    // leaves it to resume, and what marks an errand walk. Stored, never recomputed on
    // restore (architecture §4 rule 7): a committed path was chosen against the world
    // at command time.
    public TileCoord? PathFinalDest { get; set; }

    // Walking now: steps left in the walk.
    public bool IsWalking => SubtileRoute is { Count: > 0 };

    // ---- M4 in-flight haul anchor (Phase A) ----
    // Drives the pickup/deposit dispatch at the end of the move chain in
    // place of MoveArrivalEvent.OnFinalArrival. Mutated by haul events;
    // cleared on completion. See HaulPlan.cs.
    public HaulPlan? HaulPlan { get; set; }

    // ---- M31 parentage ----
    // The two units whose breeding cycle produced this one. Null for genesis
    // units and for anything spawned outside BirthEvent (bandits, boats).
    //
    // Stored because it is NOT derivable: BirthEvent has the parent ids in
    // hand and used to drop them on the floor, which meant "the eldest living
    // child of the king" was not a pure read over current state but a fact the
    // world had never recorded. Both parents rather than one: the sim has no
    // gender, so there is no father to privilege, and the pair is the reusable
    // bone for kinship, inheritance and chronicler lineage prose.
    // docs/m31-king-dynasty-spec.md.
    public int? ParentAId { get; init; }
    public int? ParentBId { get; init; }

    // ---- M30 in-flight goal anchor ----
    // Set by GoalRules.Begin (from AssignWorkers/AssignBuilders/BeginBreeding
    // when the unit isn't standing where the work is), dispatched by
    // MoveArrivalEvent on final arrival, cleared by GoalRules on every exit
    // path (completed, dissolved, countermanded by MoveIntent). While a goal
    // is waiting on a precondition the unit sits in Activity.Waiting at the
    // target tile. See GoalPlan.cs and docs/goal-shaped-intents.md.
    public GoalPlan? Goal { get; set; }

    // ---- M44 in-flight survey anchor ----
    // Set by SurveyIntent (via SurveyRules.Begin), cleared by SurveyRules on
    // every exit (report, cancel by retask, a failed walk). While digging,
    // the unit sits in Activity.Waiting at the slope and the plan carries
    // the SurveyCompleteEvent anchor. See Mining/SurveyPlan.cs.
    public Sim.Core.Mining.SurveyPlan? Survey { get; set; }

    // ---- M29 in-flight pursuit anchor ----
    // Set by EngageUnitIntent, re-pathed one hop at a time by
    // MoveArrivalEvent, cleared by PursuitRules on every exit path (caught,
    // target gone, leash broken, lost from sight). Mutually exclusive with
    // HaulPlan in practice: a chasing unit is not Idle, so HaulIntent
    // rejects it. See Pursuit.cs and docs/patrols.md.
    public Pursuit? Pursuit { get; set; }

    // ---- M46 saved task ----
    // What this unit was doing when its group mustered, as the errand that puts it
    // back: an AssignWorker goal for a held work slot, an AssignBuilder goal for a
    // build, or the goal it was walking to. Re-issued through GoalRules on dismiss;
    // cleared then, on death, and when the player gives its held slot away.
    // Written only by GroupMuster and AssignWorkersIntent.
    public GoalPlan? SavedTask { get; set; }

    // ---- M5 group membership ----
    // The leaf group this unit belongs to (at most one). Set by Create/AddTo/
    // FormGroupIntent; cleared by Delete/DisbandGroupIntent. Membership alone
    // blocks nothing (M46): solo intents reject the unit only while its group
    // has it under command (GroupRules.UnderCommand), so a member of a
    // dismissed group works like anyone else. See Groups/Group.cs and
    // docs/groups-first-class.md.
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

    // ---- rest healing (docs/unit-healing.md) ----
    // The anchor of this unit's queued RestHealEvent: a wounded unit standing
    // on its own shelter heals once per completed RestConstants.PeriodTicks.
    // Null = dormant (not sheltered, or at full health). Written only by
    // Sim.Core.Healing.Rest (Schedule / Interrupt) and Snapshot restore.
    public long? NextRestHealTick { get; internal set; }
    public long? NextRestHealSeq { get; internal set; }

    // ---- M19 home (docs/m19-per-house-food-spec.md) ----
    // The tile of this unit's home HOUSE; null = homed at the owner's
    // castle (the default and the universal fallback). The home is the
    // unit's food DEMAND POINT — meals deduct from the home's stock
    // wherever the unit stands; nobody commutes to eat. Mutated ONLY via
    // Population.SetHome (which keeps House.ResidentCount in step —
    // the PopulationCount single-mutation discipline, applied here).
    public TileCoord? Home { get; internal set; }

    // ---- automation substrate (docs/automation-substrate.md) ----
    // SACRED: never selected by a CONSCRIPTING pull. Opt-in conscription
    // (an order allowed to pull units that are already working, when no
    // dormant unit fits) would otherwise happily cannibalize the very
    // crews that keep the kingdom alive — the food line's haulers, the
    // castle garrison. Marking them Protected is how the player says
    // "grow the town, but not out of THESE people."
    //
    // Only conscription honours it: an ordinary dormant-unit pull is free
    // to take a Protected unit that is genuinely idle. The flag guards
    // against being TAKEN FROM WORK, not against being useful.
    public bool Protected { get; set; }

    // M36 — the named haul route this unit crews, or null
    // (docs/hauling-queue-and-routes.md). A crew member belongs to its loop:
    // never in the haul-queue pool, never pulled by another order. Mutated
    // ONLY by AddRouteCrewIntent, RemoveRouteCrewIntent and
    // ClearHaulRouteIntent.
    public int? RouteId { get; internal set; }

    // ---- M41 battlefield grid (docs/battlefield-grid.md) ----
    // The unit's place on an open battlefield (or waiting to come on, or
    // sheltered), null at world scale. Written only by Battlefields.
    public Sim.Core.Battlefields.BoardSlot? Board { get; internal set; }

    // Standing battle doctrine (what it does on a board with no order); null
    // = the default for its role (BattleDoctrine.DefaultFor). Set only by
    // SetBattleDoctrineIntent. Survives between battles.
    public Sim.Core.Battlefields.BattleDoctrine? Doctrine { get; internal set; }

    // M42 (docs/subtile-movement.md) — the subtile of its world tile the unit
    // stands on, in a grid-combat world. Only enemies share one (a duel).
    // Null in a Pooled world, while aboard a boat, and for a unit with no room
    // on its tile. Written only by Battlefields.Placement.
    public Sim.Core.Battlefields.Subtile? Subtile { get; internal set; }

    // M42/M43 — THE WALK: the steps left of this unit's walk on the map's subtiles
    // (SubtileRoutes), null when it has none, and the (tick, Seq) anchor of its queued
    // SubtileRouteStepEvent (null while a battle has paused it). Set by a tile order
    // (the pathfinder's path), a drawn route (the player's squares), a chase or an errand.
    public List<Sim.Core.Battlefields.WorldSubtile>? SubtileRoute { get; internal set; }
    public long? SubtileRouteTick { get; internal set; }
    public long? SubtileRouteSeq { get; internal set; }

    // M43 (docs/fix-combat-m43.md) — the open battlefield this unit's next step is waiting to
    // enter: the board's tile had no room for it (a friend standing on the arrival subtile,
    // or its side at the cap), so the step is retried on the board's beats and the unit stays
    // on its own tile, off the board, until it fits or gives up. WaitingSince = the tick the
    // wait began. Set and cleared only by SubtileRoutes (Cancel clears it), so a walk that
    // ends any other way never leaves a stale wait.
    public TileCoord? WaitingToEnter { get; internal set; }
    public long WaitingSince { get; internal set; }

    // The tile this unit last walked in from, and when: a battle's arriving
    // units deploy on the edge facing it, and Withdraw goes back out that
    // way. Set on every hop arrival.
    public TileCoord? EnteredFrom { get; internal set; }
    public long EnteredTick { get; internal set; } = long.MinValue / 2;

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

    // M31 — the ONLY other writer of Role besides training, and deliberately a
    // separate method: crowning is not retraining. Called from Succession, which
    // is the single mutation point for the whole dynasty.
    internal void SetRoleForCrowning(UnitRole role) => _role = role;

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
