namespace Sim.Core.Engine;

public enum IntentOutcomeKind : byte
{
    Applied = 0,
    Rejected = 1,
}

// What happened when an intent (or any event) resolved. `Applied` = the
// resolution did its work; `Rejected(reason)` = preconditions failed at
// resolution time, world unchanged. See docs/intent-validation.md.
//
// The Reason string is for diagnostics / notifications. It is NOT part of
// the canonical snapshot — two runs that produce different rejection
// reasons can still have identical world state.
public sealed record IntentOutcome(IntentOutcomeKind Kind, string? Reason = null)
{
    public static readonly IntentOutcome Applied = new(IntentOutcomeKind.Applied);
    public static IntentOutcome Reject(string reason) => new(IntentOutcomeKind.Rejected, reason);

    public bool IsApplied => Kind == IntentOutcomeKind.Applied;
    public bool IsRejected => Kind == IntentOutcomeKind.Rejected;
}
