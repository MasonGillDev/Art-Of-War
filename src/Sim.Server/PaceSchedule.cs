namespace Sim.Server;

// TWO-ACT PACING (docs/two-act-pacing.md): how fast the host feeds ticks to the
// sim, as a function of WHERE the sim is — not of wall-clock time.
//
// Until the landing tick the world runs at the prelude pace; from the landing
// on, at the (slower) pace of the rest of the game. A world with no landing
// (LandingTick 0) runs at TicksPerSecond throughout.
//
// Keyed to the TICK on purpose. A pause, a stalled loop or a slow start can't
// move the drop, and every consumer (the clock loop, the client's countdown,
// the tests) computes the same real-time mapping from the same three numbers.
// The sim still never reads any of this: it knows only when the landing is
// (Sim.Core.Landing.LandingConfig), and it is the host that changes pace there.
public sealed record PaceSchedule(double PreludeTicksPerSecond, double TicksPerSecond, long LandingTick)
{
    public bool HasLanding => LandingTick > 0;

    // The scheduled pace at `tick`. The landing tick itself already runs at
    // the slow pace: the second act begins ON day X.
    public double TicksPerSecondAt(double tick) =>
        HasLanding && tick < LandingTick ? PreludeTicksPerSecond : TicksPerSecond;

    // Where the clock stands after `seconds` of real time, starting at
    // `fromTick`. A span that crosses the landing runs at the prelude pace up
    // to the landing tick and at the slow pace for the rest, so the drop
    // happens at exactly the landing tick however coarse the host's loop is.
    public double Advance(double fromTick, double seconds)
    {
        if (seconds <= 0) return fromTick;
        if (!HasLanding || fromTick >= LandingTick)
            return fromTick + seconds * TicksPerSecond;

        var toLanding = (LandingTick - fromTick) / PreludeTicksPerSecond;
        return seconds <= toLanding
            ? fromTick + seconds * PreludeTicksPerSecond
            : LandingTick + (seconds - toLanding) * TicksPerSecond;
    }

    // Real seconds the schedule takes to go from `fromTick` to `toTick`.
    // What a countdown shows: the client does the same sum from the same
    // numbers (WorldDto.Pace).
    public double SecondsBetween(double fromTick, double toTick)
    {
        if (toTick <= fromTick) return 0;
        if (!HasLanding || fromTick >= LandingTick)
            return (toTick - fromTick) / TicksPerSecond;
        if (toTick <= LandingTick)
            return (toTick - fromTick) / PreludeTicksPerSecond;
        return (LandingTick - fromTick) / PreludeTicksPerSecond
             + (toTick - LandingTick) / TicksPerSecond;
    }
}
