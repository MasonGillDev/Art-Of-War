using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests.Battlefields;

// M43 (docs/fix-combat-m43.md): walking onto a battlefield. A step across an edge onto a
// tile with an open board needs room there: a walker that finds none WAITS at the edge (on
// its own tile, off the board) and retries on the board's beats, then gives up after
// BattleConfig.MaxEntryWaitBeats. It never steps in and gets popped to another tile. A hostile
// unit on the arrival subtile is no block (that is a duel). A pop never changes a tile.
public class BattlefieldEntryTests
{
    private const int Blue = 0, Red = 1;
    private const long Turn = 60;
    private static readonly TileCoord Approach = new(2, 0), Field = new(3, 0), Next = new(4, 0);

    private static (Simulation sim, GameWorld world) Make()
    {
        var world = new GameWorld(new TileGrid(9, 1, Biome.Grassland), new DiplomacyConfig(), new CombatConfig(RoundIntervalTicks: Turn));
        world.Players[Blue] = new Player(Blue);
        world.Players[Red] = new Player(Red);
        world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return (new Simulation(world, seed: 5), world);
    }

    private static Unit Put(GameWorld w, int id, int owner, TileCoord tile, Subtile at, UnitRole role = UnitRole.Soldier)
    {
        var u = new Unit(id, tile) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        u.Subtile = at;
        return u;
    }

    // An open board on Field: a Blue guard on the west edge subtile (0,1), a Red soldier at the far side.
    private static Unit OpenBoard(Simulation sim, GameWorld w, out Unit guard)
    {
        guard = Put(w, 10, Blue, Field, new Subtile(0, 1));
        var red = Put(w, 20, Red, Field, new Subtile(3, 1));
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        Assert.True(w.Battlefields.ContainsKey(Field), "fixture: the board did not open");
        return red;
    }

    // The walker stands at the east edge of Approach and is drawn a route onto Field.
    private static Unit Walker(Simulation sim, GameWorld w, int id = 30)
    {
        var u = Put(w, id, Blue, Approach, new Subtile(3, 1));
        var route = new[] { new WorldSubtile(Field.X * 4, 1), new WorldSubtile(Field.X * 4 + 1, 1) };
        sim.SubmitIntent(sim.Now, new SubtileRouteIntent(id, route) { PlayerId = Blue });
        return u;
    }

    // Ticks the walker's first step (across the edge onto Field) takes.
    private static long Step(GameWorld w, Unit walker) =>
        SubtileRoutes.StepCost(w, walker, new WorldSubtile(Approach.X * 4 + 3, 1), new WorldSubtile(Field.X * 4, 1), 0);

    // Simulation.Run(until) doesn't advance Now past the last event, so each simulation keeps its own clock.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, Clock> Clocks = new();

    private sealed class Clock { public long Now; }

    private static void Tick(Simulation sim, long count)
    {
        var clock = Clocks.GetOrCreateValue(sim);
        for (var i = 0L; i < count; i++) sim.Run(until: ++clock.Now);
    }

    [Fact]
    public void Step_OntoABoard_WithAFriendOnTheArrivalSubtile_Waits_AndNeverLeavesItsTile()
    {
        var (sim, w) = Make();
        OpenBoard(sim, w, out var guard);
        var walker = Walker(sim, w);

        var tiles = new HashSet<TileCoord>();
        var step = Step(w, walker);
        for (var t = 0L; t < step + 2 * Turn; t++)
        {
            Tick(sim, 1);
            tiles.Add(walker.Position);
        }
        Assert.Equal(new[] { Approach }, tiles);                 // never on Field, never anywhere else
        Assert.Equal(Field, walker.WaitingToEnter);
        Assert.Null(walker.Board);
        Assert.True(walker.IsWalking);                           // its route is kept

        CombatRules.OnUnitDeath(sim, guard);                     // the friend is gone
        Tick(sim, 2 * Turn + 2 * step);
        Assert.Null(walker.WaitingToEnter);
        Assert.Equal(Field, walker.Position);
        Assert.NotNull(walker.Board);                            // it came on and joined
        Assert.Equal(Field, walker.Board!.Tile);
    }

    [Fact]
    public void Step_OntoAFullBoard_Waits_ThenEndsItsWalk_AfterTheMaxBeats()
    {
        var (sim, w) = Make();
        // 16 Blue fill every subtile; one Red shares a subtile with one of them (a duel keeps the board open).
        var id = 100;
        foreach (var s in Subtile.All()) Put(w, id++, Blue, Field, s);
        Put(w, 20, Red, Field, new Subtile(3, 3));
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        var walker = Walker(sim, w);

        var step = Step(w, walker);
        Tick(sim, step + BattleConfig.MaxEntryWaitBeats * Turn - Turn);
        Assert.Equal(Field, walker.WaitingToEnter);              // still waiting inside the limit
        Assert.Equal(Approach, walker.Position);

        Tick(sim, 3 * Turn);                                     // past it
        Assert.Null(walker.WaitingToEnter);
        Assert.False(walker.IsWalking);
        Assert.Equal(Approach, walker.Position);                 // it never went in
        Assert.Null(walker.Board);
    }

    [Fact]
    public void Step_OntoABoard_WithAnEnemyOnTheArrivalSubtile_IsADuel_NotABlock()
    {
        var (sim, w) = Make();
        Put(w, 10, Blue, Field, new Subtile(3, 3));              // Blue's own guard, well clear of the edge
        Put(w, 20, Red, Field, new Subtile(0, 1));               // Red stands on the arrival subtile
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        Assert.True(w.Battlefields.ContainsKey(Field));
        var walker = Walker(sim, w);

        Tick(sim, 2 * Step(w, walker) + 2 * Turn);

        Assert.Null(walker.WaitingToEnter);
        Assert.Equal(Field, walker.Position);
        Assert.NotNull(walker.Board);
        // (it stepped onto the subtile Red held, a duel, and its board route has carried it on)
    }

    [Fact]
    public void Step_AcrossAnEdgeWithNoBoard_StillPassesThroughAFriend()
    {
        var (sim, w) = Make();
        Put(w, 10, Blue, Field, new Subtile(0, 1));              // a friend standing, no fight anywhere
        var walker = Walker(sim, w);

        Tick(sim, 2 * Step(w, walker) + Turn);

        Assert.Null(walker.WaitingToEnter);
        Assert.Empty(w.Battlefields);
        Assert.Equal(Field, walker.Position);                    // friends pass through, as before
    }

    [Fact]
    public void APop_NeverChangesATile()
    {
        var (sim, w) = Make();
        // Every subtile holds a Blue; a 17th Blue shares one of them (friends walking through each
        // other before the fight). Nothing is free, so the pop used to send it to another tile.
        var id = 100;
        foreach (var s in Subtile.All()) Put(w, id++, Blue, Field, s);
        var extra = Put(w, 99, Blue, Field, new Subtile(0, 0));
        Put(w, 20, Red, Field, new Subtile(3, 3));

        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);              // opening the board separates friends

        Assert.Equal(Field, extra.Position);
        Assert.NotNull(extra.Subtile);
    }

    [Fact]
    public void Leave_OntoANeighbourBoard_WithAFriendOnTheEdgeSubtile_StaysOnTheBoardUntilThereIsRoom()
    {
        var (sim, w) = Make();
        // Field is a board: Blue on its east edge, Red across from it. Next is a board too,
        // with a Blue friend standing on the subtile the leaver would step onto.
        var leaver = Put(w, 10, Blue, Field, new Subtile(3, 1));
        Put(w, 20, Red, Field, new Subtile(0, 3));
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        var friend = Put(w, 11, Blue, Next, new Subtile(0, 1));
        Put(w, 21, Red, Next, new Subtile(3, 3));
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Next);
        Assert.True(w.Battlefields.ContainsKey(Field) && w.Battlefields.ContainsKey(Next));

        sim.SubmitIntent(sim.Now, new SetBattleOrderIntent(10, (byte)BattleOrderKind.MoveTo, Subtile.OutsideLane(Heading.East, 1)) { PlayerId = Blue });
        Tick(sim, 3 * Turn);
        Assert.Equal(Field, leaver.Position);                    // held on the board, order kept
        Assert.NotNull(leaver.Board);

        CombatRules.OnUnitDeath(sim, friend);
        Tick(sim, 3 * Turn);
        Assert.Equal(Next, leaver.Position);                     // the room came; it crossed the edge
    }

    [Fact]
    public void AWithdrawingUnit_DoesNotBackOutIntoAnEnemy_WhenThereIsAnotherWay()
    {
        var (sim, w) = Make();
        // A Blue builder (withdraws by default) is on Field with a Red soldier; another Red soldier
        // stands on the tile to the west. The way out is east.
        var builder = Put(w, 10, Blue, Field, new Subtile(1, 1), UnitRole.Builder);
        Put(w, 20, Red, Field, new Subtile(3, 3));
        Put(w, 21, Red, Approach, new Subtile(1, 1));
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        Assert.NotNull(builder.Board);

        Tick(sim, 6 * Turn);

        Assert.Equal(Next, builder.Position);                    // out the east edge, not into the enemy
    }

    [Fact]
    public void AWaitingWalker_TwinRunAndMidWaitRestore_HashesMatch()
    {
        static Simulation Run(long until)
        {
            var (sim, w) = Make();
            OpenBoard(sim, w, out _);
            Walker(sim, w);
            Tick(sim, Step(w, w.Units[30]) + until);
            return sim;
        }
        var a = Run(Turn + Turn / 2);
        Assert.NotNull(a.World.Units[30].WaitingToEnter);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(Run(Turn + Turn / 2)));

        var restored = Snapshot.Restore(Snapshot.Serialize(a), seed: 5);
        Clocks.GetOrCreateValue(restored).Now = Clocks.GetOrCreateValue(a).Now;
        Assert.Equal(Field, restored.World.Units[30].WaitingToEnter);
        Tick(a, 5 * Turn);
        Tick(restored, 5 * Turn);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(restored));
    }
}
