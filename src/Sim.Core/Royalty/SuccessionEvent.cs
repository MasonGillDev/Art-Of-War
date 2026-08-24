using Sim.Core.World;

namespace Sim.Core.Royalty;

// M31 — the announcement. Same shape and the same argument as M30's
// GoalDissolvedEvent: applying nothing is the point, because Succession has
// already done the state work and this exists to be SEEN.
//
// An event rather than a side-list because that is what makes it deterministic
// and replay-safe — it lands in ResolvedLog in sequence, a twin run produces
// the identical line, and the projector (and later the chronicler) reads it
// from the same place it reads everything else. "The king fell at the eastern
// ford; his son is crowned" is this row, rendered.
//
// NewKingUnitId null = the line is extinct: the realm persists, buffless,
// until a new dynasty is founded (deferred; see docs/king-and-dynasty.md).
public sealed class SuccessionEvent : ScheduledEvent
{
    public int OwnerId { get; }
    public int FormerKingUnitId { get; }
    public int? NewKingUnitId { get; }

    // A child monarch: crowned, but projecting nothing until majority. Carried
    // on the event so the announcement can say so at the moment it happens
    // rather than making every reader recompute an age.
    public bool IsMinority { get; }

    public SuccessionEvent(int ownerId, int formerKingUnitId, int? newKingUnitId, bool isMinority)
    {
        OwnerId = ownerId;
        FormerKingUnitId = formerKingUnitId;
        NewKingUnitId = newKingUnitId;
        IsMinority = isMinority;
    }

    public override void Apply(Simulation sim)
    {
        if (NewKingUnitId is null)
            Outcome = IntentOutcome.Reject($"the line of player {OwnerId} is extinct");
    }

    public override string Describe() => NewKingUnitId is { } heir
        ? $"Succession(player={OwnerId}: {FormerKingUnitId} -> {heir}{(IsMinority ? ", minority" : "")})"
        : $"Succession(player={OwnerId}: {FormerKingUnitId} -> none, line extinct)";
}
