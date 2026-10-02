using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// World movement follows the tiles' shapes (docs/structure-footprints.md,
// "World movement"): a castle is entered and left only through its gate.
public class CrossingTests
{
    private const int Blue = 0, Red = 1;
    private static readonly TileCoord Keep = new(10, 10);

    private static GameWorld World()
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
        return world;   // the castle faces North: its gate is on the north edge
    }

    private static List<TileCoord>? Plan(GameWorld w, int owner, TileCoord from, TileCoord to, TileCoord? cameFrom = null) =>
        Pathfinding.FindPath(w.Grid, from, to,
            (a, b) => MovementCost.ExecutionCost(w, a, b, 0),
            CrossingRule.GroundTruth(w, owner),
            CrossingRule.EntryEdge(from, cameFrom));

    [Fact]
    public void TheCastleCrossings_OnlyTheGateEdgeLetsAnEnemyIn()
    {
        Assert.True(Crossings.Of(SubtileLayer.Open).IsAnywhere);
        var castle = Crossings.Of(Footprints.For(World().Structures[Keep]));
        Assert.True(castle.CanStay(false, Heading.North));
        Assert.False(castle.CanStay(false, Heading.East));
        Assert.False(castle.CanPass(false, Heading.North, Heading.South));   // no way through
        Assert.True(castle.CanPass(false, Heading.North, Heading.North));    // back out by the gate
        Assert.True(castle.CanPass(true, null, Heading.North));              // born inside: out by the gate
        Assert.False(castle.CanPass(true, null, Heading.East));
    }

    [Fact]
    public void WalkingIntoTheCastle_GoesRoundToTheGate()
    {
        var w = World();
        var path = Plan(w, Red, new TileCoord(Keep.X + 2, Keep.Y), Keep);
        Assert.NotNull(path);
        Assert.Equal(new TileCoord(Keep.X, Keep.Y - 1), path![^2]);          // the last step comes from the north
    }

    [Fact]
    public void LeavingTheCastle_GoesOutByTheGate()
    {
        var w = World();
        var path = Plan(w, Blue, Keep, new TileCoord(Keep.X + 2, Keep.Y), cameFrom: new TileCoord(Keep.X, Keep.Y - 1));
        Assert.NotNull(path);
        Assert.Equal(new TileCoord(Keep.X, Keep.Y - 1), path![1]);           // out through the north gate first
    }

    [Fact]
    public void AMoveIntoAnEnemyCastleFromAWalledSide_StopsAtTheWall()
    {
        var w = World();
        // Red can't see the castle from here (fog): it plans straight in from the east…
        var red = new Unit(2, new TileCoord(Keep.X + 1, Keep.Y)) { Role = UnitRole.Soldier, OwnerId = Red };
        w.AddUnit(red);
        var sim = new Simulation(w, seed: 3);
        sim.SubmitIntent(0, new MoveIntent(2, Keep) { PlayerId = Red });
        sim.Run(until: 2000);
        // …and either the plan went round by the gate (it could see) or the hop
        // check stopped it at the wall. Never through a wall.
        if (red.Position == Keep)
            Assert.Equal(new TileCoord(Keep.X, Keep.Y - 1), red.EnteredFrom);
        else
            Assert.Equal(new TileCoord(Keep.X + 1, Keep.Y), red.Position);
    }

    // ---- feet stay off water -----------------------------------------------------------

    // A 3×3 lake centred on (13, 10), in open grass.
    private static GameWorld WithLake()
    {
        var w = World();
        for (var y = 9; y <= 11; y++)
            for (var x = 12; x <= 14; x++)
                w.Grid.SetBiome(new TileCoord(x, y), Biome.Water);
        return w;
    }

    [Fact]
    public void AMoveAimedAtWater_EndsOnTheNearestLand_WithoutEverSteppingOnWater()
    {
        var w = WithLake();
        var goal = new TileCoord(13, 10);
        var walker = new Unit(3, new TileCoord(6, 10)) { Role = UnitRole.Farmer, OwnerId = Blue };
        w.AddUnit(walker);
        var sim = new Simulation(w, seed: 4);
        var visited = new List<TileCoord>();
        sim.SubmitIntent(0, new MoveIntent(3, goal) { PlayerId = Blue });
        for (var t = 1; t <= 5000; t += 5) { sim.Run(until: t); visited.Add(walker.Position); }

        Assert.All(visited, p => Assert.NotEqual(Biome.Water, w.Grid.BiomeAt(p)));
        Assert.Equal(2, Math.Abs(walker.Position.X - goal.X) + Math.Abs(walker.Position.Y - goal.Y));   // the nearest land ring
        Assert.Equal(new TileCoord(13, 8), walker.Position);                                              // nearest, then north first
    }

    [Fact]
    public void APathAcrossALake_GoesRoundIt()
    {
        var w = WithLake();
        var path = Pathfinding.FindPath(w.Grid, new TileCoord(11, 10), new TileCoord(15, 10),
            (a, b) => MovementCost.ExecutionCost(w, a, b, 0));
        Assert.NotNull(path);
        Assert.DoesNotContain(path!, p => w.Grid.BiomeAt(p) == Biome.Water);
    }

    // ---- canals: follow them, never cross them --------------------------------------------

    // A north–south canal at x = 10 from y = 4 (dug from a lake tile at (10, 3))
    // down to y = 14, as a finished canal leaves it in a grid world: Water with a
    // Canal structure on each tile.
    private static GameWorld WithCanal()
    {
        var w = World();
        w.Grid.SetBiome(new TileCoord(10, 3), Biome.Water);
        for (var y = 4; y <= 14; y++)
        {
            var t = new TileCoord(10, y);
            if (w.Structures.ContainsKey(t)) w.Structures.Remove(t);   // the castle stands in the way
            w.Grid.SetBiome(t, Biome.Water);
            w.AddStructure(new Canal(t) { OwnerId = Blue });
        }
        return w;
    }

    [Fact]
    public void AStraightCanal_HasBanksOnTheBoard_ButFeetDontEnterIt()
    {
        var w = WithCanal();
        var mid = new TileCoord(10, 8);
        Assert.Equal(new[] { Heading.North, Heading.South }, Footprints.JoinsOf(w, w.Structures[mid]));
        var board = Footprints.For(w, mid);
        Assert.Equal(SubtileKind.Water, board.KindAt(new Subtile(1, 0)));
        Assert.Equal(SubtileKind.Open, board.KindAt(new Subtile(0, 2)));   // a bank
        Assert.Equal(Biomes.Impassable, MovementCost.ExecutionCost(w, new TileCoord(9, 8), mid, 0));
    }

    [Fact]
    public void APathFromBankToBank_GoesRoundTheCanalsEnd()
    {
        var w = WithCanal();
        var path = Plan(w, Red, new TileCoord(9, 8), new TileCoord(11, 8));
        Assert.NotNull(path);
        Assert.DoesNotContain(path!, t => MovementCost.IsCanal(w, t) || w.Grid.BiomeAt(t) == Biome.Water);
        Assert.Contains(path!, t => t.Y > 14 || t.Y < 3);   // round the far end, or round the lake
    }

    [Fact]
    public void FinishingACanal_InAGridWorld_LeavesCanalTiles()
    {
        var w = new GameWorld(new TileGrid(12, 12, Biome.Grassland));
        w.RestoreCombatConfig(new CombatConfig(60));
        w.Grid.SetBiome(new TileCoord(5, 0), Biome.Water);
        var sim = new Simulation(w, seed: 1);
        var path = new List<TileCoord> { new(5, 1), new(5, 2), new(5, 3) };
        var outcome = new Sim.Core.Canals.PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        var site = (ConstructionSite)w.Structures[path[0]];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 1; i <= site.RequiredBuilderCount; i++)
        {
            var u = new Unit(i, path[0]) { Role = UnitRole.Builder };
            w.AddUnit(u);
            u.TrySetActivity(Activity.Building, path[0]);
        }
        site.StartOrResume(sim);
        sim.Run();
        foreach (var t in path)
        {
            Assert.Equal(Biome.Water, w.Grid.BiomeAt(t));
            Assert.Equal(StructureKind.Canal, w.Structures[t].Kind);
        }
        Assert.Equal(new[] { Heading.North, Heading.South }, Footprints.JoinsOf(w, w.Structures[path[0]]));
    }

    // ---- bridges: the way over a canal -------------------------------------------------------

    private static Simulation Bridged(out GameWorld w, TileCoord at)
    {
        w = WithCanal();
        var sim = new Simulation(w, seed: 2);
        var outcome = new PlaceSiteIntent(at, StructureKind.Bridge) { PlayerId = Blue }.Resolve(sim);
        Assert.True(outcome.IsApplied, outcome.ToString());
        return sim;
    }

    [Fact]
    public void ABridge_IsBuiltOnAStraightCanal_AndItsDeckJoinsTheBanks()
    {
        var at = new TileCoord(10, 8);
        var sim = Bridged(out var w, at);
        var site = Assert.IsType<ConstructionSite>(w.Structures[at]);
        Assert.Equal(StructureKind.Bridge, site.TargetKind);
        // The scaffolding is already a deck: its builders can walk on.
        Assert.True(MovementCost.ExecutionCost(w, new TileCoord(9, 8), at, 0) < Biomes.Impassable);

        Construction.Complete(sim, site);
        Assert.IsType<Bridge>(w.Structures[at]);
        var l = Footprints.For(w, at);
        Assert.Equal(SubtileKind.Bridge, l.KindAt(new Subtile(1, 1)));
        Assert.Equal(SubtileKind.Bridge, l.KindAt(new Subtile(2, 1)));
        Assert.Equal(SubtileKind.Water, l.KindAt(new Subtile(1, 2)));
        var c = Crossings.Of(l);
        Assert.True(c.CanPass(false, Heading.West, Heading.East));
        Assert.True(c.CanPass(false, Heading.North, Heading.South));

        // Bank to bank now goes over it, not round the canal's end.
        var path = Plan(w, Red, new TileCoord(9, 8), new TileCoord(11, 8));
        Assert.Equal(new[] { new TileCoord(9, 8), at, new TileCoord(11, 8) }, path);
        // Boats still pass under.
        Assert.True(MovementCost.ExecutionCost(w, new TileCoord(10, 7), at, 0, Traversal.Water) < Biomes.Impassable);
    }

    [Fact]
    public void OnAnEastWestCanal_TheDeckTurns()
    {
        var l = Footprints.For(new Bridge(new TileCoord(5, 5)) { OwnerId = Blue }, new[] { Heading.East, Heading.West });
        Assert.Equal(SubtileKind.Bridge, l.KindAt(new Subtile(2, 1)));
        Assert.Equal(SubtileKind.Bridge, l.KindAt(new Subtile(2, 2)));
        Assert.Equal(SubtileKind.Water, l.KindAt(new Subtile(1, 1)));
        Assert.True(Crossings.Of(l).CanPass(false, Heading.North, Heading.South));
    }

    [Fact]
    public void ABridge_NeedsAStraightCanalOfYourOwn()
    {
        var w = WithCanal();
        var sim = new Simulation(w, seed: 2);
        // Not a canal.
        Assert.True(new PlaceSiteIntent(new TileCoord(3, 3), StructureKind.Bridge) { PlayerId = Blue }.Resolve(sim).IsRejected);
        // Someone else's canal.
        Assert.True(new PlaceSiteIntent(new TileCoord(10, 8), StructureKind.Bridge) { PlayerId = Red }.Resolve(sim).IsRejected);
        // The canal's dead end joins only one side: not straight.
        Assert.True(new PlaceSiteIntent(new TileCoord(10, 14), StructureKind.Bridge) { PlayerId = Blue }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void ARazedBridge_GoesBackToCanal_NotRubble()
    {
        var at = new TileCoord(10, 8);
        var sim = Bridged(out var w, at);
        Construction.Complete(sim, (ConstructionSite)w.Structures[at]);
        Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, w.Structures[at]);
        Assert.Equal(StructureKind.Canal, w.Structures[at].Kind);
        Assert.Equal(Biomes.Impassable, MovementCost.ExecutionCost(w, new TileCoord(9, 8), at, 0));
    }
}
