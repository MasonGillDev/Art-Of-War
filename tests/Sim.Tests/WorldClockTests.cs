using Sim.Server.Atmosphere;

namespace Sim.Tests;

// The world's time of day (docs/atmosphere-rig.md). One function, read by the
// client's sky and by server-side narration, so these tests pin the function
// itself rather than any one consumer of it.
public class WorldClockTests
{
    [Fact]
    public void TheDefaultCycleIsOneWeek_AndTheWorldOpensInTheMorning()
    {
        var cfg = new LightCycleConfig();

        Assert.Equal(Sim.Core.Time.Week, cfg.TicksPerCycle);

        // Tick 0 sits a third of the way through the cycle: after sunrise (0.25),
        // before noon (0.5). A world that opens at midnight opens in the dark.
        var opening = WorldClock.Phase(0, cfg);
        Assert.InRange(opening, WorldClock.Sunrise, WorldClock.Noon);
    }

    [Fact]
    public void PhaseStaysInRange_AndRepeatsEveryCycle()
    {
        var cfg = new LightCycleConfig();
        var ticks = new long[] { 0, 1, 7, 1439, 1440, 5000, 10079, 10080, 123_456, 9_999_999 };

        foreach (var t in ticks)
        {
            var p = WorldClock.Phase(t, cfg);
            Assert.InRange(p, 0.0, 0.999_999_999);
            Assert.Equal(p, WorldClock.Phase(t + cfg.TicksPerCycle, cfg));
        }
    }

    [Fact]
    public void TheNamedMarkersFallWhereTheirNamesSay()
    {
        // With no offset, a quarter of the cycle is sunrise and so on — the phase
        // convention every consumer of this clock is written against.
        var cfg = new LightCycleConfig(TicksPerCycle: 4000, PhaseOffsetTicks: 0);

        Assert.Equal(WorldClock.Midnight, WorldClock.Phase(0, cfg));
        Assert.Equal(WorldClock.Sunrise, WorldClock.Phase(1000, cfg));
        Assert.Equal(WorldClock.Noon, WorldClock.Phase(2000, cfg));
        Assert.Equal(WorldClock.Sunset, WorldClock.Phase(3000, cfg));
    }

    [Fact]
    public void PhaseAdvancesSteadilyWithinACycle()
    {
        var cfg = new LightCycleConfig(TicksPerCycle: 1000, PhaseOffsetTicks: 0);
        var previous = -1.0;
        for (long t = 0; t < 1000; t += 37)
        {
            var p = WorldClock.Phase(t, cfg);
            Assert.True(p > previous, $"phase went backwards at tick {t}");
            previous = p;
        }
    }

    [Fact]
    public void AnUnsetConfigIsNeverAFrozenMidnight()
    {
        // default(LightCycleConfig) is all zeros — NOT the same as new(). Unguarded,
        // a zero cycle would pin every consumer to midnight forever and it would look
        // like a lighting choice rather than a bug.
        var unset = default(LightCycleConfig);

        Assert.Equal(new LightCycleConfig(), WorldClock.Normalize(unset));
        Assert.NotEqual(WorldClock.Phase(0, unset), WorldClock.Phase(Sim.Core.Time.Day, unset));
    }

    [Fact]
    public void ACustomCycleStillOpensInTheMorning()
    {
        var cfg = LightCycleConfig.ForCycle(Sim.Core.Time.Day);

        Assert.Equal(Sim.Core.Time.Day, cfg.TicksPerCycle);
        Assert.InRange(WorldClock.Phase(0, cfg), WorldClock.Sunrise, WorldClock.Noon);
    }
}
