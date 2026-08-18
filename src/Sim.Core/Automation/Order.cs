using Sim.Core.World;

namespace Sim.Core.Automation;

// THE UNIVERSAL ORDER RECORD (docs/automation-substrate.md, Layer 1).
//
// Every automation in the game is this one type. Supply lines, breeding,
// training quotas, staffing, caravan reinforcement, patrols — all of them
// are configurations of the fields below, not new classes with new engine
// paths. That is the whole point: adding automation N+1 is a recipe ROW
// (RecipeKind + a driver case), never a new order model.
//
// PARAMETER-BAG CONVENTION (same as ActionSpec / ConditionSpec): unused
// fields stay at their defaults for a given Subject/Program/Recipe
// combination. Integers, enums, and TileCoords only — the determinism
// contract (architecture §3.1) forbids floats in serialized sim state.
//
// Sim.Core NEVER evaluates an order. Evaluation — triggers against the
// owner's fog-filtered view, recipes into ordinary intents — is the
// server-side driver's job (the M16/M17/M18 trust boundary, retained).

// Append-only enum (serialized). What the order is ABOUT — the thing whose
// level the order maintains.
public enum SubjectKind : byte
{
    Structure = 1, // a building: its stock, its worker count, its residents
    Group     = 2, // a caravan/warband: its crew composition
    RoleCount = 3, // the kingdom's headcount of one role (training quotas)
}

// Append-only enum (serialized). The two program SHAPES.
public enum ProgramKind : byte
{
    // The thermostat: while the trigger holds, drive the subject toward
    // Target by repeating Recipe. Level-triggered, not edge-triggered —
    // it re-fires whenever the level sags, forever.
    Maintain = 1,
    // A repeating stop-list: go here, do this, leave when that holds, next.
    // Trade circuits, patrols, scouting sweeps — the shapes where the ORDER
    // of visits is the point and no thermostat can express it.
    //
    // Routine is the one program that carries a durable CURSOR. Maintain
    // re-derives its stage by reading the world, but "which stop am I on"
    // genuinely isn't in the world: a caravan standing on a tile that
    // appears twice in its circuit is either inbound or outbound and the
    // map cannot say which. So the cursor is Core state, moved only by
    // OrderStatusIntent — the M18 lesson kept: cursors durable, brains
    // ephemeral, so a restart resumes a caravan mid-circuit.
    Routine  = 2,
}

// Append-only enum (serialized). What a caravan does when it arrives.
public enum RoutineAction : byte
{
    None   = 1, // just be here (patrol waypoint, scouting stop)
    Load   = 2, // LoadCargoIntent(unit, Resource)
    Unload = 3, // UnloadCargoIntent(unit)
}

// One stop on a circuit. `DepartWhen` is a conjunction evaluated at the
// stop: an empty list means leave as soon as the action is done, which is
// the ordinary patrol case. A stop with `StockAtLeast(here, Food, 25)`
// waits for a load worth carrying — the train-schedule primitive.
public sealed class RoutineStep
{
    public TileCoord Tile { get; init; }
    public RoutineAction Action { get; init; } = RoutineAction.None;
    public Resource Resource { get; init; }
    public List<Predicate> DepartWhen { get; init; } = new();
}

// Append-only enum (serialized). THE RECIPE CATALOG — the mechanical work
// a Maintain program performs when it fires. Each row is one dumb verb
// (or a fixed sequence with no branch), mapping onto intents the player
// already has. New automation = a new row here + one driver case.
public enum RecipeKind : byte
{
    Haul  = 1, // crew member hauls Resource from SourceTile → subject tile
    // Pull two fertile adults to the subject house and begin a breeding
    // cycle. The first STAGED recipe: claim → walk both → begin. It never
    // computes whether the kingdom can afford a mouth — the trigger reads
    // the HOUSE's own larder, which a supply line keeps stocked, so the
    // breeding rate self-throttles to what logistics physically delivers.
    Breed = 2,
    // Pull an untrained adult to the School named by SourceTile and train
    // them into SubjectRole. The kingdom's "make more of this profession"
    // thermostat — quota in, workers out.
    Train = 3,
    // Pull a matching worker to the subject extractor and assign them.
    //
    // Staff does NOT train a replacement when the right role is scarce —
    // it reports the shortage and waits. The fix is a Train order, and the
    // two COMPOSE THROUGH THE POOL exactly as law 3 wants: Train makes
    // farmers, Staff puts farmers on farms, neither knows the other exists.
    // Baking a train-fallback inside Staff would have made one recipe do
    // two jobs and hidden the player's workforce design inside it.
    Staff = 4,
    // Reserved: Reinforce = 5 — BLOCKED on a sim verb. Adding a unit to an
    // existing Group has no intent (only Form / Move / Disband), and
    // automation never gets a verb the player lacks. It needs its own
    // milestone first; see docs/automation-substrate.md Layer 2.
}

// Append-only enum (serialized). Where an order's hands come from.
public enum CrewMode : byte
{
    // Named units, claimed at Set time and held until Clear. The caravan
    // model — legible, escortable, and killable (the war layer needs a
    // PERSISTENT crew to guard). docs/automation-layers.md "why claimed".
    Named = 1,
    // Drawn from the dormant pool per firing via Selector. Safe interior
    // work where the player cares about the goal, not the identity.
    Pull  = 2,
}

// Append-only enum (serialized). Dumb threshold predicates over facts the
// owner can see. Every one is a pure read with an obvious answer — no
// searching, no ranking, no weighing (the zero-judgment law).
public enum PredicateKind : byte
{
    Always        = 1,
    StockBelow    = 2, // stock(Tile, Resource) <  Threshold
    StockAtLeast  = 3, // stock(Tile, Resource) >= Threshold
    RoleCountBelow   = 4, // owner's count of Role (+ in-flight) <  Threshold
    RoleCountAtLeast = 5, // owner's count of Role (+ in-flight) >= Threshold
    // Whole-population gates — the "keep population above N" breeding
    // trigger. Omit the predicate entirely (an Always trigger) and the
    // house breeds continuously: growth then throttles ONLY on the
    // physical food in its larder, which is the "skyrocket" setting.
    PopulationBelow   = 6,
    PopulationAtLeast = 7,
    // Assigned-worker count at the extractor on Tile. The staffing
    // thermostat's own reading — "this farm is short-handed".
    WorkersBelow   = 8,
    WorkersAtLeast = 9,
}

// One dumb boolean. Unused fields default per Kind.
//
// CountInFlight (RoleCount* only): when true the count INCLUDES units other
// orders have already committed to producing (ClaimPurpose.InFlight). This
// is what stops two schools racing on one quota — the second reads the
// target as already covered and stands down. docs/automation-substrate.md.
public readonly record struct Predicate(
    PredicateKind Kind,
    TileCoord Tile,
    Resource Resource,
    UnitRole Role,
    long Threshold,
    bool CountInFlight)
{
    public static Predicate Always() =>
        new(PredicateKind.Always, default, Resource.None, UnitRole.None, 0, false);

    public static Predicate StockBelow(TileCoord tile, Resource r, long threshold) =>
        new(PredicateKind.StockBelow, tile, r, UnitRole.None, threshold, false);

    public static Predicate StockAtLeast(TileCoord tile, Resource r, long threshold) =>
        new(PredicateKind.StockAtLeast, tile, r, UnitRole.None, threshold, false);

    public static Predicate RoleCountBelow(UnitRole role, long threshold, bool countInFlight = true) =>
        new(PredicateKind.RoleCountBelow, default, Resource.None, role, threshold, countInFlight);

    public static Predicate RoleCountAtLeast(UnitRole role, long threshold, bool countInFlight = true) =>
        new(PredicateKind.RoleCountAtLeast, default, Resource.None, role, threshold, countInFlight);

    public static Predicate PopulationBelow(long threshold) =>
        new(PredicateKind.PopulationBelow, default, Resource.None, UnitRole.None, threshold, false);

    public static Predicate PopulationAtLeast(long threshold) =>
        new(PredicateKind.PopulationAtLeast, default, Resource.None, UnitRole.None, threshold, false);

    public static Predicate WorkersBelow(TileCoord extractor, long threshold) =>
        new(PredicateKind.WorkersBelow, extractor, Resource.None, UnitRole.None, threshold, false);

    public static Predicate WorkersAtLeast(TileCoord extractor, long threshold) =>
        new(PredicateKind.WorkersAtLeast, extractor, Resource.None, UnitRole.None, threshold, false);
}

// A conjunction — every predicate must hold.
// Collections here are `init`-settable, not get-only: SetOrderIntent is a
// DURABLE intent, and System.Text.Json cannot populate a get-only
// collection property — a get-only list round-trips through the intent log
// as EMPTY, silently turning a supply line into a no-op after recovery.
public sealed class TriggerClause
{
    public List<Predicate> All { get; init; } = new();
}

// THE TRIGGER: disjunctive normal form (OR of ANDs). Any clause true → the
// order may fire. An EMPTY trigger means always-fire.
//
// Why DNF and not a general tree: it is fully general for boolean logic,
// it serializes as two flat loops (no recursive node table, no cycle
// risk), and it is the shape a UI can render as "any of these situations:
// [all of these conditions]". Nesting depth is not expressive power here —
// it is just rope.
public sealed class Trigger
{
    public List<TriggerClause> Any { get; init; } = new();

    public static Trigger Always() => new();

    public static Trigger When(params Predicate[] all)
    {
        var t = new Trigger();
        var clause = new TriggerClause();
        clause.All.AddRange(all);
        t.Any.Add(clause);
        return t;
    }
}

// THE SELECTOR — the only place an order answers "who?".
//
// A WHERE CLAUSE, NEVER A RANKING (the mechanical enforcement of the
// zero-judgment law, docs/automation-as-core-game.md). The filter says
// which units are ELIGIBLE; the canonical order (distance from Anchor,
// then y, then x, then id) makes the choice a total order with no scoring
// function anywhere. "Nearest eligible" is a tiebreak rule, not a
// preference — swap the anchor and the same rule picks a different unit
// with no other change in behavior.
//
// RequireDormant is on by default: an order takes units nobody else is
// responsible for. Conscription (taking units already at work) is the
// opt-in escape hatch on the Order, and it never takes Protected units.
// MaxAgeYears (0 = no ceiling) makes the age filter a WINDOW, not just a
// floor — fertility is 18..45, so breeding could not be expressed without
// it. Trainability, by contrast, is floor-only.
public readonly record struct Selector(
    UnitRole Role,
    bool AnyRole,
    int MinAgeYears,
    int MaxAgeYears,
    bool RequireDormant,
    TileCoord Anchor,
    int Radius)
{
    // Units of a role within reach of an anchor — the staffing/crew shape.
    public static Selector OfRole(UnitRole role, TileCoord anchor, int radius, int minAgeYears = 0) =>
        new(role, AnyRole: false, minAgeYears, MaxAgeYears: 0, RequireDormant: true, anchor, radius);

    // Any role — the "no-role trainee" and "any warm body" shapes.
    public static Selector Anyone(TileCoord anchor, int radius, int minAgeYears = 0) =>
        new(UnitRole.None, AnyRole: true, minAgeYears, MaxAgeYears: 0, RequireDormant: true, anchor, radius);

    // Anyone inside an age WINDOW — the breeding-pair shape. Role-agnostic
    // on purpose: a farmer and a miner make the same baby.
    public static Selector InAgeWindow(TileCoord anchor, int radius, int minAgeYears, int maxAgeYears) =>
        new(UnitRole.None, AnyRole: true, minAgeYears, maxAgeYears, RequireDormant: true, anchor, radius);
}

// One player automation. Definition fields are init-only; the durable
// STATUS block at the bottom is mutated only by OrderStatusIntent (the
// server-internal, durable path — M18's cursor precedent).
public sealed class Order
{
    public int OrderId { get; init; }
    public int OwnerId { get; init; }

    // Evaluation rank AND triage rank. Lower fires first, so a lower number
    // wins contention for a scarce dormant unit. It is also the suspension
    // order when capacity shrinks (the async doctrine: the player's own
    // ranking decides what sleeps first, so food lines outlive luxuries).
    public int Priority { get; init; }

    // ---- subject ----
    public SubjectKind SubjectKind { get; init; }
    public TileCoord SubjectTile { get; init; }   // Structure subjects
    public UnitRole SubjectRole { get; init; }    // RoleCount subjects
    public int SubjectGroupId { get; init; }      // Group subjects

    // ---- trigger ----
    public Trigger Trigger { get; init; } = Trigger.Always();

    // ---- program ----
    public ProgramKind Program { get; init; } = ProgramKind.Maintain;
    public RecipeKind Recipe { get; init; }
    // The setpoint the recipe drives toward. Read per-recipe (Haul: the
    // stock level to restore; Train: the headcount; Staff: worker slots).
    public long Target { get; init; }
    // Recipe parameters (parameter-bag: unused stay default).
    public TileCoord SourceTile { get; init; }
    public Resource Resource { get; init; }

    // Routine only — the circuit, walked in order and wrapped at the end.
    public List<RoutineStep> Steps { get; init; } = new();

    // ---- crew ----
    public CrewMode CrewMode { get; init; } = CrewMode.Named;
    // Ascending, distinct (normalized at Set). Named crews only.
    public List<int> NamedCrew { get; init; } = new();
    // Pull crews only — how the pool is filtered each firing.
    public Selector Selector { get; init; }

    // ---- posture (M29 patrols, Routine only) ----
    //
    // Two knobs turn a circuit into a PATROL (docs/patrols.md). 0/0 is the
    // pacifist circuit that already existed, so this is not a new program.
    //
    // EngageRadius — hostiles within this many tiles OF THE PARTY are chased.
    //   Party-LOCAL on purpose: "attack anything sighted in my kingdom" would
    //   be a police dispatcher (goal-AI) and would delete the spatial game.
    //   Where you walk is what you protect.
    // LeashRadius  — how far from the ROUTE the chase may be drawn. Measured
    //   from the nearest stop, never from where the chase began, so a target
    //   that hovers at the boundary cannot drag the party across the map in
    //   leash-sized increments. 0 = no leash (chase while visible).
    public int EngageRadius { get; init; }
    public int LeashRadius { get; init; }

    // ---- flags ----
    // Allow taking units that are already working when no dormant unit
    // fits. OFF by default: growth that cannibalizes the workforce feeding
    // it is a famine with extra steps. Never takes Unit.Protected.
    public bool ConscriptOptIn { get; init; }

    // ---- durable status (mutated ONLY by OrderStatusIntent) ----

    // False = auto-disabled (retry budget exhausted) or player-suspended.
    public bool Enabled { get; set; } = true;
    // Consecutive fruitless firings. The bounded-retry rule (the M16
    // "wedge forever" fix) disables the order when it exhausts the budget.
    public int RetryCount { get; set; }
    // Last tick this order actually dispatched work. Basis for cooldowns
    // and for the morning report's "when did this last run".
    public long LastFiredTick { get; set; } = long.MinValue;

    // Routine only — index into Steps of the stop being served. Durable so
    // a restart resumes the circuit instead of sending everyone back to the
    // first stop.
    public int CurrentStep { get; set; }

    // Deep copy of the definition (status reset) — used by Set to normalize
    // and by tests. Claims are NOT copied: they belong to the live ledger.
    // The owner is passed in, never taken from the payload: the SUBMITTER
    // owns the order, so a spoofed OwnerId in a wire payload is inert.
    public Order CloneDefinition(int orderId, int ownerId) => new()
    {
        OrderId = orderId,
        OwnerId = ownerId,
        Priority = Priority,
        SubjectKind = SubjectKind,
        SubjectTile = SubjectTile,
        SubjectRole = SubjectRole,
        SubjectGroupId = SubjectGroupId,
        Trigger = Trigger,
        Program = Program,
        Recipe = Recipe,
        Target = Target,
        SourceTile = SourceTile,
        Resource = Resource,
        CrewMode = CrewMode,
        Selector = Selector,
        ConscriptOptIn = ConscriptOptIn,
        EngageRadius = EngageRadius,
        LeashRadius = LeashRadius,
        Steps = Steps,
    };
}
