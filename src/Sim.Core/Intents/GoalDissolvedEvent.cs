using Sim.Core.World;

namespace Sim.Core.Intents;

// M30 — the announcement half of the visibility contract
// (docs/goal-shaped-intents.md §"The visibility contract").
//
// Goal-shaped intents move work off the player's memory and into the sim. If
// the sim then drops a goal silently, appointment-anxiety has merely been
// traded for silent-failure anxiety — so every dissolution says so out loud.
//
// It is an EVENT rather than a side-list because that is what makes the
// announcement deterministic and replay-safe: it lands in ResolvedLog in
// sequence, a twin run produces the identical line, and the projector (and
// later the chronicler) reads it from the same place it reads everything else.
// Applying nothing is the point — GoalRules.Dissolve has already done the
// state work; this event exists to be seen.
public sealed class GoalDissolvedEvent : ScheduledEvent
{
    public int UnitId { get; }
    public GoalKind Kind { get; }
    public TileCoord TargetTile { get; }
    public string Reason { get; }

    public GoalDissolvedEvent(int unitId, GoalKind kind, TileCoord targetTile, string reason)
    {
        UnitId = unitId;
        Kind = kind;
        TargetTile = targetTile;
        Reason = reason;
    }

    public override void Apply(Simulation sim)
    {
        Outcome = IntentOutcome.Reject(Reason);
    }

    public override string Describe() =>
        $"GoalDissolved(unit={UnitId} {Kind} @ {TargetTile.X},{TargetTile.Y}: {Reason})";
}
