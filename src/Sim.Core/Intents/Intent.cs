namespace Sim.Core.Intents;

// An Intent is a player-issued command. Recorded verbatim into the intent log
// so the same sequence can be re-fed to a fresh sim for replay.
public abstract class Intent
{
    // Returns Applied on success; Reject(reason) when preconditions fail at
    // resolution time. Resolve MUST NOT mutate state when it rejects.
    public abstract IntentOutcome Resolve(Simulation sim);
    public virtual string Describe() => GetType().Name;
}
