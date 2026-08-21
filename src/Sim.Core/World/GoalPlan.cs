namespace Sim.Core.World;

// Append-only enum (serialized into snapshots).
public enum GoalKind : byte
{
    AssignWorker  = 1,
    AssignBuilder = 2,
    Breed         = 3,
}

// M30 — the on-unit GOAL anchor (docs/goal-shaped-intents.md).
//
// Third instance of the shape HaulPlan established and Pursuit confirmed:
// durable state on the unit that says "this body is on its way to do a
// specific thing", from which MoveArrivalEvent derives what happens next.
// The two reasons it lives on the unit rather than in the issuing intent are
// the same two as always:
//
//   1. RECOVERY. A goal in flight at snapshot time must survive a restart,
//      and RegenerateQueue rebuilds the queue from anchors alone — there is
//      no OnFinalArrival payload to serialize.
//   2. REPLAY. One intent in the log is the whole compound action; travel,
//      the wait, and the completion all unfold from this anchor.
//
// THE SIM NEVER PICKS PARTICIPANTS. Every id in here was named by the player
// (or by the client's convenience sugar, which binds concrete ids before the
// intent reaches the wire). Substituting a dead participant is the automation
// tier's auto-replacement feature and is deliberately absent here.
//
// Mutually exclusive with HaulPlan and Pursuit in practice: goal intents bind
// Idle units, and the intents that create those anchors reject non-Idle ones.
public sealed class GoalPlan
{
    public GoalKind Kind { get; init; }

    // The structure this goal is about: the extractor to be worked, the site
    // to be built, the house to breed in.
    public TileCoord TargetTile { get; init; }

    // Breed only: the other parent. 0 for every other kind. The pair is
    // symmetric — each parent carries a GoalPlan naming the other.
    public int PartnerUnitId { get; init; }

    public GoalPlan(GoalKind kind, TileCoord targetTile, int partnerUnitId = 0)
    {
        Kind = kind;
        TargetTile = targetTile;
        PartnerUnitId = partnerUnitId;
    }
}
