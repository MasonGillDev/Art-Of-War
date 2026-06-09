namespace Sim.Core.Intents;

// An Intent is a player-issued command. Recorded verbatim into the intent log
// so the same sequence can be re-fed to a fresh sim for replay.
public abstract class Intent
{
    // Player who issued this intent. Defaults to 0 for single-player
    // scenarios. Carried through to OwnerId on any structures built by
    // this intent (PlaceSiteIntent), and consumed by every Resolve as
    // the authorization check against the target's OwnerId — see
    // docs/intent-authorization.md.
    public int PlayerId { get; init; } = 0;

    // Returns Applied on success; Reject(reason) when preconditions fail at
    // resolution time. Resolve MUST NOT mutate state when it rejects.
    public abstract IntentOutcome Resolve(Simulation sim);
    public virtual string Describe() => GetType().Name;
}
