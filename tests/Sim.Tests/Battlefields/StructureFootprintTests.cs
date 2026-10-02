using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests.Battlefields;

// Structure footprints (docs/structure-footprints.md): subtile types, the
// structure patterns and their rotation, and how movement on the board obeys
// them. Numbers come from the catalogs, never written in.
public class StructureFootprintTests
{
    private const int Blue = 0, Red = 1;
    private static readonly BattleConfig Cfg = new();

    private static readonly BoardMover Owner = new(Friendly: true, Archer: false);
    private static readonly BoardMover OwnerArcher = new(Friendly: true, Archer: true);
    private static readonly BoardMover Enemy = new(Friendly: false, Archer: false);

    private static SubtileLayer Layer(Structure s) => Footprints.For(s);

    private static Castle BlueCastle(Heading facing = Heading.North) =>
        new(new TileCoord(5, 5)) { OwnerId = Blue, Facing = facing };

    private static T Blues<T>(T s, Heading facing) where T : Structure { s.Facing = facing; return s; }

    private static int SoldierHp => UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
    private static int ArcherDmg => UnitCombatCatalog.Spec(UnitRole.Archer).BasePower + EquipmentCatalog.Spec(Resource.Bow).PowerModifier;
    private static BoardUnit Soldier(int id, int owner, int x, int y) => new(id, owner, new Subtile(x, y), SoldierHp, 1, false);
    private static BoardUnit Archer(int id, int owner, int x, int y) =>
        new(id, owner, new Subtile(x, y), UnitCombatCatalog.Spec(UnitRole.Archer).BaseHealth, ArcherDmg, true);
    private static BoardState Board(SubtileLayer layer, params BoardUnit[] units) => new(layer, units, (a, b) => a != b);

    private static int Count(SubtileLayer layer, Func<Subtile, bool> f) => Subtile.All().Count(f);

    // ---- the patterns ----------------------------------------------------------------

    [Fact]
    public void TheCastle_IsARing_TowersAtTheCorners_AKeepAtTheBack_OneGap_AndAnOpenCourtyard()
    {
        var l = Layer(BlueCastle());
        Assert.Equal(5, Count(l, s => l.KindAt(s) == SubtileKind.Wall));
        Assert.Equal(4, Count(l, s => l.KindAt(s) == SubtileKind.Tower));
        foreach (var c in new[] { new Subtile(0, 0), new Subtile(3, 0), new Subtile(0, 3), new Subtile(3, 3) })
        {
            Assert.Equal(SubtileKind.Tower, l.KindAt(c));
            Assert.Equal(Footprints.CastleTowerReach, l.ReachAt(c));
        }
        Assert.Equal(1, l.ReachAt(new Subtile(0, 1)));
        foreach (var c in new[] { new Subtile(1, 3), new Subtile(2, 3) })   // the keep, on the back wall
            Assert.Equal(SubtileKind.Blocked, l.KindAt(c));
        Assert.Equal(SubtileKind.Open, l.KindAt(new Subtile(1, 0)));   // the gap, facing north
        foreach (var c in new[] { new Subtile(1, 1), new Subtile(2, 1), new Subtile(1, 2), new Subtile(2, 2) })
            Assert.Equal(SubtileKind.Open, l.KindAt(c));
        // Each ring subtile is closed on its outer side, a corner on both.
        Assert.Equal(new[] { Heading.North, Heading.West }, l.ClosedSides(new Subtile(0, 0)));
        Assert.Equal(new[] { Heading.East }, l.ClosedSides(new Subtile(3, 2)));
        Assert.Empty(l.ClosedSides(new Subtile(1, 1)));
    }

    [Theory]
    [InlineData(Heading.North, 1, 0)]
    [InlineData(Heading.East, 3, 1)]
    [InlineData(Heading.South, 2, 3)]
    [InlineData(Heading.West, 0, 2)]
    public void Facing_TurnsThePattern_TheGapIsOnTheFacingEdge(Heading facing, int gx, int gy)
    {
        var l = Layer(BlueCastle(facing));
        var gap = new Subtile(gx, gy);
        Assert.Equal(SubtileKind.Open, l.KindAt(gap));
        Assert.True(gap.IsOnEdgeRow(facing));
        Assert.Equal(5, Count(l, s => l.KindAt(s) == SubtileKind.Wall));
        Assert.Equal(4, Count(l, s => l.KindAt(s) == SubtileKind.Tower));
        // The keep is on the wall opposite the gap.
        Assert.Equal(2, Count(l, s => l.KindAt(s) == SubtileKind.Blocked && s.IsOnEdgeRow(facing.Opposite())));
    }

    [Fact]
    public void AWall_IsALineOnTheMiddleRowNearerItsOuterSide_ClosedOnTheOuterSide()
    {
        var l = Layer(new Wall(new TileCoord(5, 5)) { OwnerId = Blue });
        for (var x = 0; x < Subtile.Size; x++)
        {
            Assert.Equal(SubtileKind.Wall, l.KindAt(new Subtile(x, 1)));
            Assert.Equal(new[] { Heading.North }, l.ClosedSides(new Subtile(x, 1)));
        }
        Assert.Equal(4, Count(l, s => l.KindAt(s) == SubtileKind.Wall));

        var east = Layer(Blues(new Wall(new TileCoord(5, 5)) { OwnerId = Blue }, Heading.East));
        Assert.Equal(SubtileKind.Wall, east.KindAt(new Subtile(2, 0)));   // the column nearer the east edge
        Assert.Equal(new[] { Heading.East }, east.ClosedSides(new Subtile(2, 3)));
    }

    [Fact]
    public void AGateTile_HasTwoGateSubtilesInItsLine_ATowerTileTwoTowers()
    {
        var gate = Layer(new Gate(new TileCoord(5, 5)) { OwnerId = Blue });
        Assert.Equal(2, Count(gate, s => gate.KindAt(s) == SubtileKind.Gate));
        Assert.Empty(gate.ClosedSides(new Subtile(1, 1)));
        var tower = Layer(new Tower(new TileCoord(5, 5)) { OwnerId = Blue });
        Assert.Equal(2, Count(tower, s => tower.KindAt(s) == SubtileKind.Tower));
        Assert.Equal(Heading.North, tower.TowerFront(new Subtile(1, 1)));
    }

    [Fact]
    public void TheRaiderCamp_IsAsDrawn_AndAnyoneWhoGetsToItsTowerStandsOnIt()
    {
        var camp = new BanditCamp(new TileCoord(5, 5)) { OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId };
        var l = Layer(camp);
        var blocked = new HashSet<Subtile> { new(0, 0), new(1, 0), new(0, 1), new(2, 1), new(0, 2) };
        foreach (var c in Subtile.All())
        {
            var expected = blocked.Contains(c) ? SubtileKind.Blocked
                : c == new Subtile(3, 3) ? SubtileKind.Tower
                : SubtileKind.Open;
            Assert.Equal(expected, l.KindAt(c));
        }
        var tower = new Subtile(3, 3);
        Assert.True(l.CanStand(tower, OwnerArcher));                       // a raider archer
        Assert.True(l.CanStand(tower, Owner));                             // a raider soldier (2026-10-01: the ground decides, not the role)
        Assert.True(l.CanStand(tower, new BoardMover(Friendly: false, Archer: true)));   // a player's archer that got there
        Assert.Equal(new Subtile(2, 3), BattlePathing.FirstStepTo(l, OwnerArcher, new Subtile(1, 3), tower));
        Assert.Equal(11, Count(l, c => l.CanStand(c, Enemy)));
    }

    // The user's drawings: each kind's Blocked and Cover subtiles facing North,
    // the rest open land.
    private static readonly (int, int)[] None = Array.Empty<(int, int)>();
    public static IEnumerable<object[]> Drawings() => new[]
    {
        new object[] { StructureKind.Farm, new[] { (2, 1) }, None },
        new object[] { StructureKind.School, new[] { (1, 1), (2, 1), (1, 2), (2, 2) }, None },
        new object[] { StructureKind.House, new[] { (1, 0), (2, 0), (2, 3) }, None },
        new object[] { StructureKind.Workshop, new[] { (2, 1), (1, 2), (2, 2) }, None },
        new object[] { StructureKind.Quarry, None, new[] { (2, 1) } },
        new object[] { StructureKind.Lodge, None, new[] { (1, 1), (1, 2) } },
        new object[] { StructureKind.Cache, new[] { (1, 1) }, None },
        new object[] { StructureKind.Idol, new[] { (1, 1) }, None },
        new object[] { StructureKind.Rubble, None, None },
        new object[] { StructureKind.Smelter, new[] { (1, 1) }, new[] { (2, 1) } },
        new object[] { StructureKind.Smithy, new[] { (1, 1) }, new[] { (3, 1) } },
        new object[] { StructureKind.Mine, new[] { (2, 0) }, None },
        new object[] { StructureKind.LumberCamp, new[] { (0, 1), (2, 2) }, None },
        new object[] { StructureKind.Barracks, new[] { (2, 1), (1, 2), (2, 2) }, None },
        new object[] { StructureKind.Stockpile, new[] { (1, 1), (1, 2) }, None },
        new object[] { StructureKind.ConstructionSite, new[] { (1, 1) }, new[] { (2, 2) } },
    };

    // Drawn towers, facing North.
    private static (int X, int Y)[] TowersOf(StructureKind kind) =>
        kind == StructureKind.Barracks ? new[] { (0, 0) } : None;

    private static Structure Make(StructureKind kind, TileCoord at) => kind switch
    {
        StructureKind.Farm => new Extractor(StructureKind.Farm, at) { OwnerId = Blue },
        StructureKind.School => new School(at) { OwnerId = Blue },
        StructureKind.House => new House(at) { OwnerId = Blue },
        StructureKind.Workshop => new Workshop(at) { OwnerId = Blue },
        StructureKind.Quarry => new Extractor(StructureKind.Quarry, at) { OwnerId = Blue },
        StructureKind.Lodge => new Lodge(at) { OwnerId = Blue },
        StructureKind.Cache => new Cache(at) { OwnerId = Blue },
        StructureKind.Idol => new Idol(at, default) { OwnerId = Blue },
        StructureKind.Rubble => new Rubble(at) { OwnerId = Sim.Core.Sieges.SiegeConstants.RubbleOwnerId },
        StructureKind.Smelter => new Extractor(StructureKind.Smelter, at) { OwnerId = Blue },
        StructureKind.Smithy => new Smithy(at) { OwnerId = Blue },
        StructureKind.Mine => new Extractor(StructureKind.Mine, at) { OwnerId = Blue },
        StructureKind.LumberCamp => new Extractor(StructureKind.LumberCamp, at) { OwnerId = Blue },
        StructureKind.Barracks => new Barracks(at) { OwnerId = Blue },
        StructureKind.Stockpile => new Stockpile(at) { OwnerId = Blue },
        StructureKind.ConstructionSite => ConstructionSiteAt(at),
        _ => throw new ArgumentException($"no test maker for {kind}"),
    };

    private static ConstructionSite ConstructionSiteAt(TileCoord at) =>
        new(at, StructureKind.House) { OwnerId = Blue };

    [Theory]
    [MemberData(nameof(Drawings))]
    public void DrawnStructures_AreAsDrawn_AndTurnWithTheirFacing(StructureKind kind, (int X, int Y)[] blocked, (int X, int Y)[] cover)
    {
        var s = Make(kind, new TileCoord(5, 5));
        foreach (var facing in Headings.All)
        {
            s.Facing = facing;
            var l = Layer(s);
            HashSet<Subtile> Turned((int X, int Y)[] cells) =>
                cells.Select(b => Footprints.Rotate(new Subtile(b.X, b.Y), (int)facing)).ToHashSet();
            var b = Turned(blocked);
            var cv = Turned(cover);
            var tw = Turned(TowersOf(kind));
            foreach (var c in Subtile.All())
                Assert.Equal(b.Contains(c) ? SubtileKind.Blocked : cv.Contains(c) ? SubtileKind.Cover
                    : tw.Contains(c) ? SubtileKind.Tower : SubtileKind.Open, l.KindAt(c));
            Assert.Equal(16 - blocked.Length, Count(l, c => l.CanStand(c, Enemy)));   // a tower takes anyone
            foreach (var t in tw)
            {
                Assert.True(l.CanStand(t, OwnerArcher));
                Assert.True(l.CanStand(t, Owner));
                Assert.Equal(1, l.ReachAt(t));
            }
        }
    }

    [Theory]
    [InlineData(1, 0, 2, 1)]    // slip to the east: water down the east edge, as drawn
    [InlineData(0, -1, 1, 1)]   // slip to the north: water along the north edge
    [InlineData(-1, 0, 1, 2)]   // slip to the west
    [InlineData(0, 1, 2, 2)]    // slip to the south
    public void TheDock_HasItsWaterOnTheSlipSide_AndNobodyStandsOnIt(int dx, int dy, int bx, int by)
    {
        var at = new TileCoord(5, 5);
        var l = Layer(new Dock(at, new TileCoord(at.X + dx, at.Y + dy)) { OwnerId = Blue });
        var edge = Headings.All.Single(h => h.Dx() == dx && h.Dy() == dy);
        foreach (var c in Subtile.All())
        {
            var expected = c.IsOnEdgeRow(edge) ? SubtileKind.Water
                : c == new Subtile(bx, by) ? SubtileKind.Blocked
                : SubtileKind.Open;
            Assert.Equal(expected, l.KindAt(c));
        }
        Assert.Equal(11, Count(l, c => l.CanStand(c, Owner)));
        Assert.Equal(11, Count(l, c => l.CanStand(c, Enemy)));
    }

    // World movement remembers only the edge a unit came onto a tile by, so each
    // edge of a footprint must lead into at most ONE area per side: two lanes of
    // one edge must never reach separate parts of the tile. True of every drawn
    // building; a new drawing that breaks it fails here (the canal's banks would,
    // which is why canal tiles are water to feet at world scale).
    [Theory]
    [MemberData(nameof(Drawings))]
    public void EveryDrawnFootprint_HasOneAreaPerEdgePerSide(StructureKind kind, (int X, int Y)[] blocked, (int X, int Y)[] cover)
    {
        _ = blocked; _ = cover;
        var s = Make(kind, new TileCoord(5, 5));
        foreach (var facing in Headings.All)
        {
            s.Facing = facing;
            AssertOneAreaPerEdge(Layer(s), kind.ToString());
        }
    }

    [Fact]
    public void TheCastleAndTheCampAlso_HaveOneAreaPerEdgePerSide()
    {
        foreach (var facing in Headings.All)
        {
            AssertOneAreaPerEdge(Layer(BlueCastle(facing)), "Castle");
            var camp = new BanditCamp(new TileCoord(5, 5)) { OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId, Facing = facing };
            AssertOneAreaPerEdge(Layer(camp), "BanditCamp");
        }
        var bridge = new Bridge(new TileCoord(5, 5)) { OwnerId = Blue };
        AssertOneAreaPerEdge(Footprints.For(bridge, new[] { Heading.North, Heading.South }), "Bridge (north-south)");
        AssertOneAreaPerEdge(Footprints.For(bridge, new[] { Heading.East, Heading.West }), "Bridge (east-west)");
    }

    private static void AssertOneAreaPerEdge(SubtileLayer l, string what)
    {
        foreach (var mover in new[] { Owner, Enemy })
            foreach (var e in Headings.All)
            {
                HashSet<Subtile>? area = null;
                for (var lane = 0; lane < Subtile.Size; lane++)
                {
                    var outside = Subtile.OutsideLane(e, lane);
                    var inside = outside.Step(e.Opposite());
                    if (!l.CanStep(outside, inside, mover)) continue;
                    var reach = Reach(l, mover, inside);
                    if (area is null) area = reach;
                    else Assert.True(area.SetEquals(reach), $"{what}: edge {e} leads into two areas ({(mover.Friendly ? "owner" : "enemy")})");
                }
            }
    }

    private static HashSet<Subtile> Reach(SubtileLayer l, BoardMover m, Subtile from)
    {
        var seen = new HashSet<Subtile> { from };
        var q = new Queue<Subtile>(seen);
        while (q.Count > 0)
        {
            var at = q.Dequeue();
            foreach (var h in Headings.All)
            {
                var n = at.Step(h);
                if (n.IsOnBoard && l.CanStep(at, n, m) && seen.Add(n)) q.Enqueue(n);
            }
        }
        return seen;
    }

    [Fact]
    public void NoStructure_KeepsPlainGround()
    {
        Assert.True(Footprints.For(null).IsOpen);
    }

    // ---- wall lines joined to their neighbours ---------------------------------------------

    private static HashSet<Subtile> WallCells(SubtileLayer l) =>
        Subtile.All().Where(c => l.KindAt(c) != SubtileKind.Open).ToHashSet();

    [Fact]
    public void AWallAtAnOuterCorner_TurnsNinetyDegrees_ClosedOnBothOuterSides()
    {
        // Joins east and south, facing north: the north-west corner of an enclosure.
        var wall = new Wall(new TileCoord(5, 5)) { OwnerId = Blue };
        var l = Footprints.For(wall, new[] { Heading.East, Heading.South });
        Assert.Equal(new HashSet<Subtile> { new(1, 1), new(2, 1), new(3, 1), new(1, 2), new(1, 3) }, WallCells(l));
        Assert.Equal(new[] { Heading.North, Heading.West }, l.ClosedSides(new Subtile(1, 1)));
        Assert.Equal(new[] { Heading.North }, l.ClosedSides(new Subtile(3, 1)));
        Assert.Equal(new[] { Heading.West }, l.ClosedSides(new Subtile(1, 3)));
        // Outside (north and west) and inside (south-east) are cut apart.
        Assert.Null(BattlePathing.FirstStepTo(l, Enemy, new Subtile(0, 0), new Subtile(2, 2)));
        Assert.Null(BattlePathing.FirstStepTo(l, Owner, new Subtile(2, 2), new Subtile(0, 0)));
    }

    [Fact]
    public void AWallAtAnInnerCorner_TurnsTheOtherWay_LeavingTheAttackerOneSubtile()
    {
        // Joins east and south, facing south: the enclosure is on the north-west.
        var wall = new Wall(new TileCoord(5, 5)) { OwnerId = Blue, Facing = Heading.South };
        var l = Footprints.For(wall, new[] { Heading.East, Heading.South });
        Assert.Equal(new HashSet<Subtile> { new(2, 2), new(3, 2), new(2, 3) }, WallCells(l));
        Assert.Empty(l.ClosedSides(new Subtile(2, 2)));             // the inside of the turn
        Assert.Equal(new[] { Heading.South }, l.ClosedSides(new Subtile(3, 2)));
        Assert.Equal(new[] { Heading.East }, l.ClosedSides(new Subtile(2, 3)));
        Assert.Null(BattlePathing.FirstStepTo(l, Enemy, new Subtile(3, 3), new Subtile(0, 0)));
    }

    [Fact]
    public void AGateOrTowerAtACorner_IsTheCornerSubtile()
    {
        var tower = new Tower(new TileCoord(5, 5)) { OwnerId = Blue };
        var l = Footprints.For(tower, new[] { Heading.East, Heading.South });
        Assert.Equal(SubtileKind.Tower, l.KindAt(new Subtile(1, 1)));
        Assert.Equal(1, Count(l, s => l.KindAt(s) == SubtileKind.Tower));
    }

    [Fact]
    public void InTheWorld_AWallTurningACorner_MeetsItsNeighboursAtTheSharedEdges()
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[] { new FactionStartSpec { OwnerId = Blue, CastlePosition = new TileCoord(1, 1) } },
        });
        // An enclosure's north-east corner: a run west–east, turning south.
        var west = world.AddStructure(new Wall(new TileCoord(5, 5)) { OwnerId = Blue });
        var turn = world.AddStructure(new Wall(new TileCoord(6, 5)) { OwnerId = Blue });
        var south = world.AddStructure(new Wall(new TileCoord(6, 6)) { OwnerId = Blue, Facing = Heading.East });
        world.AddStructure(new Wall(new TileCoord(7, 5)) { OwnerId = Red });   // another owner's: not joined

        Assert.Equal(new[] { Heading.East }, Footprints.JoinsOf(world, west));
        Assert.Equal(new[] { Heading.South, Heading.West }, Footprints.JoinsOf(world, turn));

        var a = Footprints.For(world, west.At);
        var b = Footprints.For(world, turn.At);
        var c = Footprints.For(world, south.At);
        Assert.Equal(SubtileKind.Wall, a.KindAt(new Subtile(3, 1)));   // west tile's east end…
        Assert.Equal(SubtileKind.Wall, b.KindAt(new Subtile(0, 1)));   // …meets the corner's west end
        Assert.Equal(SubtileKind.Wall, b.KindAt(new Subtile(2, 3)));   // corner's south end…
        Assert.Equal(SubtileKind.Wall, c.KindAt(new Subtile(2, 0)));   // …meets the south tile's north end
        Assert.Equal(new[] { Heading.North, Heading.East }, b.ClosedSides(new Subtile(2, 1)));
    }

    // ---- who stands where ------------------------------------------------------------

    // 2026-10-01 (user): the wall is its closed sides, not a rule about whose name is
    // on it. Anyone who is inside may climb it; the keep's two subtiles are nobody's.
    [Fact]
    public void OnACastle_BothSidesMayStandOn14_TheKeepOnNobody()
    {
        var l = Layer(BlueCastle());
        Assert.Equal(14, Count(l, s => l.CanStand(s, Owner)));
        Assert.Equal(14, Count(l, s => l.CanStand(s, Enemy)));
    }

    [Fact]
    public void AWall_IsSteppedOnFromInside_AndAlongIt_ButNeverThroughItsOuterSide_ByAnyone()
    {
        var l = Layer(BlueCastle());
        foreach (var mover in new[] { Owner, Enemy })
        {
            Assert.True(l.CanStep(new Subtile(1, 1), new Subtile(0, 1), mover));   // courtyard → west wall
            Assert.True(l.CanStep(new Subtile(0, 1), new Subtile(0, 2), mover));   // along the ring
            Assert.True(l.CanStep(new Subtile(0, 1), new Subtile(0, 0), mover));   // up into the corner tower
            Assert.False(l.CanStep(new Subtile(0, 1), new Subtile(-1, 1), mover)); // out through the outer side
            Assert.False(l.CanStep(new Subtile(-1, 1), new Subtile(0, 1), mover)); // or in through it
            Assert.False(l.CanStep(new Subtile(1, 2), new Subtile(1, 3), mover));  // nobody enters the keep
        }
        // From the rampart beside the gap, down into the gateway (a gatehouse's stair).
        Assert.True(l.CanStep(new Subtile(0, 0), new Subtile(1, 0), Owner));
    }

    [Fact]
    public void AnEnemy_ComesIntoTheCastleOnlyThroughTheGap_AndOnceInsideReachesTheWalls()
    {
        var l = Layer(BlueCastle());
        var fromNorth = BattlePathing.ReachableFrom(l, Enemy, Heading.North);
        Assert.Contains(new Subtile(1, 0), fromNorth);
        Assert.Contains(new Subtile(2, 2), fromNorth);
        Assert.Contains(new Subtile(0, 1), fromNorth);   // the west wall, from the courtyard
        Assert.Contains(new Subtile(3, 3), fromNorth);   // a corner tower
        Assert.Equal(14, fromNorth.Count);
        Assert.Empty(BattlePathing.ReachableFrom(l, Enemy, Heading.East));
        Assert.Empty(BattlePathing.ReachableFrom(l, Enemy, Heading.South));
    }

    [Fact]
    public void AStandaloneWall_NobodyCrosses_AnEnemyInsideMayClimbIt_AndAGateLetsOnlyTheOwnerThrough()
    {
        var wall = Layer(new Wall(new TileCoord(5, 5)) { OwnerId = Blue });
        Assert.Null(BattlePathing.FirstStepTo(wall, Owner, new Subtile(1, 2), new Subtile(1, 0)));
        Assert.Null(BattlePathing.FirstStepTo(wall, Enemy, new Subtile(1, 0), new Subtile(1, 2)));
        Assert.True(wall.CanStand(new Subtile(1, 1), Owner));
        Assert.True(wall.CanStand(new Subtile(1, 1), Enemy));
        Assert.Null(BattlePathing.FirstStepTo(wall, Enemy, new Subtile(1, 0), new Subtile(1, 1)));              // not from the outer side
        Assert.Equal(new Subtile(1, 1), BattlePathing.FirstStepTo(wall, Enemy, new Subtile(1, 2), new Subtile(1, 1)));   // from the defended side
        Assert.Null(BattlePathing.FirstStepTo(wall, Enemy, new Subtile(1, 2), new Subtile(1, 0)));              // and still never through

        var gate = Layer(new Gate(new TileCoord(5, 5)) { OwnerId = Blue });
        Assert.Equal(new Subtile(1, 1), BattlePathing.FirstStepTo(gate, Owner, new Subtile(1, 2), new Subtile(1, 0)));
        Assert.Equal(new Subtile(1, 1), BattlePathing.FirstStepTo(gate, Owner, new Subtile(1, 0), new Subtile(1, 2)));
        Assert.Null(BattlePathing.FirstStepTo(gate, Enemy, new Subtile(1, 0), new Subtile(1, 2)));
        Assert.False(gate.CanStand(new Subtile(1, 1), Enemy));   // a gate is a door; only the owner's side has the key
    }

    [Fact]
    public void ATower_TakesAnyoneWhoGetsToIt_OnlyArchersShootFromIt()
    {
        var l = Layer(new Tower(new TileCoord(5, 5)) { OwnerId = Blue });
        Assert.True(l.CanStand(new Subtile(1, 1), OwnerArcher));
        Assert.True(l.CanStand(new Subtile(1, 1), Owner));
        Assert.True(l.CanStand(new Subtile(1, 1), new BoardMover(Friendly: false, Archer: true)));
        Assert.Null(BattlePathing.FirstStepTo(l, Enemy, new Subtile(1, 0), new Subtile(1, 1)));   // its outer side is closed
    }

    [Fact]
    public void ACastleCornerTower_ReachesTheGapAndTheNearestCourtyardSubtile()
    {
        var l = Layer(BlueCastle());
        var reach = TurnResolver.InReach(l, new Subtile(0, 0), l.ReachAt(new Subtile(0, 0))).ToHashSet();
        Assert.Equal(new HashSet<Subtile> { new(1, 0), new(0, 1), new(2, 0), new(1, 1), new(0, 2) }, reach);
        var b = Board(l, Archer(1, Blue, 0, 0), Soldier(2, Red, 1, 1));
        Assert.Equal(2, TurnResolver.ArcherTarget(b, b.Get(1)!)!.Id);
    }

    [Fact]
    public void AnAttackerInside_ClimbsTheWall_AndDuelsTheDefenderOnIt()
    {
        var l = Layer(BlueCastle());
        var b = Board(l, Soldier(1, Blue, 0, 1), Soldier(2, Red, 1, 1));
        var steps = new Dictionary<int, PlannedStep> { [2] = new(new Subtile(0, 1)) };
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(new Subtile(0, 1), r.After.Get(2)!.At);
        Assert.Equal(2, r.Hits.Count);
        Assert.All(r.Hits, h => Assert.Equal(HitKind.Duel, h.Kind));
    }

    // ---- fighting from them -----------------------------------------------------------

    [Fact]
    public void ATowerArcher_ReachesTheTwoSubtilesBesideTheOneInFront_AWallArcherDoesNot()
    {
        var tower = Layer(new Tower(new TileCoord(5, 5)) { OwnerId = Blue });
        var b = Board(tower, Archer(1, Blue, 1, 1), Soldier(2, Red, 0, 0));
        Assert.Equal(2, TurnResolver.ArcherTarget(b, b.Get(1)!)!.Id);

        var wall = Layer(new Wall(new TileCoord(5, 5)) { OwnerId = Blue });
        var w = Board(wall, Archer(1, Blue, 1, 1), Soldier(2, Red, 0, 0));
        Assert.Null(TurnResolver.ArcherTarget(w, w.Get(1)!));
    }

    private static BoardUnit Raider(int id, int x, int y) =>
        Archer(id, Sim.Core.Bandits.BanditConstants.OwnerId, x, y);

    [Fact]
    public void ACampTowerArcher_ReachesEverySubtileWithinTwoMoves()
    {
        var camp = new BanditCamp(new TileCoord(5, 5)) { OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId };
        var l = Layer(camp);
        Assert.Equal(Footprints.CampTowerReach, l.ReachAt(new Subtile(3, 3)));
        Assert.Equal(1, l.ReachAt(new Subtile(2, 3)));
        var reach = TurnResolver.InReach(l, new Subtile(3, 3), l.ReachAt(new Subtile(3, 3)));
        Assert.Equal(new HashSet<Subtile> { new(3, 2), new(2, 3), new(3, 1), new(2, 2), new(1, 3) }, reach.ToHashSet());

        var b = Board(l, Raider(1, 3, 3), Soldier(2, Blue, 3, 1));          // two moves up
        Assert.Equal(2, TurnResolver.ArcherTarget(b, b.Get(1)!)!.Id);
        var far = Board(l, Raider(1, 3, 3), Soldier(2, Blue, 3, 0));        // three: out of reach
        Assert.Null(TurnResolver.ArcherTarget(far, far.Get(1)!));
    }

    [Fact]
    public void BlockedSubtiles_StopArrows()
    {
        var open = SubtileLayer.Build(null, Array.Empty<(Subtile, SubtileKind, IReadOnlyList<Heading>)>(),
            new[] { (new Subtile(0, 0), 2) });
        var walled = SubtileLayer.Build(null,
            new[] { (new Subtile(1, 0), SubtileKind.Blocked, (IReadOnlyList<Heading>)Array.Empty<Heading>()) },
            new[] { (new Subtile(0, 0), 2) });
        var clear = Board(open, Archer(1, Red, 0, 0), Soldier(2, Blue, 2, 0));
        Assert.Equal(2, TurnResolver.ArcherTarget(clear, clear.Get(1)!)!.Id);
        var behind = Board(walled, Archer(1, Red, 0, 0), Soldier(2, Blue, 2, 0));
        Assert.Null(TurnResolver.ArcherTarget(behind, behind.Get(1)!));   // the only 2-move way is through the block
    }

    [Fact]
    public void WithFartherReach_EqualTargetsGoToTheNearer()
    {
        var l = SubtileLayer.Build(null, Array.Empty<(Subtile, SubtileKind, IReadOnlyList<Heading>)>(),
            new[] { (new Subtile(1, 1), 2) });
        var b = Board(l, Archer(1, Red, 1, 1), Soldier(2, Blue, 1, 3), Soldier(3, Blue, 2, 1));
        Assert.Equal(3, TurnResolver.ArcherTarget(b, b.Get(1)!)!.Id);
    }

    [Fact]
    public void AnAttackerCannotReachADefenderOnTheWall_OnlyArrowsCan()
    {
        var l = Layer(new Wall(new TileCoord(5, 5)) { OwnerId = Blue });
        var b = Board(l, Soldier(1, Blue, 1, 1), Soldier(2, Red, 1, 0));
        var steps = new Dictionary<int, PlannedStep> { [2] = new(new Subtile(1, 1)) };
        var r = TurnResolver.Resolve(b, steps, Cfg);
        Assert.Equal(new Subtile(1, 0), r.After.Get(2)!.At);
        Assert.Equal(StepNote.NoWay, r.Failed[2]);
        Assert.Empty(r.Hits);
    }

    [Fact]
    public void Planner_AnAdvancingEnemyDoesNotChaseADefenderItCannotReach()
    {
        var l = Layer(new Wall(new TileCoord(5, 5)) { OwnerId = Blue });
        var b = Board(l, Soldier(1, Blue, 1, 1), Soldier(2, Red, 1, 0));
        var plan = TurnPlanner.Plan(b, b.Get(2)!, null, BattleDoctrine.Advance, BoardSurroundings.Anywhere);
        Assert.Null(plan.To);
    }

    [Fact]
    public void Planner_AnEnemyWaitingOutsideAClosedLane_CannotComeOn()
    {
        var l = Layer(BlueCastle());
        var b = Board(l, Soldier(2, Red, 4, 1));
        var plan = TurnPlanner.Plan(b, b.Get(2)!, null, BattleDoctrine.Hold, BoardSurroundings.Anywhere);
        Assert.Null(plan.To);
        Assert.Equal(StepNote.NoWay, plan.Note);
    }

    // ---- in the world ------------------------------------------------------------------

    private static readonly TileCoord Keep = new(10, 10);

    private static Simulation CastleWorld(TileCoord redFrom, out Unit blue, out Unit red)
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        blue = new Unit(1, Keep) { Role = UnitRole.Soldier, OwnerId = Blue };
        red = new Unit(2, redFrom) { Role = UnitRole.Soldier, OwnerId = Red };
        world.AddUnit(blue);
        world.AddUnit(red);
        var sim = new Simulation(world, seed: 5);
        sim.SubmitIntent(0, new MoveIntent(2, Keep) { PlayerId = Red });
        return sim;
    }

    private static void RunToBattle(Simulation sim)
    {
        for (var t = 1; t <= 400 && sim.World.Battlefields.Count == 0; t++) sim.Run(until: t);
    }

    [Fact]
    public void AtACastle_TheDefenderStandsInTheCourtyard_AndAnAttackerFromTheGateSideComesIn()
    {
        var sim = CastleWorld(new TileCoord(Keep.X, Keep.Y - 1), out var blue, out var red);   // from the north: the gate
        RunToBattle(sim);
        Assert.True(sim.World.Battlefields.ContainsKey(Keep));
        var layer = Sim.Core.Battlefields.Battlefields.LayerFor(sim.World, Keep);
        Assert.Equal(SubtileKind.Open, layer.KindAt(blue.Subtile!.Value));
        Assert.NotNull(red.Board);
        Assert.True(layer.CanStand(red.Subtile!.Value, Enemy));
    }

    [Fact]
    public void AtACastle_AnAttackerFromAWalledSide_NeverComesInThroughTheWall()
    {
        // World movement follows the castle's shape: from the east it goes round
        // to the north gate (if it can see the castle) or is stopped at the wall
        // (if it can't). It never arrives across the east edge.
        var sim = CastleWorld(new TileCoord(Keep.X + 1, Keep.Y), out _, out var red);
        sim.Run(until: 3000);
        Assert.NotEqual(new TileCoord(Keep.X + 1, Keep.Y), red.Position == Keep ? red.EnteredFrom : null);
    }

    [Fact]
    public void Facing_SurvivesASnapshot()
    {
        var sim = CastleWorld(new TileCoord(Keep.X, Keep.Y - 1), out _, out _);
        sim.World.Structures[Keep].Facing = Heading.West;
        var copy = Snapshot.Restore(Snapshot.Serialize(sim), seed: 5);
        Assert.Equal(Heading.West, copy.World.Structures[Keep].Facing);
    }
}
