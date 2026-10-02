using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests.Battlefields;

// M41 Phase 3 — the battlefield grid in the world (docs/battlefield-grid.md
// §2, §4, §5, §9; docs/m41-status.md). Grid worlds only; a Pooled world never
// opens a board (the rest of the suite covers it unchanged).
public class BattlefieldWorldTests
{
    private const int Blue = 0, Red = 1;
    private const long Turn = 60;
    private static readonly TileCoord Field = new(10, 10);

    private static GameWorld World()
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: Turn),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = new TileCoord(1, 1) },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return world;
    }

    private static Unit Add(GameWorld w, int id, int owner, TileCoord at, UnitRole role = UnitRole.Soldier)
    {
        var u = new Unit(id, at) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        return u;
    }

    private static void Move(Simulation sim, int id, int owner, TileCoord to) =>
        sim.SubmitIntent(sim.Now, new MoveIntent(id, to) { PlayerId = owner });

    private static void Order(Simulation sim, int id, int owner, BattleOrderKind kind, Subtile target = default) =>
        sim.SubmitIntent(sim.Now, new SetBattleOrderIntent(id, (byte)kind, target) { PlayerId = owner });

    // Red holds the field; Blue walks in from the west.
    private static Simulation Meeting(out Unit blue, out Unit red)
    {
        var w = World();
        red = Add(w, 2, Red, Field);
        blue = Add(w, 1, Blue, new TileCoord(Field.X - 1, Field.Y));
        var sim = new Simulation(w, seed: 7);
        Move(sim, 1, Blue, Field);
        return sim;
    }

    [Fact]
    public void AnArrivalOntoAnEnemy_OpensABoard_ArrivalOnItsEntryEdge_DefenderWhereItStood()
    {
        var sim = Meeting(out var blue, out var red);
        var stood = red.Subtile;                     // where the defender was placed before anyone came
        sim.Run(until: 40);
        var bf = Assert.Single(sim.World.Battlefields.Values);
        Assert.Equal(Field, bf.Tile);
        Assert.NotNull(blue.Board);
        Assert.Equal(0, blue.Subtile!.Value.X);      // the west edge: it came from the west
        Assert.NotNull(red.Board);
        Assert.Equal(stood, red.Subtile);            // a battle opens with everyone where they stood
        Assert.Equal(0, bf.NextTurnTick % Turn);     // on the beat
        Assert.True(bf.NextTurnTick - bf.OpenedTick >= Turn / 2);
    }

    [Fact]
    public void Doctrine_BlueHoldsRedHolds_TheBoardSuspends_AndAnOrderWakesIt()
    {
        var sim = Meeting(out var blue, out var red);
        sim.Run(until: 3 * Turn);
        var bf = sim.World.Battlefields[Field];
        Assert.True(bf.Suspended);                   // two soldiers holding apart: nothing to do

        Order(sim, 1, Blue, BattleOrderKind.MoveTo, red.Subtile!.Value);
        sim.Run(until: sim.Now + 1);
        Assert.False(bf.Suspended);
        sim.Run(until: 40 * Turn);
        Assert.True(!sim.World.Units.ContainsKey(1) || !sim.World.Units.ContainsKey(2));
    }

    [Fact]
    public void AFightToTheEnd_ClosesTheBoard_AndTheSurvivorIsBackAtWorldScale()
    {
        var w = World();
        var red = Add(w, 2, Red, Field, UnitRole.Farmer);
        var blue = Add(w, 1, Blue, new TileCoord(Field.X - 1, Field.Y));
        w.Units[1].Doctrine = BattleDoctrine.Advance;
        var sim = new Simulation(w, seed: 3);
        // The farmer wants out, but Blue closes first.
        Move(sim, 1, Blue, Field);
        sim.Run(until: 40 * Turn);
        Assert.Empty(sim.World.Battlefields);
        Assert.Null(blue.Board);
    }

    [Fact]
    public void Withdraw_LeavesOnTheBeat_ByStepAcrossTheEdge_AndNoLongerCounts()
    {
        var sim = Meeting(out var blue, out var red);
        sim.Run(until: 40);
        Order(sim, 1, Blue, BattleOrderKind.Withdraw);
        var beat = sim.World.Battlefields[Field].NextTurnTick;
        sim.Run(until: beat);
        Assert.Null(blue.Board);
        Assert.Equal(new TileCoord(Field.X - 1, Field.Y), blue.Position);   // stepped across the edge it came in by
        Assert.Empty(sim.World.Battlefields);                               // Red alone: the board closed
    }

    [Fact]
    public void AnArrivalOntoAnOpenBoard_JoinsAtOnce_OnTheSubtileItSteppedOnto()
    {
        var w = World();
        Add(w, 2, Red, Field);
        Add(w, 1, Blue, new TileCoord(Field.X - 1, Field.Y));
        var late = Add(w, 3, Blue, new TileCoord(Field.X, Field.Y - 2));
        var sim = new Simulation(w, seed: 1);
        Move(sim, 1, Blue, Field);
        sim.Run(until: 40);
        Assert.True(sim.World.Battlefields.ContainsKey(Field));
        Move(sim, 3, Blue, Field);
        var joined = -1L;
        for (var t = sim.Now + 1; t <= 20 * Turn && joined < 0; t++)
        {
            sim.Run(until: t);
            if (late.Position == Field) joined = t;
        }
        Assert.True(joined > 0, "it never reached the tile");
        Assert.NotNull(late.Board);                              // on the board the moment it stepped in, no beat waited
        Assert.NotNull(late.Subtile);
    }

    [Fact]
    public void Overflow_PastSixteenASide_TheRestHaveNoPlaceAndAreNotOnTheBoard()
    {
        var w = World();
        for (var i = 0; i < 20; i++) Add(w, 100 + i, Blue, Field, UnitRole.Farmer);
        Add(w, 1, Red, Field);
        var sim = new Simulation(w, seed: 1);
        CombatTrigger.MaybeBeginCombatOnTile(sim, Field);
        var blues = sim.World.Units.Values.Where(u => u.OwnerId == Blue).ToList();
        Assert.Equal(Subtile.Count, blues.Count(u => u.Board is not null));
        Assert.Equal(4, blues.Count(u => u.Board is null && u.Subtile is null));   // no subtile, no part in the fight
    }

    [Fact]
    public void OrdersAreRefused_ForAnotherPlayersUnit_OrAUnitNotInBattle()
    {
        var sim = Meeting(out _, out _);
        sim.Run(until: 40);
        var foreign = new SetBattleOrderIntent(2, (byte)BattleOrderKind.Hold) { PlayerId = Blue }.Resolve(sim);
        Assert.True(foreign.IsRejected);
        var w = sim.World;
        Add(w, 50, Blue, new TileCoord(3, 3));
        var idle = new SetBattleOrderIntent(50, (byte)BattleOrderKind.Hold) { PlayerId = Blue }.Resolve(sim);
        Assert.True(idle.IsRejected);
    }

    [Fact]
    public void AWorldMoveGivenInBattle_IsKept_AndTakesTheUnitOffByThatEdge()
    {
        var sim = Meeting(out var blue, out _);
        sim.Run(until: 40);
        var east = new TileCoord(Field.X + 2, Field.Y);
        Move(sim, 1, Blue, east);
        sim.Run(until: sim.Now + 1);
        // The tile walk became a battle ROUTE: the steps inside this tile, then the subtile just
        // outside the edge it leaves by.
        Assert.Equal(BattleOrderKind.Route, blue.Board!.Order!.Kind);
        Assert.Equal(Heading.East, blue.Board.Order.Waypoints[^1].EdgeBeyond);
        Assert.Equal(east, blue.PathFinalDest);                            // and it remembers where it was going
        sim.Run(until: 30 * Turn);
        Assert.Equal(east, blue.Position);
    }

    [Fact]
    public void TwinRuns_AndARestoreMidBattle_EndIdentically()
    {
        Simulation Battle()
        {
            var w = World();
            for (var i = 0; i < 4; i++) Add(w, 10 + i, Red, Field);
            for (var i = 0; i < 3; i++) Add(w, 20 + i, Blue, new TileCoord(Field.X - 1, Field.Y));
            Add(w, 30, Blue, new TileCoord(Field.X - 1, Field.Y), UnitRole.Archer);
            foreach (var u in w.Units.Values.Where(u => u.OwnerId == Blue)) u.Doctrine = u.Role == UnitRole.Archer ? BattleDoctrine.Support : BattleDoctrine.Advance;
            var sim = new Simulation(w, seed: 11);
            foreach (var id in new[] { 20, 21, 22, 30 }) Move(sim, id, Blue, Field);
            return sim;
        }

        var a = Battle();
        var b = Battle();
        a.Run(until: 30 * Turn);
        b.Run(until: 5 * Turn + 1);
        Assert.NotEmpty(b.World.Battlefields);
        var restored = Snapshot.Restore(Snapshot.Serialize(b), seed: 11);
        restored.Run(until: 30 * Turn);
        b.Run(until: 30 * Turn);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(restored));
    }

    [Fact]
    public void Attackers_LeftAloneWithAHostileStructure_LaySiege()
    {
        var w = World();
        var farm = w.Structures.Values.OfType<Castle>().First(c => c.OwnerId == Red);
        farm.Facing = Heading.West;                                // its gate faces the raider
        var guard = Add(w, 2, Red, farm.At);                     // a soldier: holds (a farmer would withdraw)
        guard.Health = 1;
        var raider = Add(w, 1, Blue, new TileCoord(farm.At.X - 1, farm.At.Y));
        raider.Doctrine = BattleDoctrine.Advance;
        var sim = new Simulation(w, seed: 1);
        Move(sim, 1, Blue, farm.At);
        sim.Run(until: 20 * Turn);
        Assert.Empty(sim.World.Battlefields);
        Assert.False(sim.World.Units.ContainsKey(2));
        Assert.DoesNotContain(sim.World.Units.Values, u => u.OwnerId == Red && u.Position == farm.At);
        Assert.True(sim.World.CombatStates.ContainsKey(farm.At) || farm.Health < StructureCatalog.Spec(StructureKind.Castle).BaseHealth);
    }
}

// M41 decision D7: nobody loots a tile while a battle is open on it.
public class BattlefieldLootingTests
{
    [Fact]
    public void LoadingCargo_OnAnOpenBattlefield_IsRefused()
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(10, 10),
                    CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 50 } },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
            StartingWars = new[] { (0, 1) },
        });
        world.AddUnit(new Unit(1, new TileCoord(10, 10)) { Role = UnitRole.Soldier, OwnerId = 0 });
        world.AddUnit(new Unit(2, new TileCoord(10, 10)) { Role = UnitRole.Soldier, OwnerId = 1 });
        var sim = new Simulation(world, seed: 1);
        CombatTrigger.MaybeBeginCombatOnTile(sim, new TileCoord(10, 10));
        Assert.NotEmpty(sim.World.Battlefields);
        var outcome = new Sim.Core.Logistics.LoadCargoIntent(2, Resource.Food) { PlayerId = 1 }.Resolve(sim);
        Assert.True(outcome.IsRejected);
        Assert.Contains("battlefield", outcome.Reason);
    }
}
