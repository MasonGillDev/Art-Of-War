using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Equipment;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// M41 — the turn resolver against docs/battlefield-grid.md §3 and §5, one
// test per rule. Numbers come from the game's catalogs and BattleConfig,
// never written in.
public class TurnResolverTests
{
    private const int Blue = 0, Red = 1;
    private static readonly BattleConfig Cfg = new();

    // A fielded soldier: sword + shield. A fielded archer: bow.
    private static int SoldierHp => UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth + EquipmentCatalog.Spec(Resource.Shield).HealthModifier;
    private static int SoldierDmg => UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower + EquipmentCatalog.Spec(Resource.BronzeSword).PowerModifier;
    private static int ArcherHp => UnitCombatCatalog.Spec(UnitRole.Archer).BaseHealth;
    private static int ArcherDmg => UnitCombatCatalog.Spec(UnitRole.Archer).BasePower + EquipmentCatalog.Spec(Resource.Bow).PowerModifier;

    private static BoardUnit Soldier(int id, int owner, int x, int y) => new(id, owner, new Subtile(x, y), SoldierHp, SoldierDmg, false);
    private static BoardUnit Archer(int id, int owner, int x, int y) => new(id, owner, new Subtile(x, y), ArcherHp, ArcherDmg, true);

    private static BoardState Board(params BoardUnit[] units) => Board(SubtileLayer.Open, units);
    private static BoardState Board(SubtileLayer layer, params BoardUnit[] units) =>
        new(layer, units, (a, b) => a != b);

    private static PlannedStep To(int x, int y) => new(new Subtile(x, y));

    private static TurnResult Resolve(BoardState b, params (int Id, PlannedStep Step)[] steps) =>
        TurnResolver.Resolve(b, steps.ToDictionary(s => s.Id, s => s.Step), Cfg);

    private static Subtile At(TurnResult r, int id) => r.After.Get(id)!.At;
    private static int Hp(TurnResult r, int id) => r.After.Get(id)!.Hp;
    private static int Alone(int dmg) => dmg * BattleConfig.SteadyMorale / BattleConfig.SteadyMorale;
    private static int WithFriends(int dmg, int n) => dmg * (BattleConfig.SteadyMorale + Cfg.LineSupport * n) / BattleConfig.SteadyMorale;

    // ---- the collision table ----------------------------------------------------

    [Fact]
    public void TwoEnemiesIntoTheSameEmptySubtile_BothArrive_AndDuelThisTurn()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 2, 1)), (1, To(1, 1)), (2, To(1, 1)));
        Assert.Equal(new Subtile(1, 1), At(r, 1));
        Assert.Equal(new Subtile(1, 1), At(r, 2));
        Assert.Equal(SoldierHp - Alone(SoldierDmg), Hp(r, 1));
        Assert.Contains(r.Hits, h => h.Kind == HitKind.Duel && h.AttackerId == 1 && h.TargetId == 2);
    }

    [Fact]
    public void TwoEnemiesChargingEachOther_NeitherMoves_AndTheyClash()
    {
        var r = Resolve(Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 2, 1)), (1, To(2, 1)), (2, To(1, 1)));
        Assert.Equal(new Subtile(1, 1), At(r, 1));
        Assert.Equal(new Subtile(2, 1), At(r, 2));
        Assert.Equal(StepNote.Clashed, r.Failed[1]);
        Assert.Single(r.Clashes);
        Assert.Equal(SoldierHp - Alone(SoldierDmg), Hp(r, 1));
        Assert.Equal(SoldierHp - Alone(SoldierDmg), Hp(r, 2));
    }

    [Fact]
    public void MovingIntoASubtileAnEnemyIsLeaving_Arrives_AndNoOneFights()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 1, 1)), (1, To(1, 1)), (2, To(1, 2)));
        Assert.Equal(new Subtile(1, 1), At(r, 1));
        Assert.Equal(new Subtile(1, 2), At(r, 2));
        Assert.Empty(r.Hits);
    }

    [Fact]
    public void MovingOntoAnEnemyThatStays_IsADuel()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 1, 1)), (1, To(1, 1)));
        Assert.Equal(new Subtile(1, 1), At(r, 1));
        Assert.True(r.After.InDuel(r.After.Get(1)!));
    }

    [Fact]
    public void TwoFriendsIntoOneSubtile_TheLowerIdArrives()
    {
        var r = Resolve(Board(Soldier(5, Blue, 0, 1), Soldier(3, Blue, 2, 1)), (5, To(1, 1)), (3, To(1, 1)));
        Assert.Equal(new Subtile(1, 1), At(r, 3));
        Assert.Equal(new Subtile(0, 1), At(r, 5));
        Assert.Equal(StepNote.AllyInTheWay, r.Failed[5]);
    }

    [Fact]
    public void MovingOntoAFriendThatStays_Fails()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Blue, 1, 1)), (1, To(1, 1)));
        Assert.Equal(new Subtile(0, 1), At(r, 1));
        Assert.Equal(StepNote.AllyInTheWay, r.Failed[1]);
    }

    [Fact]
    public void AColumnMovingTogether_AllArrive()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Blue, 1, 1), Soldier(3, Blue, 2, 1)),
            (1, To(1, 1)), (2, To(2, 1)), (3, To(3, 1)));
        Assert.Equal(new Subtile(1, 1), At(r, 1));
        Assert.Equal(new Subtile(2, 1), At(r, 2));
        Assert.Equal(new Subtile(3, 1), At(r, 3));
    }

    [Fact]
    public void AWheelOfFriends_AllArrive()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 0), Soldier(2, Blue, 1, 0), Soldier(3, Blue, 1, 1), Soldier(4, Blue, 0, 1)),
            (1, To(1, 0)), (2, To(1, 1)), (3, To(0, 1)), (4, To(0, 0)));
        Assert.Equal(new Subtile(1, 0), At(r, 1));
        Assert.Equal(new Subtile(0, 0), At(r, 4));
        Assert.Empty(r.Failed);
    }

    [Fact]
    public void AFailedMove_ChainsBackThroughAColumn()
    {
        // The head walks into a friend that holds; everyone behind it fails too.
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Blue, 1, 1), Soldier(3, Blue, 2, 1), Soldier(4, Blue, 3, 1)),
            (1, To(1, 1)), (2, To(2, 1)), (3, To(3, 1)));
        Assert.Equal(new Subtile(0, 1), At(r, 1));
        Assert.Equal(new Subtile(1, 1), At(r, 2));
        Assert.Equal(new Subtile(2, 1), At(r, 3));
        Assert.Equal(3, r.Failed.Count);
    }

    [Fact]
    public void Orders_AreResolvedTheSame_WhateverOrderTheyAreGivenIn()
    {
        var b = Board(Soldier(1, Blue, 0, 1), Soldier(2, Blue, 2, 1), Soldier(3, Red, 1, 0), Archer(4, Red, 1, 2));
        var a = new List<(int, PlannedStep)> { (1, To(1, 1)), (2, To(1, 1)), (3, To(1, 1)), (4, To(1, 1)) };
        var r1 = TurnResolver.Resolve(b, a.ToDictionary(x => x.Item1, x => x.Item2), Cfg);
        a.Reverse();
        var r2 = TurnResolver.Resolve(b, a.ToDictionary(x => x.Item1, x => x.Item2), Cfg);
        Assert.Equal(r1.After.Units.Select(u => (u.Id, u.At, u.Hp)), r2.After.Units.Select(u => (u.Id, u.At, u.Hp)));
    }

    // ---- duels, relief, stepping out -----------------------------------------------

    [Fact]
    public void SteppingOutWhileTheEnemyHolds_NobodyTakesDuelDamage()
    {
        var r = Resolve(Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 1, 1)), (1, To(0, 1)));
        Assert.Equal(SoldierHp, Hp(r, 1));
        Assert.Equal(SoldierHp, Hp(r, 2));
    }

    [Fact]
    public void SteppingOutWhileTheEnemyFollows_TheyDuelWhereTheyMeet()
    {
        var r = Resolve(Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 1, 1)), (1, To(0, 1)), (2, To(0, 1)));
        Assert.Equal(new Subtile(0, 1), At(r, 2));
        Assert.Equal(SoldierHp - Alone(SoldierDmg), Hp(r, 1));
    }

    [Fact]
    public void Relief_ASwapIntoADuel_HandsTheFightToTheFreshUnit()
    {
        var wounded = Soldier(1, Blue, 1, 1) with { Hp = 5 };
        var b = Board(wounded, Soldier(2, Red, 1, 1), Soldier(3, Blue, 0, 1));
        var steps = TurnPlanner.PlanAll(b,
            new Dictionary<int, BattleOrder> { [1] = BattleOrder.Swap(3) },
            new Dictionary<int, BattleDoctrine>(), BoardSurroundings.Anywhere);
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(new Subtile(0, 1), At(r, 1));
        Assert.Equal(new Subtile(1, 1), At(r, 3));
        Assert.Equal(5, Hp(r, 1));
        Assert.True(Hp(r, 3) < SoldierHp);
    }

    [Fact]
    public void Relief_WhenTheEnemyChargesTheRelievingUnit_BothFail_AndTheWoundedStaysInTheDuel()
    {
        var b = Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 1, 1), Soldier(3, Blue, 0, 1));
        var steps = TurnPlanner.PlanAll(b,
            new Dictionary<int, BattleOrder> { [1] = BattleOrder.Swap(3), [2] = BattleOrder.MoveTo(new Subtile(0, 1)) },
            new Dictionary<int, BattleDoctrine>(), BoardSurroundings.Anywhere);
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(new Subtile(1, 1), At(r, 1));
        Assert.Equal(new Subtile(1, 1), At(r, 2));
        Assert.Equal(new Subtile(0, 1), At(r, 3));
        Assert.True(r.After.InDuel(r.After.Get(1)!));
    }

    [Fact]
    public void AClashingUnitThatEndsInADuel_FightsTheDuelInstead()
    {
        // Blue 1 and Red 2 charge each other; Red 3 walks onto Blue 1.
        var r = Resolve(Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 2, 1), Soldier(3, Red, 1, 0)),
            (1, To(2, 1)), (2, To(1, 1)), (3, To(1, 1)));
        Assert.DoesNotContain(r.Hits, h => h.Kind == HitKind.Clash && h.AttackerId == 1);
        Assert.Contains(r.Hits, h => h.Kind == HitKind.Duel && h.AttackerId == 1 && h.TargetId == 3);
    }

    [Fact]
    public void NoPassingThrough_CannotCarryStraightOn_NorStepOntoTheOpponentsFormerSubtile()
    {
        // Blue entered (1,1) from the west; Red came from the north.
        var blue = Soldier(1, Blue, 1, 1) with { CameFrom = new Subtile(0, 1) };
        var red = Soldier(2, Red, 1, 1) with { CameFrom = new Subtile(1, 0) };
        var b = Board(blue, red);
        Assert.True(TurnPlanner.IsPassingThrough(b, blue, new Subtile(2, 1)));   // straight on
        Assert.True(TurnPlanner.IsPassingThrough(b, blue, new Subtile(1, 0)));   // where Red came from
        Assert.False(TurnPlanner.IsPassingThrough(b, blue, new Subtile(0, 1)));  // back off
        Assert.False(TurnPlanner.IsPassingThrough(b, blue, new Subtile(1, 2)));  // slide sideways
        var step = TurnPlanner.Plan(b, blue, BattleOrder.MoveTo(new Subtile(3, 1)), BattleDoctrine.Hold, BoardSurroundings.Anywhere);
        Assert.Null(step.To);
        Assert.Equal(StepNote.CannotPassOpponent, step.Note);
    }

    // ---- morale and archers -----------------------------------------------------------

    [Fact]
    public void Morale_EachFriendAlongside_AddsLineSupport()
    {
        // Blue 1 in the middle of a line of three, duelling Red 9.
        var r = Resolve(Board(Soldier(1, Blue, 1, 1), Soldier(2, Blue, 0, 1), Soldier(3, Blue, 2, 1), Soldier(9, Red, 1, 1)));
        var hit = r.Hits.Single(h => h.AttackerId == 1);
        Assert.Equal(WithFriends(SoldierDmg, 2), hit.Damage);
        Assert.Equal(Alone(SoldierDmg), r.Hits.Single(h => h.AttackerId == 9).Damage);
    }

    [Fact]
    public void Morale_KillingTheMiddleOfALine_LowersItsNeighboursNextTurn()
    {
        var weakMiddle = Soldier(1, Blue, 1, 1) with { Hp = 1 };
        var b = Board(weakMiddle, Soldier(2, Blue, 0, 1), Soldier(9, Red, 1, 1));
        var r = TurnResolver.Resolve(b, new Dictionary<int, PlannedStep>(), Cfg);
        Assert.Contains(1, r.Deaths);
        var neighbour = r.After.Get(2)!;
        Assert.Equal(BattleConfig.SteadyMorale, TurnResolver.Morale(r.After, neighbour, Cfg));
    }

    [Fact]
    public void Archers_ShootTheWeakestAdjacentEnemy_TiesNorthFirst_AndFireIntoDuels()
    {
        var r = Resolve(Board(Archer(1, Blue, 1, 1), Soldier(2, Red, 1, 0), Soldier(3, Red, 1, 2), Soldier(4, Blue, 1, 2)));
        var arrow = r.Hits.Single(h => h.Kind == HitKind.Arrow);
        Assert.Equal(2, arrow.TargetId);                       // equal HP: north wins
        var hurt = Board(Archer(1, Blue, 1, 1), Soldier(2, Red, 1, 0), Soldier(3, Red, 1, 2) with { Hp = 4 }, Soldier(4, Blue, 1, 2));
        Assert.Equal(3, Resolve(hurt).Hits.Single(h => h.Kind == HitKind.Arrow).TargetId);   // weakest wins
    }

    [Fact]
    public void Archers_CannotTargetAnEnemyInCover()
    {
        var layer = SubtileLayer.With((new Subtile(1, 0), SubtileKind.Cover));
        var r = Resolve(Board(layer, Archer(1, Blue, 1, 1), Soldier(2, Red, 1, 0)));
        Assert.DoesNotContain(r.Hits, h => h.Kind == HitKind.Arrow);
    }

    [Fact]
    public void AnArcherInADuel_FightsItsOpponent_AndDoesNotShoot()
    {
        var r = Resolve(Board(Archer(1, Blue, 1, 1), Soldier(2, Red, 1, 1), Soldier(3, Red, 1, 0)));
        var hit = r.Hits.Single(h => h.AttackerId == 1);
        Assert.Equal(HitKind.Duel, hit.Kind);
        Assert.Equal(2, hit.TargetId);
    }

    [Fact]
    public void EvenDuel_TwoEqualSoldiersDieTheSameTurn()
    {
        var b = Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 1, 1));
        var turns = 0;
        TurnResult r;
        do { r = TurnResolver.Resolve(b, new Dictionary<int, PlannedStep>(), Cfg); b = r.After; turns++; }
        while (r.Deaths.Count == 0 && turns < 100);
        Assert.Equal(2, r.Deaths.Count);
        Assert.Equal((SoldierHp + SoldierDmg - 1) / SoldierDmg, turns);
    }

    // ---- the subtile layer --------------------------------------------------------------

    [Fact]
    public void TheSubtileLayerIsConsulted_AnObstacleBlocksMovesAndPaths()
    {
        var layer = SubtileLayer.With((new Subtile(1, 1), SubtileKind.Blocked));
        var b = Board(layer, Soldier(1, Blue, 0, 1));
        var r = Resolve(b, (1, To(1, 1)));
        Assert.Equal(new Subtile(0, 1), At(r, 1));
        var step = BattlePathing.FirstStepTo(layer, default, new Subtile(0, 1), new Subtile(2, 1));
        Assert.NotEqual(new Subtile(1, 1), step);
        Assert.NotNull(step);
        Assert.True(SubtileLayer.Open.IsOpen);
    }

    // ---- leaving and entering --------------------------------------------------------------

    [Fact]
    public void Leaving_HappensBeforeMoves_SoAChaserFindsTheSubtileEmpty()
    {
        var r = Resolve(Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 1, 1)), (1, To(-1, 1)), (2, To(0, 1)));
        Assert.Null(r.After.Get(1));
        Assert.Contains(r.Moves, m => m.UnitId == 1 && m.Left);
        Assert.Empty(r.Hits);
    }

    [Fact]
    public void Entering_AWaitingUnitComesOnInItsLane_AndDuelsAnEnemyThere()
    {
        var waiting = Soldier(1, Blue, -1, 2);
        var b = Board(waiting, Soldier(2, Red, 0, 2));
        var steps = TurnPlanner.PlanAll(b, new Dictionary<int, BattleOrder>(), new Dictionary<int, BattleDoctrine>(), BoardSurroundings.Anywhere);
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(new Subtile(0, 2), At(r, 1));
        Assert.True(r.After.InDuel(r.After.Get(1)!));
        Assert.Contains(r.Moves, m => m.UnitId == 1 && m.Entered);
    }

    [Fact]
    public void Entering_ALaneHeldByAFriend_Fails_AndTheUnitKeepsWaiting()
    {
        var b = Board(Soldier(1, Blue, -1, 2), Soldier(2, Blue, 0, 2));
        var steps = TurnPlanner.PlanAll(b, new Dictionary<int, BattleOrder>(), new Dictionary<int, BattleDoctrine>(), BoardSurroundings.Anywhere);
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(new Subtile(-1, 2), At(r, 1));
    }

    [Fact]
    public void Wall_FourOnAnEdgeRow_AllowAtMostFourDuels_FromThatSide()
    {
        var units = new List<BoardUnit>();
        for (var lane = 0; lane < 4; lane++) units.Add(Soldier(1 + lane, Blue, lane, 0));
        for (var i = 0; i < 4; i++) units.Add(Soldier(10 + i, Red, i, -1));
        var b = Board(units.ToArray());
        var steps = TurnPlanner.PlanAll(b, new Dictionary<int, BattleOrder>(), new Dictionary<int, BattleDoctrine>(), BoardSurroundings.Anywhere);
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(4, r.After.OnBoard.Count(u => u.OwnerId == Blue && r.After.InDuel(u)));
    }
}
