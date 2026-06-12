namespace Sim.Core.Automation;

// Append-only enum (serialized). Template tag only — the engine sequences
// Steps and never dispatches on Kind. It exists for the client UI and for
// the future per-branch unlock gate (OrderKind → required StructureKind,
// docs/automation-layers.md).
public enum OrderKind : byte
{
    SupplyLine         = 1,
    Route              = 2,
    StandingProduction = 3,
}

// Append-only enum (serialized).
public enum LoopMode : byte
{
    Once = 1, // order disables itself after the last step completes
    Loop = 2, // wraps back to step 0
}

// One step of a standing order: wait until ALL Conditions hold (empty =
// always), then perform Action once, then advance. Immutable after Set —
// editing an order is Clear + Set (a future in-place edit intent is
// deliberately deferred).
public sealed class OrderStep
{
    public List<ConditionSpec> Conditions { get; init; } = new();
    public ActionSpec Action { get; init; }
}

// M18 — a player's durable automation program (docs/automation-layers.md +
// docs/m18-automation-engine-spec.md). Lives in GameWorld.StandingOrders,
// snapshot-serialized, surfaced (owner-only) through the player view.
//
// MUTATION CONTRACT (docs/determinism-audit.md):
//   * Created/removed ONLY by SetStandingOrderIntent / ClearStandingOrderIntent.
//   * The cursor block below is mutated ONLY by AdvanceOrderCursorIntent —
//     a server-internal intent the AutomationDriver submits, so cursor moves
//     are durable in the intent log and crash recovery resumes a mid-route
//     order exactly. Identity/definition fields are init-only.
//
// Sim.Core never evaluates an order. Evaluation (conditions against the
// owner's fog-filtered view, actions into ordinary intents) is the
// server-side driver's job — out-of-sim, ephemeral, replay-proven by the
// M16 BanditDriver pattern.
public sealed class StandingOrder
{
    public int OrderId { get; init; }
    public int OwnerId { get; init; }
    public OrderKind Kind { get; init; }
    public LoopMode Loop { get; init; }

    // Ascending, distinct (normalized at Set). A unit may be claimed by at
    // most one order. Claims don't lock the unit — manual intents always
    // win; the driver only acts on a claimed unit that is idle by anchors.
    public List<int> ClaimedUnits { get; } = new();

    public List<OrderStep> Steps { get; } = new();

    // ---- cursor (mutated only via AdvanceOrderCursorIntent) ----

    // False = auto-disabled (bounded-retry exhausted) or completed (Once).
    // Cleared by the cursor intent; re-enabling is Clear + Set.
    public bool Enabled { get; set; } = true;

    // Index into Steps of the step currently waiting/executing.
    public int CurrentStep { get; set; }

    // Tick at which CurrentStep became current. Basis for ElapsedTicks
    // conditions; anchored at Set time and at every cursor advance.
    public long StepEnteredTick { get; set; }

    // Consecutive no-progress thinks on the current step. The driver
    // auto-disables the order when this exhausts its retry budget — the
    // structural fix for M16's "wedge forever" pitfall.
    public int StepRetryCount { get; set; }

    // Dispatch fence: true once the step's action intent has been submitted
    // and the driver is waiting for its effect to land. Prevents the driver
    // re-submitting the same action every think.
    public bool ActionDispatched { get; set; }
}
