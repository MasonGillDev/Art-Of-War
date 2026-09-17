namespace Sim.Server.Atmosphere;

// THE WORLD'S TIME OF DAY — one mapping, two consumers (docs/atmosphere-rig.md).
//
// The client's sky and the server's narration (scout reports now, the chronicler
// later) must agree about when things happen. If a chronicle says "they struck at
// dawn" while the screen showed dusk, the world contradicts its own historian. So
// there is exactly ONE function from tick to time of day, it lives on the server,
// and the client receives its parameters plus the server's own evaluation of it.
//
// COUNTED IN TICKS, NEVER IN WALL-CLOCK TIME. Pausing freezes the sun and a lab
// fast-forward speeds it, so the sky stays locked to the world whatever the pace
// dial says. A cycle measured in real minutes would detach the two the first time
// anyone pressed pause.
//
// PRESENTATION-OWNED TODAY. Nothing in the sim reads this. The day night becomes a
// mechanic, this mapping becomes sim-authoritative and moves to Sim.Core — with no
// grandfathering, because a mechanic and the sky disagreeing about whether it is
// night would be the same contradiction in a worse place.
//
// Phase convention: 0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset.

/// <summary>How long a day of light lasts, and where tick 0 falls in it.</summary>
public readonly record struct LightCycleConfig(long TicksPerCycle, long PhaseOffsetTicks)
{
    // One Time.Week of ticks per cycle: about 42 real minutes at the intended 4 tps
    // (a sim Time.Day is only 6, which would put a sunset on screen every few
    // minutes). The offset opens the world at about 08:00 — a world that starts in
    // darkness at its fog frontier is the wrong first impression.
    public LightCycleConfig() : this(Sim.Core.Time.Week, Sim.Core.Time.Week / 3) { }

    /// A cycle of the given length that still opens in the morning.
    public static LightCycleConfig ForCycle(long ticksPerCycle) =>
        new(ticksPerCycle, ticksPerCycle / 3);
}

public static class WorldClock
{
    public const double Midnight = 0.0;
    public const double Sunrise = 0.25;
    public const double Noon = 0.5;
    public const double Sunset = 0.75;

    /// The time of day at `tick`, in [0, 1).
    ///
    /// Integer arithmetic all the way to the final division, so the client and every
    /// server-side consumer compute the identical double for the same tick.
    public static double Phase(long tick, LightCycleConfig config)
    {
        var c = Normalize(config);
        var t = (tick + c.PhaseOffsetTicks) % c.TicksPerCycle;
        if (t < 0) t += c.TicksPerCycle;
        return t / (double)c.TicksPerCycle;
    }

    /// An unset config (all zeros — `default`, not `new()`) becomes the default
    /// cycle, and an offset outside the cycle is folded back into it.
    ///
    /// Without this a zero cycle would freeze every consumer at midnight forever,
    /// silently — the kind of failure that looks like a tuning choice rather than a
    /// bug.
    public static LightCycleConfig Normalize(LightCycleConfig config)
    {
        if (config.TicksPerCycle <= 0) return new LightCycleConfig();
        var offset = config.PhaseOffsetTicks % config.TicksPerCycle;
        if (offset < 0) offset += config.TicksPerCycle;
        return config with { PhaseOffsetTicks = offset };
    }
}
