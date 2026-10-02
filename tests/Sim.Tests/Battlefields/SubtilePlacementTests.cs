using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// M42 phase 1 (docs/subtile-movement.md): every unit stands on a subtile, and
// only enemies share one.
public class SubtilePlacementTests
{
    private const int Blue = 0, Red = 1, Green = 2;
    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord Field = new(5, 5);
    private static readonly TileCoord Wild = new(3, 14);

    private static GameWorld World()
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        w.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return w;
    }

    private static int _nextId = 5000;
    private static Unit Put(GameWorld w, int owner, TileCoord at, UnitRole role = UnitRole.Farmer)
    {
        var u = new Unit(_nextId++, at) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        return u;
    }

    private static List<Unit> On(GameWorld w, TileCoord t) =>
        w.Units.Values.Where(u => u.Position == t).OrderBy(u => u.Id).ToList();

    private static void AssertOnlyEnemiesShare(GameWorld w)
    {
        foreach (var g in w.Units.Values.Where(u => u.Subtile is not null).GroupBy(u => (u.Position, u.Subtile)))
        {
            var us = g.ToList();
            for (var i = 0; i < us.Count; i++)
                for (var j = i + 1; j < us.Count; j++)
                    Assert.True(w.Diplomacy.AreHostile(us[i].OwnerId, us[j].OwnerId),
                        $"units {us[i].Id} and {us[j].Id} share {g.Key.Subtile} on {g.Key.Position} but aren't enemies");
        }
    }

    [Fact]
    public void EveryUnitAtGenesis_StandsOnASubtile_AndOnlyEnemiesShare()
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = Blue, CastlePosition = Keep,
                    UnitSpawns = Enumerable.Range(1, 12).Select(i => new UnitSpawn(i, Keep, UnitRole.Farmer, Blue)).ToArray(),
                },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        Assert.Equal(12, w.Units.Count);
        Assert.All(w.Units.Values, u => Assert.NotNull(u.Subtile));
        Assert.Equal(12, w.Units.Values.Select(u => u.Subtile).Distinct().Count());
        AssertOnlyEnemiesShare(w);
    }

    [Fact]
    public void CreatedUnits_TakeTheCentreRowsFirst_LowestLaneFirst()
    {
        var w = World();
        var placed = Enumerable.Range(0, 10).Select(_ => Put(w, Blue, Field).Subtile!.Value).ToList();
        Assert.Equal(new[]
        {
            new Subtile(0, 1), new Subtile(1, 1), new Subtile(2, 1), new Subtile(3, 1),
            new Subtile(0, 2), new Subtile(1, 2), new Subtile(2, 2), new Subtile(3, 2),
            new Subtile(0, 0), new Subtile(1, 0),
        }, placed);
    }

    [Fact]
    public void SixteenFillTheTile_ASeventeenthHasNoRoom_AndNobodySharesWithAFriend()
    {
        var w = World();
        var all = Enumerable.Range(0, 16).Select(_ => Put(w, Blue, Field)).ToList();
        Assert.Equal(16, all.Select(u => u.Subtile!.Value).Distinct().Count());
        var extra = Put(w, Blue, Field);
        Assert.Null(extra.Subtile);
        AssertOnlyEnemiesShare(w);
    }

    [Fact]
    public void NeutralsAndAllies_AreKeptApartToo()
    {
        var w = World();
        w.Players[Green] = new Player(Green);
        var a = Put(w, Blue, Field);
        var b = Put(w, Green, Field);   // neutral to Blue
        Assert.NotEqual(a.Subtile, b.Subtile);
        w.Diplomacy.SetState(FactionPair.Of(Blue, Green), RelationshipState.Ally);
        var c = Put(w, Green, Field);
        Assert.Equal(3, new[] { a.Subtile, b.Subtile, c.Subtile }.Distinct().Count());
    }

    [Fact]
    public void AnEnemy_TakesAnEmptySubtileFirst_AndSharesOnlyWhenTheRestAreHeld()
    {
        var w = World();
        var friends = Enumerable.Range(0, 15).Select(_ => Put(w, Blue, Field)).ToList();
        var first = Put(w, Red, Field);
        Assert.DoesNotContain(first.Subtile, friends.Select(f => f.Subtile));   // the one free subtile
        var second = Put(w, Red, Field);
        // The last free subtile is held by Red's first, which its own side can't share,
        // so the second one shares a Blue-held subtile: a duel.
        Assert.Contains(second.Subtile, friends.Select(f => f.Subtile));
        Assert.NotEqual(first.Subtile, second.Subtile);
        // With every subtile held by Blue, a Red unit shares one: a duel.
        var w2 = World();
        var blues = Enumerable.Range(0, 16).Select(_ => Put(w2, Blue, Field)).ToList();
        var lone = Put(w2, Red, Field);
        Assert.Contains(lone.Subtile, blues.Select(b => b.Subtile));
    }

    [Fact]
    public void OnACastleTile_AnAttackerIsOnlyPlacedWhereItCouldWalkFromAnEdge()
    {
        var w = World();
        var red = Put(w, Red, Keep, UnitRole.Soldier);
        var layer = Sim.Core.Battlefields.Battlefields.LayerFor(w, Keep);
        var usable = Placement.Usable(layer, Sim.Core.Battlefields.Battlefields.MoverFor(w, layer, red));
        Assert.Contains(red.Subtile!.Value, usable);
        Assert.Equal(14, usable.Count);   // the gap, the courtyard and the walls, as the cap says (2026-10-01)
    }

    [Fact]
    public void ADefender_PrefersOpenGround_OverTheWall()
    {
        var w = World();
        var layer = Sim.Core.Battlefields.Battlefields.LayerFor(w, Keep);
        var blue = Put(w, Blue, Keep, UnitRole.Soldier);
        Assert.True(layer.KindAt(blue.Subtile!.Value) is SubtileKind.Open or SubtileKind.Cover);
    }

    [Fact]
    public void ABuildingThatMakesAUnitsSubtileImpassable_PopsItToTheNearestOpenSubtile()
    {
        var w = World();
        var red = Put(w, Red, Wild, UnitRole.Soldier);
        Assert.Equal(new Subtile(0, 1), red.Subtile);   // a ring subtile once the castle stands
        var friend = Put(w, Blue, Wild);
        Assert.Equal(new Subtile(1, 1), friend.Subtile);

        w.AddStructure(new Castle(Wild) { OwnerId = Blue });

        var layer = Sim.Core.Battlefields.Battlefields.LayerFor(w, Wild);
        var usable = Placement.Usable(layer, Sim.Core.Battlefields.Battlefields.MoverFor(w, layer, red));
        Assert.Contains(red.Subtile!.Value, usable);
        Assert.NotEqual(new Subtile(0, 1), red.Subtile);
        Assert.NotEqual(friend.Subtile, red.Subtile);   // never onto a held subtile
        Assert.Equal(new Subtile(1, 1), friend.Subtile);   // the owner stands on the wall or courtyard: no need to move
        AssertOnlyEnemiesShare(w);
    }

    [Fact]
    public void APop_IsNeverThroughAWall_ItWalksThere()
    {
        var w = World();
        // A red unit on a ring subtile with the courtyard nearest by straight line
        // but a wall between: it must land on a subtile it can WALK to (the gap side).
        var red = Put(w, Red, Wild, UnitRole.Soldier);
        red.Subtile = new Subtile(0, 0);   // a ring corner, once the castle stands
        w.AddStructure(new Castle(Wild) { OwnerId = Blue });
        var layer = Sim.Core.Battlefields.Battlefields.LayerFor(w, Wild);
        Assert.Contains(red.Subtile!.Value, Placement.Usable(layer, Sim.Core.Battlefields.Battlefields.MoverFor(w, layer, red)));
    }

    [Fact]
    public void APop_IsDeterministic_LowestIdFirst_EachTakingTheNearestFree()
    {
        Subtile?[] Run()
        {
            var w = World();
            var reds = Enumerable.Range(0, 3).Select(_ => Put(w, Red, Wild, UnitRole.Soldier)).ToList();
            w.AddStructure(new Castle(Wild) { OwnerId = Blue });
            AssertOnlyEnemiesShare(w);
            return reds.Select(r => r.Subtile).ToArray();
        }
        Assert.Equal(Run(), Run());
        Assert.Equal(3, Run().Distinct().Count());
    }

    [Fact]
    public void AFullTile_NeverPopsTheUnitToAnotherTile_ItSharesWithAnEnemyInstead()
    {
        var w = World();
        var red = Put(w, Red, Wild, UnitRole.Soldier);
        red.Subtile = new Subtile(0, 0);
        // Fill every subtile the castle leaves an attacker (its gap and courtyard)
        // with the owner's units, then build it: the attacker has nowhere on the tile.
        w.AddStructure(new Castle(Wild) { OwnerId = Blue });
        var layer = Sim.Core.Battlefields.Battlefields.LayerFor(w, Wild);
        var usable = Placement.Usable(layer, Sim.Core.Battlefields.Battlefields.MoverFor(w, layer, red));
        foreach (var s in usable)
        {
            var b = new Unit(_nextId++, Wild) { Role = UnitRole.Farmer, OwnerId = Blue };
            w.Units.Add(b.Id, b);
            b.Subtile = s;
        }
        red.Subtile = new Subtile(0, 0);   // back on a ring subtile it may not stand on
        Placement.Reseat(w, Wild);
        // M43 (docs/fix-combat-m43.md): a pop never changes a unit's tile (that is a jump the client can
        // only draw as a teleport). It takes the best subtile left, one an enemy holds: a duel.
        Assert.Equal(Wild, red.Position);
        Assert.Contains(red.Subtile!.Value, usable);
    }

    [Fact]
    public void Peace_SeparatesUnitsSharingASubtile_TheLowestIdStays()
    {
        var w = World();
        var a = Put(w, Blue, Field);
        var b = Put(w, Red, Field);
        b.Subtile = a.Subtile;   // a duel in one subtile
        Assert.True(w.Diplomacy.AreHostile(Blue, Red));

        w.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Neutral);
        Placement.SeparateNonHostile(w);

        Assert.Equal(new Subtile(0, 1), a.Subtile);
        Assert.NotEqual(a.Subtile, b.Subtile);
        Assert.NotNull(b.Subtile);
        AssertOnlyEnemiesShare(w);
    }

    [Fact]
    public void WarThatContinues_LeavesADuelWhereItStands()
    {
        var w = World();
        var a = Put(w, Blue, Field);
        var b = Put(w, Red, Field);
        b.Subtile = a.Subtile;
        Placement.SeparateNonHostile(w);
        Assert.Equal(a.Subtile, b.Subtile);
    }

    [Fact]
    public void AHopIntoATile_SeatsTheUnitThere()
    {
        var w = World();
        var walker = Put(w, Blue, new TileCoord(Field.X - 1, Field.Y));
        var occupant = Put(w, Blue, Field);
        var sim = new Simulation(w, seed: 3);
        sim.SubmitIntent(0, new MoveIntent(walker.Id, Field) { PlayerId = Blue });
        sim.Run(until: 400);
        Assert.Equal(Field, walker.Position);
        Assert.NotNull(walker.Subtile);
        Assert.NotEqual(occupant.Subtile, walker.Subtile);
    }

    [Fact]
    public void SubtilesSurviveASaveAndRestore_AndRestoreDoesNotReplaceAnyone()
    {
        var w = World();
        var pin = Put(w, Blue, Field);
        pin.Subtile = new Subtile(3, 3);   // deliberately not where placement would put it
        var mate = Put(w, Blue, Field);
        var sim = new Simulation(w, seed: 5);
        var bytes = Snapshot.Serialize(sim);

        var restored = Snapshot.Restore(bytes, seed: 5);
        Assert.Equal(new Subtile(3, 3), restored.World.Units[pin.Id].Subtile);
        Assert.Equal(mate.Subtile, restored.World.Units[mate.Id].Subtile);
        foreach (var u in w.Units.Values)
            Assert.Equal(u.Subtile, restored.World.Units[u.Id].Subtile);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void ATwinRun_HashesEqual_WithSubtilesInTheState()
    {
        string Hash()
        {
            _nextId = 9000;   // ids are part of the state: both runs must use the same
            var w = World();
            for (var i = 0; i < 6; i++) Put(w, Blue, Field);
            var mover = Put(w, Blue, new TileCoord(Field.X - 2, Field.Y));
            var sim = new Simulation(w, seed: 9);
            sim.SubmitIntent(0, new MoveIntent(mover.Id, Field) { PlayerId = Blue });
            sim.Run(until: 600);
            return Snapshot.Hash(sim);
        }
        Assert.Equal(Hash(), Hash());
    }
}
