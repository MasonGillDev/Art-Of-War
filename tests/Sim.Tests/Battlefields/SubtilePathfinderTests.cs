using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests.Battlefields;

// M43 step 1 (docs/m43-status.md): the subtile pathfinder. A tile route shaped by the
// crossing masks, then A* over the route's tiles and a one-tile margin, the whole grid
// if that finds nothing. Every step is one the walk's own rules accept.
public class SubtilePathfinderTests
{
    private const int Blue = 0, Red = 1;
    private static readonly TileCoord Keep = new(10, 10);

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
        return w;   // Blue's castle faces North: its gate is on the north edge
    }

    private static WorldSubtile W(int tx, int ty, int sx, int sy) => WorldSubtile.Of(new TileCoord(tx, ty), new Subtile(sx, sy));
    private static StepMover Foot(int owner) => new(owner, Traversal.Foot, false);

    private static int Cost(GameWorld w, StepMover m, WorldSubtile from, List<WorldSubtile> path)
    {
        var total = 0;
        var at = from;
        foreach (var s in path) { total += SubtileStepRules.StepCost(w, m.Traversal, at, s, 0); at = s; }
        return total;
    }

    // Every step 4-adjacent and accepted by the ground-truth rules.
    private static void AssertWalkable(GameWorld w, StepMover m, WorldSubtile from, List<WorldSubtile> path)
    {
        var rules = new SubtileStepRules(w, m, null);
        var at = from;
        foreach (var s in path)
        {
            Assert.True(at.IsAdjacentTo(s), $"{at} → {s} isn't one step");
            Assert.Null(rules.Problem(at, s));
            at = s;
        }
    }

    // ---- open ground ---------------------------------------------------------------------

    [Fact]
    public void OnOpenGround_TheShortestWalk_ThroughTheCorridor()
    {
        var w = World();
        var from = W(3, 3, 1, 1);
        var to = W(7, 5, 2, 2);
        var stats = new SubtilePathStats();
        var path = SubtilePathfinder.Find(w, Foot(Blue), from, to, stats: stats);
        Assert.NotNull(path);
        Assert.Equal(to, path![^1]);
        Assert.Equal(Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y), path.Count);   // Manhattan on open ground
        Assert.True(stats.UsedCorridor);
        Assert.False(stats.FellBack);
        AssertWalkable(w, Foot(Blue), from, path);
    }

    [Fact]
    public void AStartThatIsTheGoal_IsAnEmptyPath()
    {
        var w = World();
        var s = W(3, 3, 1, 1);
        Assert.Empty(SubtilePathfinder.Find(w, Foot(Blue), s, s)!);
    }

    [Fact]
    public void TheCorridor_CostsAsMuchAsTheWholeGrid_OnOpenGround()
    {
        var w = World();
        var rng = new Random(3);
        for (var i = 0; i < 20; i++)
        {
            var a = W(rng.Next(1, 8), rng.Next(1, 8), rng.Next(4), rng.Next(4));
            var b = W(rng.Next(1, 8), rng.Next(1, 8), rng.Next(4), rng.Next(4));
            var m = Foot(Blue);
            var viaCorridor = SubtilePathfinder.Find(w, m, a, b)!;
            var whole = SubtilePathfinder.Find(w, m, a, b, useCorridor: false)!;
            Assert.Equal(Cost(w, m, a, whole), Cost(w, m, a, viaCorridor));
        }
    }

    [Fact]
    public void AGoalOffTheMap_OrOnWater_IsRefused()
    {
        var w = World();
        w.Grid.SetBiome(new TileCoord(6, 6), Biome.Water);
        Assert.Null(SubtilePathfinder.Find(w, Foot(Blue), W(3, 3, 0, 0), W(6, 6, 1, 1)));
        Assert.Null(SubtilePathfinder.Find(w, Foot(Blue), W(3, 3, 0, 0), new WorldSubtile(-1, 5)));
    }

    // ---- shapes: walls, castles, bridges ---------------------------------------------------

    [Fact]
    public void AWallLine_IsWalkedRoundItsEnd_ByAnEnemy_AndThroughByItsOwner()
    {
        var w = World();
        for (var x = 6; x <= 12; x++) w.AddStructure(new Wall(new TileCoord(x, 6)) { OwnerId = Blue });
        var from = W(9, 3, 1, 1);
        var to = W(9, 9, 1, 1);

        var enemy = SubtilePathfinder.Find(w, Foot(Red), from, to);
        Assert.NotNull(enemy);
        Assert.DoesNotContain(enemy!, s => s.Tile.Y == 6 && s.Tile.X is >= 6 and <= 12);   // never onto the line
        AssertWalkable(w, Foot(Red), from, enemy);
        Assert.True(enemy.Count > Math.Abs(from.Y - to.Y));                                  // the detour

        var owner = SubtilePathfinder.Find(w, Foot(Blue), from, to);
        Assert.NotNull(owner);
        AssertWalkable(w, Foot(Blue), from, owner!);
        Assert.True(owner.Count <= enemy.Count);                                             // a wall's outer side is shut to its owner too
    }

    [Fact]
    public void AnEnemyReachesTheCourtyard_OnlyByTheGate()
    {
        var w = World();
        var from = W(10, 6, 1, 1);
        var courtyard = W(Keep.X, Keep.Y, 1, 1);
        var path = SubtilePathfinder.Find(w, Foot(Red), from, courtyard);
        Assert.NotNull(path);
        AssertWalkable(w, Foot(Red), from, path!);
        // The gap is on the north edge of the castle tile, subtile (1, 0).
        var gap = WorldSubtile.Of(Keep, new Subtile(1, 0));
        Assert.Contains(gap, path);
        Assert.True(path.IndexOf(gap) >= path.Count - 3);   // in by the gap, then the courtyard

        // Its own side stays out of the way of nothing: the owner walks in the same way, or
        // through the wall (friendly); either way the path is valid.
        var own = SubtilePathfinder.Find(w, Foot(Blue), from, courtyard);
        Assert.NotNull(own);
        AssertWalkable(w, Foot(Blue), from, own!);
        Assert.True(own.Count <= path.Count);
    }

    // 2026-10-01: anyone may stand on a wall it can reach through an open side, so an enemy's
    // way onto the castle's corner tower is in by the gap and up from the courtyard — never
    // over the wall.
    [Fact]
    public void AGoalOnACastleWall_IsReachedByAnEnemy_OnlyThroughTheGap()
    {
        var w = World();
        var ring = WorldSubtile.Of(Keep, new Subtile(0, 0));   // a corner of the castle wall
        var path = SubtilePathfinder.Find(w, Foot(Red), W(8, 8, 1, 1), ring);
        Assert.NotNull(path);
        AssertWalkable(w, Foot(Red), W(8, 8, 1, 1), path!);
        Assert.Contains(WorldSubtile.Of(Keep, new Subtile(1, 0)), path);
        Assert.NotNull(SubtilePathfinder.Find(w, Foot(Blue), W(8, 8, 1, 1), ring));
    }

    private static GameWorld WithCanal(bool bridge)
    {
        var w = World();
        w.Grid.SetBiome(new TileCoord(10, 3), Biome.Water);
        for (var y = 4; y <= 14; y++)
        {
            var t = new TileCoord(10, y);
            if (w.Structures.ContainsKey(t)) w.Structures.Remove(t);
            w.Grid.SetBiome(t, Biome.Water);
            w.AddStructure(new Canal(t) { OwnerId = Blue });
        }
        if (bridge)
        {
            var at = new TileCoord(10, 8);
            w.Structures.Remove(at);
            w.AddStructure(new Bridge(at) { OwnerId = Blue });
        }
        return w;
    }

    [Fact]
    public void ACanal_IsCrossedOnlyAtABridge()
    {
        var from = W(9, 8, 3, 1);
        var to = W(11, 8, 0, 1);

        var without = WithCanal(bridge: false);
        var round = SubtilePathfinder.Find(without, Foot(Red), from, to);
        Assert.NotNull(round);                                                     // round the canal's end
        Assert.DoesNotContain(round!, s => s.Tile.X == 10 && s.Tile.Y is >= 4 and <= 14);
        Assert.True(round.Count > 10);

        var with = WithCanal(bridge: true);
        var over = SubtilePathfinder.Find(with, Foot(Red), from, to);
        Assert.NotNull(over);
        AssertWalkable(with, Foot(Red), from, over!);
        Assert.True(over.Any(s => s.Tile == new TileCoord(10, 8)), string.Join(' ', over.Select(x => x.ToString())));   // over the deck
        Assert.True(over.Count < round.Count);
    }

    [Fact]
    public void FeetNeverPlanOntoWater_ButBoatsStayOnIt()
    {
        var w = WithCanal(bridge: false);
        var walk = SubtilePathfinder.Find(w, Foot(Red), W(9, 5, 1, 1), W(11, 12, 1, 1))!;
        Assert.DoesNotContain(walk, s => w.Grid.BiomeAt(s.Tile) == Biome.Water);
        var boat = SubtilePathfinder.Find(w, new StepMover(Blue, Traversal.Water, false), W(10, 5, 1, 1), W(10, 12, 1, 1));
        Assert.NotNull(boat);
        Assert.All(boat!, s => Assert.Equal(Biome.Water, w.Grid.BiomeAt(s.Tile)));
    }

    // ---- fog, rivers, cost -------------------------------------------------------------------

    [Fact]
    public void AWallInTheFog_PlansAsOpenGround_TheGroundTruthDoesNot()
    {
        var w = World();
        for (var x = 6; x <= 12; x++) w.AddStructure(new Wall(new TileCoord(x, 6)) { OwnerId = Blue });
        var from = W(9, 3, 1, 1);
        var to = W(9, 9, 1, 1);
        var m = Foot(Red);

        var planned = SubtilePathfinder.Find(w, m, from, to, visible: new HashSet<TileCoord>())!;
        Assert.Equal(Math.Abs(from.Y - to.Y), planned.Count);                                  // straight through
        Assert.Contains(planned, s => s.Tile.Y == 6);
        // The walk's rules stop it at the wall: that is the per-step ground-truth check.
        var rules = new SubtileStepRules(w, m, null);
        var at = from;
        var stopped = false;
        foreach (var s in planned)
        {
            if (rules.Problem(at, s) is not null) { stopped = true; break; }
            at = s;
        }
        Assert.True(stopped);

        // Once the wall is seen it plans round.
        var seen = new HashSet<TileCoord>(Enumerable.Range(6, 7).Select(x => new TileCoord(x, 6)));
        var round = SubtilePathfinder.Find(w, m, from, to, visible: seen)!;
        Assert.True(round.Count > planned.Count);
        AssertWalkable(w, m, from, round);
    }

    [Fact]
    public void ARiverEdge_CostsTheFord_AndThePathPaysItAtMostOnceWhenItMustCross()
    {
        var w = World();
        var a = new TileCoord(5, 5);
        var b = new TileCoord(6, 5);
        var ford = Sim.Core.Rivers.River.CrossingCostFor(w.Grid, a, b);
        var step = (int)SubtileStepRules.StepTicks(w, b);
        Assert.Equal(step + ford, SubtileStepRules.StepCost(w, Traversal.Foot, WorldSubtile.Of(a, new Subtile(3, 1)), WorldSubtile.Of(b, new Subtile(0, 1)), 0));
        Assert.Equal(step, SubtileStepRules.StepCost(w, Traversal.Foot, WorldSubtile.Of(b, new Subtile(0, 1)), WorldSubtile.Of(b, new Subtile(1, 1)), 0));
    }

    // ---- the contract ---------------------------------------------------------------------------

    [Fact]
    public void ASearch_IsAPureRead_AndTheSameQueryGivesTheSamePath()
    {
        var w = World();
        for (var x = 6; x <= 12; x++) w.AddStructure(new Wall(new TileCoord(x, 6)) { OwnerId = Blue });
        var sim = new Simulation(w, seed: 1);
        var before = Snapshot.Hash(sim);
        var first = SubtilePathfinder.Find(w, Foot(Red), W(9, 3, 1, 1), W(9, 9, 1, 1))!;
        for (var i = 0; i < 100; i++)
            Assert.Equal(first, SubtilePathfinder.Find(w, Foot(Red), W(9, 3, 1, 1), W(9, 9, 1, 1)));
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
