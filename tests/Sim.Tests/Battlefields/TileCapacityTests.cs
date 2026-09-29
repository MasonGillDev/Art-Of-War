using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// The per-side unit cap (docs/structure-footprints.md, "The unit cap"): a side
// may have no more units on a tile than it has subtiles to stand on.
public class TileCapacityTests
{
    private const int Blue = 0, Red = 1;
    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord Field = new(5, 5);

    private static GameWorld World(CombatModel model = CombatModel.Grid)
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60, Model: model),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        w.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return w;
    }

    private static int _nextId = 100;
    private static Unit Put(GameWorld w, int owner, TileCoord at, UnitRole role = UnitRole.Farmer)
    {
        var u = new Unit(_nextId++, at) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        return u;
    }

    private static void Fill(GameWorld w, int owner, TileCoord at, int n)
    {
        for (var i = 0; i < n; i++) Put(w, owner, at);
    }

    [Fact]
    public void Capacity_IsTheSubtilesTheSideMayStandOn()
    {
        var w = World();
        Assert.Equal(16, TileCapacity.For(w, Field, Blue));
        Assert.Equal(16, TileCapacity.For(w, Keep, Blue));   // the castle: ring and courtyard
        Assert.Equal(5, TileCapacity.For(w, Keep, Red));     // attackers: the gap and courtyard
        w.AddStructure(new School(new TileCoord(3, 3)) { OwnerId = Blue });
        Assert.Equal(12, TileCapacity.For(w, new TileCoord(3, 3), Red));
        w.AddStructure(new Barracks(new TileCoord(3, 7)) { OwnerId = Blue });
        Assert.Equal(12, TileCapacity.For(w, new TileCoord(3, 7), Blue));   // the tower is an archer's post, not counted
    }

    [Fact]
    public void ASeventeenthUnit_StopsOnTheTileBefore_ButAnEnemyStillGetsIn()
    {
        var w = World();
        Fill(w, Blue, Field, 16);
        var late = Put(w, Blue, new TileCoord(Field.X - 1, Field.Y));
        var foe = Put(w, Red, new TileCoord(Field.X + 1, Field.Y), UnitRole.Soldier);
        var sim = new Simulation(w, seed: 3);
        sim.SubmitIntent(0, new MoveIntent(late.Id, Field) { PlayerId = Blue });
        sim.SubmitIntent(0, new MoveIntent(foe.Id, Field) { PlayerId = Red });
        sim.Run(until: 400);
        Assert.Equal(new TileCoord(Field.X - 1, Field.Y), late.Position);
        Assert.Equal(Field, foe.Position);
        Assert.Equal(16, TileCapacity.SideCount(w, Field, Blue));
    }

    [Fact]
    public void ANewUnitOnAFullTile_AppearsOnTheNearestTileWithRoom()
    {
        var w = World();
        Fill(w, Blue, Field, 16);
        var sim = new Simulation(w, seed: 3);
        var baby = Sim.Core.Population.Population.OnUnitAdded(sim, new Unit(900, Field) { OwnerId = Blue });
        Assert.NotEqual(Field, baby.Position);
        Assert.Equal(1, Math.Abs(baby.Position.X - Field.X) + Math.Abs(baby.Position.Y - Field.Y));
        Assert.Equal(new TileCoord(Field.X, Field.Y - 1), baby.Position);   // nearest, then north first
    }

    [Fact]
    public void ThePlanner_RoutesRoundATileItSeesIsFull()
    {
        var w = World();
        // The straight way runs through a tile full of the player's own: it goes round.
        Fill(w, Blue, Field, 16);
        var visible = Sim.Core.Vision.View.VisibleTiles(w, Blue);
        var path = Pathfinding.FindPath(w.Grid, new TileCoord(Field.X - 1, Field.Y), new TileCoord(Field.X + 1, Field.Y),
            MovementCost.Planner(w, Blue, visible, 0));
        Assert.NotNull(path);
        Assert.DoesNotContain(Field, path!);
    }

    [Fact]
    public void InTheDefaultGame_ThereIsNoPerSideCap()
    {
        var w = World(CombatModel.Pooled);
        Fill(w, Blue, Field, 20);
        Assert.True(TileCapacity.HasRoom(w, Field, Blue));
    }
}
