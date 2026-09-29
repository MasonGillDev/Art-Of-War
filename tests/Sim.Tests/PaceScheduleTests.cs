using System.Text.Json;
using Sim.Core;
using Sim.Server;

namespace Sim.Tests;

// Two-act pacing (docs/two-act-pacing.md): the HOST's side. The pace is fast
// until the world's landing tick and slow after it, keyed to the tick so that
// nothing (a pause, a slow loop, a replay) can move the drop.
public class PaceScheduleTests
{
    private static readonly PaceSchedule TwoActs = new(PreludeTicksPerSecond: 4, TicksPerSecond: 1, LandingTick: 100);

    [Fact]
    public void ThePace_DropsOnTheLandingTick()
    {
        Assert.Equal(4, TwoActs.TicksPerSecondAt(0));
        Assert.Equal(4, TwoActs.TicksPerSecondAt(99.9));
        Assert.Equal(1, TwoActs.TicksPerSecondAt(100));
        Assert.Equal(1, TwoActs.TicksPerSecondAt(1_000_000));
    }

    [Fact]
    public void WithoutALanding_OnePaceThroughout()
    {
        var oneAct = new PaceSchedule(PreludeTicksPerSecond: 4, TicksPerSecond: 1, LandingTick: 0);
        Assert.False(oneAct.HasLanding);
        Assert.Equal(1, oneAct.TicksPerSecondAt(0));
        Assert.Equal(11, oneAct.Advance(1, 10));
    }

    [Fact]
    public void Advance_SplitsASpanThatCrossesTheLanding()
    {
        Assert.Equal(20, TwoActs.Advance(0, 5));        // all prelude: 5 s x 4
        // 10 ticks to the landing take 2.5 s at 4 tps; the other 2.5 s run at 1 tps.
        Assert.Equal(102.5, TwoActs.Advance(90, 5));
        Assert.Equal(105, TwoActs.Advance(100, 5));     // all after
        Assert.Equal(90, TwoActs.Advance(90, 0));       // no time, no ticks
    }

    // However coarse the host's loop, the clock arrives at the same tick: one long
    // span and many short ones agree.
    [Fact]
    public void Advance_DoesNotDependOnStepSize()
    {
        var once = TwoActs.Advance(0, 60);
        var many = 0.0;
        for (var i = 0; i < 600; i++) many = TwoActs.Advance(many, 0.1);
        Assert.Equal(once, many, precision: 6);
    }

    [Fact]
    public void SecondsBetween_IsTheInverseOfAdvance()
    {
        foreach (var (from, to) in new[] { (0.0, 50.0), (0.0, 100.0), (40.0, 250.0), (120.0, 400.0) })
            Assert.Equal(to, TwoActs.Advance(from, TwoActs.SecondsBetween(from, to)), precision: 9);
        // A day-30 landing at 4 tps comes three real hours in.
        var host = new PaceSchedule(4, 1, 30L * Time.Day);
        Assert.Equal(3 * 3600, host.SecondsBetween(0, host.LandingTick), precision: 6);
    }

    [Fact]
    public void TheHost_LandsOnDay30ByDefault_ButALabsWorldHasOneAct()
    {
        var host = ServerOptions.Parse(Array.Empty<string>());
        Assert.Equal(30, host.LandingDay);
        Assert.Equal(4.0, host.PreludeTicksPerSecond);
        Assert.Equal(1.0, host.TicksPerSecond);

        var flags = ServerOptions.Parse(new[] { "--landing-day", "20", "--prelude-tps", "8", "--tps", "0.5" });
        Assert.Equal(20, flags.LandingDay);
        Assert.Equal(8.0, flags.PreludeTicksPerSecond);
        Assert.Equal(0.5, flags.TicksPerSecond);

        // A bare ServerOptions is what the AI labs build: no landing, no truce, and
        // no change to their hashes.
        Assert.Equal(0, new ServerOptions().LandingDay);
    }

    [Fact]
    public void TheWorldFactory_PutsTheLandingInGenesis()
    {
        var landed = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 0, LandingDay = 30 });
        Assert.Equal(30L * Time.Day, landed.Spec.Landing.Tick);

        var oneAct = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 0 });
        Assert.False(oneAct.Spec.Landing.Enabled);
    }

    private static GameHost MakeHost(int landingDay)
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 0, LandingDay = landingDay });
        return new GameHost(build, seed: 0xBEEF, ticksPerSecond: 1.0) { PreludeTicksPerSecond = 4.0 };
    }

    [Fact]
    public void TheHost_RunsThePreludePace_UntilTheLanding()
    {
        using var host = MakeHost(landingDay: 30);

        Assert.Equal(new PaceSchedule(4.0, 1.0, 30L * Time.Day), host.Schedule);
        Assert.Equal((false, 4.0), host.GetPace());   // tick 0: the prelude
    }

    // The pace endpoint survives as a dev tool: a rate HOLDS an override, and
    // clearing it (no rate) hands the pace back to the schedule.
    [Fact]
    public void ADevOverride_Holds_UntilCleared()
    {
        using var host = MakeHost(landingDay: 30);

        Assert.Equal((false, 16.0), host.SetPace(false, 16.0));
        Assert.True(host.PaceOverridden);
        Assert.Equal((true, 16.0), host.SetPace(true, 16.0));

        Assert.Equal((false, 4.0), host.SetPace(false, null));
        Assert.False(host.PaceOverridden);
    }

    // The schedule reaches the client: genesis carries it, and every view carries the
    // landing tick and the pace it was produced at.
    [Fact]
    public void TheScheduleAndThePace_AreOnTheWire()
    {
        using var host = MakeHost(landingDay: 30);

        using var world = JsonDocument.Parse(host.BuildWorldJson());
        var pace = world.RootElement.GetProperty("pace");
        Assert.Equal(30L * Time.Day, pace.GetProperty("landingTick").GetInt64());
        Assert.Equal(4.0, pace.GetProperty("preludeTicksPerSecond").GetDouble());
        Assert.Equal(1.0, pace.GetProperty("ticksPerSecond").GetDouble());

        using var view = JsonDocument.Parse(host.BuildViewV2Json(playerId: 0, reveal: false));
        Assert.Equal(30L * Time.Day, view.RootElement.GetProperty("landingTick").GetInt64());
        Assert.Equal(4.0, view.RootElement.GetProperty("ticksPerSecond").GetDouble());
        Assert.False(view.RootElement.GetProperty("paused").GetBoolean());
    }
}
