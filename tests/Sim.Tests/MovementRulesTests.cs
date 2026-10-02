using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M43 (docs/subtile-movement.md, "all movement on subtiles"; docs/m43-status.md): the ONE
// movement. A tile order is a walk of subtile steps to the free subtile nearest the tile's
// centre; friends pass through each other; only stopping needs room; a hostile force stops a
// walk by opening a board; a hidden wall stops it where it stands. These replace the M2 to M5
// crowding, cap and group-hop tests (there is no crowding price and no flat cap now).
public class MovementRulesTests
{
    private const int Blue = 0, Red = 1;

    private static (Simulation sim, GameWorld world) World(int w = 9, int h = 1, bool enemies = false)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland), new DiplomacyConfig(), new CombatConfig(RoundIntervalTicks: 60));
        world.Players[Blue] = new Player(Blue);
        world.Players[Red] = new Player(Red);
        if (enemies) world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return (new Simulation(world, seed: 3), world);
    }

    private static Unit Add(GameWorld w, int id, TileCoord at, int owner = Blue, UnitRole role = UnitRole.Farmer)
    {
        var u = new Unit(id, at) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        return u;
    }

    private static void Go(Simulation sim, int id, TileCoord to, int owner = Blue) =>
        sim.SubmitIntent(sim.Now, new MoveIntent(id, to) { PlayerId = owner });

    // ---- reachability (step 6: drivers ask before they order) -----------------------------------

    [Fact]
    public void CanReach_IsTrueOverLand_AndFalseAcrossOpenWater()
    {
        var (sim, w) = World(9, 3);
        for (var y = 0; y < 3; y++) w.Grid.SetBiome(new TileCoord(4, y), Biome.Water);   // a sea cuts the map
        var u = Add(w, 1, new TileCoord(0, 1));
        sim.Run(until: 1);
        Assert.True(Walk.CanReach(w, u, new TileCoord(3, 1)));
        Assert.False(Walk.CanReach(w, u, new TileCoord(7, 1)));                            // the far shore
        Assert.True(Walk.CanReach(w, u, u.Position));
        var before = Snapshot.Hash(sim);
        Walk.CanReach(w, u, new TileCoord(7, 1));
        Assert.Equal(before, Snapshot.Hash(sim));                                          // a pure read
    }

    // ---- where a tile order ends -----------------------------------------------------------

    [Fact]
    public void ATileOrder_EndsOnTheFreeSubtileNearestTheTilesCentre()
    {
        var (sim, w) = World();
        var u = Add(w, 1, new TileCoord(0, 0));
        Go(sim, 1, new TileCoord(4, 0));
        sim.Run();
        Assert.Equal(new TileCoord(4, 0), u.Position);
        Assert.Equal(new Subtile(1, 1), u.Subtile);                 // the first of the four centre subtiles
        Assert.False(u.IsWalking);
        Assert.Null(u.PathFinalDest);
    }

    [Fact]
    public void ManyUnitsSentToOneTile_EndOnSubtilesOfTheirOwn_NearestTheCentreFirst()
    {
        var (sim, w) = World(3, 1);
        var ids = Enumerable.Range(1, 6).ToArray();
        foreach (var id in ids) Add(w, id, new TileCoord(0, 0));
        foreach (var id in ids) Go(sim, id, new TileCoord(2, 0));
        sim.Run();
        var spots = ids.Select(id => w.Units[id].Subtile!.Value).ToList();
        Assert.All(ids, id => Assert.Equal(new TileCoord(2, 0), w.Units[id].Position));
        Assert.Equal(ids.Length, spots.Distinct().Count());        // nobody shares while standing
        // The four centre subtiles are taken before any edge subtile.
        var centre = new[] { new Subtile(1, 1), new Subtile(2, 1), new Subtile(1, 2), new Subtile(2, 2) };
        Assert.Equal(4, spots.Count(s => centre.Contains(s)));
    }

    [Fact]
    public void MoreUnitsThanASideHasRoomFor_SpillOntoTheTileBeside()
    {
        var (sim, w) = World(3, 1);
        var count = Subtile.Count + 4;
        for (var id = 1; id <= count; id++) Add(w, id, new TileCoord(id % 2, 0));   // no tile starts over its room
        for (var id = 1; id <= count; id++) Go(sim, id, new TileCoord(2, 0));
        sim.Run();
        // Nobody is stuck or dropped: every unit stands somewhere, nobody shares a subtile.
        var standing = w.Units.Values.Where(u => !u.IsWalking).ToList();
        Assert.Equal(count, standing.Count);
        Assert.Equal(count, standing.Select(u => (u.Position, u.Subtile)).Distinct().Count());
        Assert.True(w.Units.Values.Count(u => u.Position == new TileCoord(2, 0)) <= Subtile.Count);
    }

    [Fact]
    public void AnOrderToTheTileTheUnitIsOn_IsNotAWalk()
    {
        var (sim, w) = World();
        var u = Add(w, 1, new TileCoord(3, 0));
        var stood = u.Subtile;
        Go(sim, 1, new TileCoord(3, 0));
        sim.Run(until: 5);
        Assert.False(u.IsWalking);
        Assert.Equal(stood, u.Subtile);
    }

    // ---- friends pass through each other -----------------------------------------------------

    [Fact]
    public void UnitsWalkingOppositeWays_PassThroughEachOther_AndBothArrive()
    {
        var (sim, w) = World();
        var a = Add(w, 1, new TileCoord(0, 0));
        var b = Add(w, 2, new TileCoord(8, 0));
        Go(sim, 1, new TileCoord(8, 0));
        Go(sim, 2, new TileCoord(0, 0));
        var shared = false;
        sim.Run(until: 1);
        for (var t = 2L; (a.IsWalking || b.IsWalking) && t < 5000; t++)
        {
            sim.Run(until: t);
            if (a.Position == b.Position && a.Subtile == b.Subtile) shared = true;
        }
        Assert.Equal(new TileCoord(8, 0), a.Position);
        Assert.Equal(new TileCoord(0, 0), b.Position);
        Assert.True(shared, "two friends walking through each other should meet on one subtile for a step");
    }

    [Fact]
    public void ACrowdOnARoad_DoesNotJam_EveryoneArrivesInTheTimeItsOwnWalkTakes()
    {
        var (sim, w) = World(7, 1);
        var units = Enumerable.Range(1, 14).Select(id => Add(w, id, new TileCoord(0, 0))).ToList();
        var alone = TestMarch.TicksFor(w, units[0], new TileCoord(6, 0));
        foreach (var u in units) Go(sim, u.Id, new TileCoord(6, 0));
        sim.Run();
        Assert.All(units, u => Assert.Equal(new TileCoord(6, 0), u.Position));
        // Nothing waited on anything: the last to arrive took about as long as a solo walk plus
        // the few extra steps to a free subtile near the centre.
        Assert.True(sim.Now <= alone + 40 * SubtileStepRules.StepTicks(w, new TileCoord(6, 0)), $"{sim.Now} vs {alone}");
    }

    // ---- a hostile force stops a walk ----------------------------------------------------------

    [Fact]
    public void AWalkerCannotPassThroughAHostileForce_ItStopsAndFightsThere()
    {
        var (sim, w) = World(9, 1, enemies: true);
        var mover = Add(w, 1, new TileCoord(0, 0), Blue, UnitRole.Soldier);
        Add(w, 2, new TileCoord(4, 0), Red, UnitRole.Soldier);
        Go(sim, 1, new TileCoord(8, 0));
        for (var t = 1L; t < 3000 && !w.Battlefields.ContainsKey(new TileCoord(4, 0)); t++) sim.Run(until: t);
        Assert.True(w.Battlefields.ContainsKey(new TileCoord(4, 0)), "a board should have opened where it met the enemy");
        Assert.Equal(new TileCoord(4, 0), mover.Position);
        Assert.NotNull(mover.Board);
        Assert.False(mover.IsWalking);                              // the board took the walk
        Assert.Equal(new TileCoord(8, 0), mover.PathFinalDest);     // and remembers where it was going
    }

    [Fact]
    public void ANeutralPassesThroughAFight_Unstopped()
    {
        var (sim, w) = World(9, 1, enemies: true);
        w.Players[2] = new Player(2);
        Add(w, 10, new TileCoord(4, 0), Blue, UnitRole.Soldier);
        Add(w, 11, new TileCoord(4, 0), Red, UnitRole.Soldier);
        var neutral = Add(w, 12, new TileCoord(0, 0), 2);
        sim.Run(until: 1);
        Go(sim, 12, new TileCoord(8, 0), 2);
        for (var t = 2L; t < 3000 && neutral.Position != new TileCoord(8, 0); t++) sim.Run(until: t);
        Assert.Equal(new TileCoord(8, 0), neutral.Position);
    }

    // ---- fog and hidden walls --------------------------------------------------------------------

    [Fact]
    public void AWallTheOwnerCouldNotSee_StopsTheWalkWhereItStands()
    {
        var (sim, w) = World(9, 1, enemies: true);
        var u = Add(w, 1, new TileCoord(0, 0));
        Go(sim, 1, new TileCoord(8, 0));
        sim.Run(until: 1);
        Assert.True(u.IsWalking);
        w.AddStructure(new Wall(new TileCoord(5, 0)) { OwnerId = Red });   // built after the plan, in the fog
        sim.Run();
        Assert.False(u.IsWalking);
        Assert.Null(u.PathFinalDest);
        Assert.True(u.Position.X <= 4, $"stopped before the wall, at {u.Position}");
        Assert.Equal(Activity.Idle, u.Activity);
    }

    // ---- orders replace walks ---------------------------------------------------------------------

    [Fact]
    public void ANewOrder_ReplacesTheWalk_AndTheOldStepsDoNothing()
    {
        var (sim, w) = World();
        var u = Add(w, 1, new TileCoord(0, 0));
        Go(sim, 1, new TileCoord(8, 0));
        sim.Run(until: 100);
        Go(sim, 1, new TileCoord(0, 0));
        sim.Run();
        Assert.Equal(new TileCoord(0, 0), u.Position);
        Assert.False(u.IsWalking);
    }

    [Fact]
    public void AWalk_IsAsLongAsThePathfindersPath_AndTakesTheSumOfItsStepCosts()
    {
        var (sim, w) = World(6, 3);
        var u = Add(w, 1, new TileCoord(0, 0));
        var expected = TestMarch.TicksFor(w, u, new TileCoord(5, 2));
        Go(sim, 1, new TileCoord(5, 2));
        sim.Run();
        Assert.Equal(expected, sim.Now);
        Assert.Equal(new TileCoord(5, 2), u.Position);
    }

    // ---- groups ------------------------------------------------------------------------------------

    private static int Form(Simulation sim, int[] ids, TileCoord at)
    {
        sim.SubmitIntent(sim.Now, new FormGroupIntent(ids, at) { PlayerId = Blue });
        sim.Run(until: sim.Now);
        return sim.World.Groups.Keys.Max();
    }

    [Fact]
    public void AGroupOrder_SendsEveryMemberToASubtileOfItsOwn_AndTheGroupIsIdleAtTheTile()
    {
        var (sim, w) = World(6, 1);
        var ids = new[] { 1, 2, 3, 4 };
        foreach (var id in ids) Add(w, id, new TileCoord(0, 0));
        var gid = Form(sim, ids, new TileCoord(0, 0));
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(5, 0)) { PlayerId = Blue });
        sim.Run(until: sim.Now);
        Assert.Equal(GroupState.Moving, w.Groups[gid].State);
        sim.Run();
        var g = w.Groups[gid];
        Assert.Equal(GroupState.Idle, g.State);
        Assert.Equal(new TileCoord(5, 0), g.Position);
        Assert.Equal(4, ids.Select(id => w.Units[id].Subtile).Distinct().Count());
        Assert.All(ids, id => Assert.Equal(new TileCoord(5, 0), w.Units[id].Position));
    }

    [Fact]
    public void AMovingGroup_PausesForAFight_ThenMarchesOn()
    {
        // M46 (docs/m46-groups-spec.md, "Battles"): the column waits while a member is on a
        // board and carries on to its destination when the fight is over. Before M46 it
        // halted for good and the player had to order it again.
        var (sim, w) = World(9, 1, enemies: true);
        var ids = new[] { 1, 2, 3 };
        foreach (var id in ids) Add(w, id, new TileCoord(0, 0), Blue, UnitRole.Soldier).Doctrine = BattleDoctrine.Advance;
        Add(w, 9, new TileCoord(4, 0), Red, UnitRole.Soldier).Doctrine = BattleDoctrine.Advance;   // two Holds never fight
        var gid = Form(sim, ids, new TileCoord(0, 0));
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(8, 0)) { PlayerId = Blue });
        for (var t = sim.Now + 1; t < 3000 && !w.Battlefields.ContainsKey(new TileCoord(4, 0)); t++) sim.Run(until: t);
        Assert.True(w.Battlefields.ContainsKey(new TileCoord(4, 0)));
        var group = w.Groups[gid];
        Assert.Equal(GroupState.Moving, group.State);   // paused, not halted
        Assert.Null(group.NextStepTick);

        sim.Run(until: sim.Now + 50_000);

        Assert.Empty(w.Battlefields);
        Assert.False(w.Units.ContainsKey(9));
        Assert.Equal(GroupState.Idle, group.State);
        Assert.Equal(new TileCoord(8, 0), group.Position);
        Assert.All(group.Members, id => Assert.Equal(new TileCoord(8, 0), w.Units[id].Position));
    }

    // ---- the contract -------------------------------------------------------------------------------

    [Fact]
    public void ACrowdWalking_TwinRunsHashEqual_AndAMidWalkRestoreEndsTheSame()
    {
        Simulation Build()
        {
            var (sim, w) = World(8, 2);
            for (var id = 1; id <= 12; id++) Add(w, id, new TileCoord(id % 2, id % 2));
            for (var id = 1; id <= 12; id++) Go(sim, id, new TileCoord(7, 1 - id % 2));
            return sim;
        }
        var a = Build(); a.Run();
        var b = Build(); b.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));

        var mid = Build();
        mid.Run(until: 150);
        Assert.Contains(mid.World.Units.Values, u => u.IsWalking);
        var restored = Snapshot.Restore(Snapshot.Serialize(mid), seed: 3);
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
        mid.Run();
        restored.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(mid));
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
    }
}
