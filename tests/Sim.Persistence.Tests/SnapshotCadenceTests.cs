using Sim.Persistence;

namespace Sim.Persistence.Tests;

public class SnapshotCadenceTests
{
    [Fact]
    public void TicksThreshold_TriggersSnapshot()
    {
        var c = new SnapshotCadence(everyNTicks: 100, everyMIntents: 1000);
        c.AccumulateTicks(99);
        Assert.False(c.ShouldSnapshot());
        c.AccumulateTicks(1);
        Assert.True(c.ShouldSnapshot());
    }

    [Fact]
    public void IntentsThreshold_TriggersSnapshot()
    {
        var c = new SnapshotCadence(everyNTicks: 1_000_000, everyMIntents: 3);
        c.AccumulateIntent();
        c.AccumulateIntent();
        Assert.False(c.ShouldSnapshot());
        c.AccumulateIntent();
        Assert.True(c.ShouldSnapshot());
    }

    [Fact]
    public void Reset_ClearsBothCounters()
    {
        var c = new SnapshotCadence(10, 10);
        c.AccumulateTicks(20);
        c.AccumulateIntent();
        Assert.True(c.ShouldSnapshot());
        c.Reset();
        Assert.False(c.ShouldSnapshot());
    }
}
