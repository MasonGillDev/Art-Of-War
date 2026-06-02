using Sim.Core.World;

namespace Sim.Tests;

// Every cell of the Activity transition matrix is exercised here. Adding a new
// activity later means adding rows to this table — if the table stops being
// exhaustive the [Theory] count will visibly drop.
public class ActivityTransitionTests
{
    // (from, to, expectedLegal)
    public static IEnumerable<object[]> AllPairs()
    {
        foreach (Activity from in Enum.GetValues<Activity>())
            foreach (Activity to in Enum.GetValues<Activity>())
            {
                var legal =
                    from == to ||
                    to == Activity.Idle ||
                    from == Activity.Idle;
                yield return new object[] { from, to, legal };
            }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void CanTransition_MatchesTable(Activity from, Activity to, bool expected)
    {
        Assert.Equal(expected, ActivityTransitions.CanTransition(from, to));
    }

    [Fact]
    public void Matrix_IsExhaustive()
    {
        // 5 activities × 5 = 25 pairs. If a new activity is added, this number
        // must grow to keep coverage exhaustive.
        var count = AllPairs().Count();
        var expected = Enum.GetValues<Activity>().Length;
        Assert.Equal(expected * expected, count);
    }

    [Fact]
    public void TrySetActivity_RejectsIllegalHop_AndPreservesState()
    {
        var u = new Unit(1, new TileCoord(0, 0));
        Assert.True(u.TrySetActivity(Activity.Working, new TileCoord(2, 2)));
        // Working → Hauling is illegal (must pass through Idle first).
        Assert.False(u.TrySetActivity(Activity.Hauling));
        Assert.Equal(Activity.Working, u.Activity);
        Assert.Equal(new TileCoord(2, 2), u.Assignment);
    }

    [Fact]
    public void TrySetActivity_IdleClearsAssignment()
    {
        var u = new Unit(1, new TileCoord(0, 0));
        Assert.True(u.TrySetActivity(Activity.Building, new TileCoord(5, 5)));
        Assert.Equal(new TileCoord(5, 5), u.Assignment);
        Assert.True(u.TrySetActivity(Activity.Idle));
        Assert.Null(u.Assignment);
    }
}
