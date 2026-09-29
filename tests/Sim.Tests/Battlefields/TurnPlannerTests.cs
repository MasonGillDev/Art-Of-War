using Sim.Core.Battlefields;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// M41 — orders and doctrine becoming steps (docs/battlefield-grid.md §3, §4, §7).
public class TurnPlannerTests
{
    private const int Blue = 0, Red = 1;

    private static BoardUnit U(int id, int owner, int x, int y, bool ranged = false) =>
        new(id, owner, new Subtile(x, y), 10, 1, ranged);

    private static BoardState Board(params BoardUnit[] units) => new(SubtileLayer.Open, units, (a, b) => a != b);

    private static PlannedStep Plan(BoardState b, int id, BattleOrder? order = null, BattleDoctrine? doctrine = null,
        BoardSurroundings? around = null) =>
        TurnPlanner.Plan(b, b.Get(id)!, order, doctrine ?? BattleDoctrine.Hold, around ?? BoardSurroundings.Anywhere);

    [Fact]
    public void MoveTo_TakesTheShortestPath_TiesNorthFirst()
    {
        var b = Board(U(1, Blue, 1, 1));
        Assert.Equal(new Subtile(1, 0), Plan(b, 1, BattleOrder.MoveTo(new Subtile(2, 0))).To);
    }

    [Fact]
    public void MoveTo_OffTheBoard_WalksToTheEdgeThenLeaves()
    {
        var b = Board(U(1, Blue, 1, 1));
        var target = new Subtile(4, 1);
        Assert.Equal(new Subtile(2, 1), Plan(b, 1, BattleOrder.MoveTo(target)).To);
        var atEdge = Board(U(1, Blue, 3, 1));
        Assert.Equal(target, Plan(atEdge, 1, BattleOrder.MoveTo(target)).To);
    }

    [Fact]
    public void Route_IsFollowedExactly_AndAdvancesAsWaypointsAreReached()
    {
        var route = BattleOrder.Route(new[] { new Subtile(1, 2), new Subtile(2, 2), new Subtile(2, 1) });
        var b = Board(U(1, Blue, 1, 1));
        Assert.Equal(new Subtile(1, 2), Plan(b, 1, route).To);
        var after = route.After(new Subtile(1, 2), moved: true)!;
        Assert.Equal(1, after.NextWaypoint);
        Assert.Null(after.After(new Subtile(2, 1), moved: true)?.After(new Subtile(2, 1), true));
    }

    [Fact]
    public void Route_KnockedOff_WalksBackToTheNextWaypoint()
    {
        var route = BattleOrder.Route(new[] { new Subtile(1, 2), new Subtile(1, 3) });
        var b = Board(U(1, Blue, 3, 3));
        // Two equally short ways back to (1,2); north first.
        Assert.Equal(new Subtile(3, 2), Plan(b, 1, route).To);
    }

    [Fact]
    public void Route_MustBeContiguous()
    {
        Assert.Throws<ArgumentException>(() => BattleOrder.Route(new[] { new Subtile(0, 0), new Subtile(2, 0) }));
    }

    [Fact]
    public void Hold_OverridesDoctrine()
    {
        var b = Board(U(1, Blue, 0, 0), U(2, Red, 3, 3));
        var step = Plan(b, 1, BattleOrder.Hold(), BattleDoctrine.Advance);
        Assert.Null(step.To);
        Assert.Equal(StepNote.Holding, step.Note);
    }

    [Fact]
    public void Advance_ClosesOnTheNearestEnemy()
    {
        var b = Board(U(1, Blue, 0, 0), U(2, Red, 3, 0), U(3, Red, 0, 3));
        Assert.Equal(new Subtile(1, 0), Plan(b, 1, doctrine: BattleDoctrine.Advance).To);
    }

    [Fact]
    public void Advance_StaysPut_InADuel()
    {
        var b = Board(U(1, Blue, 1, 1), U(2, Red, 1, 1), U(3, Red, 3, 3));
        Assert.Null(Plan(b, 1, doctrine: BattleDoctrine.Advance).To);
    }

    [Fact]
    public void Support_StepsNextToTheNearestFriendInADuel()
    {
        var b = Board(U(1, Blue, 3, 3, ranged: true), U(2, Blue, 1, 1), U(3, Red, 1, 1));
        var step = Plan(b, 1, doctrine: BattleDoctrine.Support).To!.Value;
        Assert.Equal(1, Subtile.Manhattan(new Subtile(3, 3), step));
    }

    [Fact]
    public void Withdraw_GoesBackTheWayItCame()
    {
        var b = Board(U(1, Blue, 1, 1));
        var around = new BoardSurroundings(new HashSet<Heading>(Headings.All), new Dictionary<int, Heading> { [1] = Heading.South });
        Assert.Equal(new Subtile(1, 2), Plan(b, 1, BattleOrder.Withdraw(), around: around).To);
    }

    [Fact]
    public void Withdraw_WithNoWayIn_TakesTheNearestEdgeWithoutAnEnemyOnIt()
    {
        // At (0,1): west is nearest, but an enemy stands on the west row.
        var b = Board(U(1, Blue, 0, 1), U(2, Red, 0, 3));
        Assert.Equal(Heading.North, TurnPlanner.WithdrawEdge(b, b.Get(1)!, BoardSurroundings.Anywhere));
        var alone = Board(U(1, Blue, 0, 1));
        Assert.Equal(Heading.West, TurnPlanner.WithdrawEdge(alone, alone.Get(1)!, BoardSurroundings.Anywhere));
    }

    [Fact]
    public void Withdraw_OnlyThroughEdgesTheWorldAllows()
    {
        var b = Board(U(1, Blue, 0, 1));
        var around = new BoardSurroundings(new HashSet<Heading> { Heading.East }, new Dictionary<int, Heading>());
        Assert.Equal(new Subtile(1, 1), Plan(b, 1, BattleOrder.Withdraw(), around: around).To);
        var none = new BoardSurroundings(new HashSet<Heading>(), new Dictionary<int, Heading>());
        Assert.Equal(StepNote.CannotLeaveThere, Plan(b, 1, BattleOrder.Withdraw(), around: none).Note);
    }

    [Fact]
    public void WithdrawAtThreshold_TurnsAnyBehaviourIntoWithdrawal()
    {
        var b = Board(U(1, Red, 1, 1), U(2, Blue, 3, 1));
        var doctrine = new BattleDoctrine(DoctrineBehaviour.Advance, WithdrawBelow: 2);
        var step = Plan(b, 1, doctrine: doctrine).To!.Value;
        Assert.NotEqual(new Subtile(2, 1), step);   // not toward the enemy
    }

    [Fact]
    public void DefaultDoctrine_ByRole()
    {
        Assert.Equal(DoctrineBehaviour.Hold, BattleDoctrine.DefaultFor(UnitRole.Soldier).Behaviour);
        Assert.Equal(DoctrineBehaviour.Support, BattleDoctrine.DefaultFor(UnitRole.Archer).Behaviour);
        Assert.Equal(DoctrineBehaviour.Advance, BattleDoctrine.DefaultFor(UnitRole.Bandit).Behaviour);
        Assert.Equal(DoctrineBehaviour.Withdraw, BattleDoctrine.DefaultFor(UnitRole.Farmer).Behaviour);
        Assert.Equal(DoctrineBehaviour.Withdraw, BattleDoctrine.DefaultFor(UnitRole.Hauler).Behaviour);
    }

    [Fact]
    public void Swap_WithANonAdjacentOrForeignUnit_IsRefusedAsGone()
    {
        var b = Board(U(1, Blue, 0, 0), U(2, Blue, 2, 0), U(3, Red, 1, 0));
        Assert.Equal(StepNote.SwapPartnerGone, Plan(b, 1, BattleOrder.Swap(2)).Note);
        Assert.Equal(StepNote.SwapPartnerGone, Plan(b, 1, BattleOrder.Swap(3)).Note);
    }
}

// M41 — doctrine walks round its own line where it can (found in the test
// bed: a supporting archer kept stepping into the back of its own soldiers).
public class DoctrinePathingTests
{
    private static BoardUnit U(int id, int owner, int x, int y, bool ranged = false) =>
        new(id, owner, new Subtile(x, y), 10, 1, ranged);

    [Fact]
    public void Support_WalksRoundFriends_RatherThanIntoThem()
    {
        // Friends fill row 1 west of the duel; the archer at (0,2) goes along row 2.
        var b = new BoardState(SubtileLayer.Open, new[]
        {
            U(1, 0, 0, 1), U(2, 0, 1, 1), U(3, 0, 2, 1), U(4, 0, 3, 1), U(9, 1, 3, 1),
            U(5, 0, 0, 2, ranged: true),
        }, (a, c) => a != c);
        var step = TurnPlanner.Plan(b, b.Get(5)!, null, BattleDoctrine.Support, BoardSurroundings.Anywhere);
        Assert.Equal(new Subtile(1, 2), step.To);
    }
}
