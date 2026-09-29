namespace Sim.Core.Landing;

// Day X (docs/two-act-pacing.md): the war bands come out of the fog. Scheduled
// once, at genesis, for LandingConfig.Tick.
//
// ANCHOR: GameWorld.LandingSeq is this event's Seq while it is pending, cleared
// when it fires; RegenerateQueue reschedules it from there. Fenced on that Seq.
public sealed class LandingEvent : ScheduledEvent
{
    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (world.LandingSeq != Seq || world.LandingConfig.Tick != At)
        {
            Outcome = IntentOutcome.Reject($"stale landing (seq {Seq})");
            return;
        }
        world.LandingSeq = null;
        LandingRules.Land(sim);
    }

    public override string Describe() => $"Landing(tick={At})";
}
