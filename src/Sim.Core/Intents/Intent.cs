namespace Sim.Core.Intents;

// An Intent is a player-issued command. Recorded verbatim into the intent log
// so the same sequence can be re-fed to a fresh sim for replay.
public abstract class Intent
{
    public abstract void Resolve(Simulation sim);
    public virtual string Describe() => GetType().Name;
}
