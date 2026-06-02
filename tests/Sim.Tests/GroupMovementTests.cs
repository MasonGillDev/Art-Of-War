using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M5 Phase C — MoveGroupIntent + GroupArrivalEvent.
public class GroupMovementTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 12, int h = 12)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var sim = new Simulation(world, seed: 1);
        return (sim, world);
    }

    // Form a group at `tile` with two units already there. Returns the group id.
    private static int FormAt(Simulation sim, GameWorld world, TileCoord tile, params int[] memberIds)
    {
        foreach (var id in memberIds)
            world.AddUnit(new Unit(id, tile) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(memberIds, tile));
        sim.Run(until: 0);
        return world.Groups.Keys.Last();
    }

    [Fact]
    public void Move_IdleGroup_AllMembersArriveTogether()
    {
        var (sim, world) = MakeWorld();
        var start = new TileCoord(2, 2);
        var dest = new TileCoord(7, 2);
        var gid = FormAt(sim, world, start, 1, 2, 3);

        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, dest));
        sim.Run();

        Assert.Equal(GroupState.Idle, world.Groups[gid].State);
        Assert.Equal(dest, world.Groups[gid].Position);
        Assert.Equal(dest, world.Units[1].Position);
        Assert.Equal(dest, world.Units[2].Position);
        Assert.Equal(dest, world.Units[3].Position);
    }

    [Fact]
    public void Move_FormingGroup_Rejected()
    {
        var (sim, world) = MakeWorld();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(9, 9)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, new TileCoord(5, 5)));
        sim.Run(until: 0);
        // Group is Forming.
        var gid = world.Groups.Keys.Last();
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(7, 7)));
        // Resolve the MoveGroupIntent immediately, but stop before any
        // member's arrival fires (which would transition Forming → Idle).
        sim.Run(until: sim.Now);
        Assert.True(sim.ResolvedLog.OfType<IntentEvent>()
            .Last(e => e.Intent is MoveGroupIntent)
            .Outcome.IsRejected);
    }

    [Fact]
    public void Move_NonexistentGroup_Rejected()
    {
        var (sim, _) = MakeWorld();
        sim.SubmitIntent(0, new MoveGroupIntent(999, new TileCoord(5, 5)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void Move_WrongOwner_Rejected()
    {
        var (sim, world) = MakeWorld();
        world.Players[1] = new Player(1);
        // Group owned by player 1.
        var u = world.AddUnit(new Unit(1, new TileCoord(5, 5)) { Role = UnitRole.Builder, OwnerId = 1 });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(5, 5)) { PlayerId = 1 });
        sim.Run(until: 0);
        var gid = world.Groups.Keys.Last();
        // Player 0 attempts the move.
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(7, 7)) { PlayerId = 0 });
        sim.Run();
        Assert.True(sim.ResolvedLog.OfType<IntentEvent>()
            .Last(e => e.Intent is MoveGroupIntent)
            .Outcome.IsRejected);
    }

    [Fact]
    public void Move_OutOfBounds_Rejected()
    {
        var (sim, world) = MakeWorld();
        var gid = FormAt(sim, world, new TileCoord(2, 2), 1);
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(100, 100)));
        sim.Run();
        Assert.True(sim.ResolvedLog.OfType<IntentEvent>()
            .Last(e => e.Intent is MoveGroupIntent)
            .Outcome.IsRejected);
    }

    [Fact]
    public void MoveRetask_BumpsEpoch_OldChainFences()
    {
        var (sim, world) = MakeWorld();
        var gid = FormAt(sim, world, new TileCoord(2, 2), 1, 2);
        var firstDest = new TileCoord(9, 2);
        var secondDest = new TileCoord(2, 9);

        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, firstDest));
        // Run for a few ticks to put the group mid-walk.
        sim.Run(until: sim.Now + 30);
        var epochBefore = world.Groups[gid].MovementEpoch;

        // Retask to a different destination.
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, secondDest));
        sim.Run();

        // Epoch bumped; group reached the second destination.
        Assert.True(world.Groups[gid].MovementEpoch > epochBefore);
        Assert.Equal(secondDest, world.Groups[gid].Position);
        Assert.Equal(secondDest, world.Units[1].Position);
        Assert.Equal(secondDest, world.Units[2].Position);

        // At least one GroupArrivalEvent from the old chain fenced via epoch.
        Assert.Contains(sim.ResolvedLog.OfType<GroupArrivalEvent>(),
            e => e.Outcome.IsRejected && e.Outcome.Reason == "stale (epoch mismatch)");
    }

    [Fact]
    public void TwinRun_MultiGroupScenario_HashesMatch()
    {
        Simulation Build()
        {
            var (sim, world) = MakeWorld();
            // Two separate groups walking toward different destinations.
            world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(2, new TileCoord(0, 0)) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, new TileCoord(0, 0)));
            world.AddUnit(new Unit(3, new TileCoord(11, 11)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(4, new TileCoord(11, 11)) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new FormGroupIntent(new[] { 3, 4 }, new TileCoord(11, 11)));
            sim.Run(until: 0);

            var g1 = 1; var g2 = 2;
            sim.SubmitIntent(sim.Now, new MoveGroupIntent(g1, new TileCoord(11, 0)));
            sim.SubmitIntent(sim.Now, new MoveGroupIntent(g2, new TileCoord(0, 11)));
            sim.Run();
            return sim;
        }
        var a = Build();
        var b = Build();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void MovingGroup_SnapshotMidFlight_RestoreReachesSameHash()
    {
        // M4 gap-closing contract extended to groups: a Moving group mid-path
        // snapshotted and restored runs forward to identical state.
        Simulation Build()
        {
            var (sim, world) = MakeWorld();
            world.AddUnit(new Unit(1, new TileCoord(2, 2)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(2, new TileCoord(2, 2)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(3, new TileCoord(2, 2)) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2, 3 }, new TileCoord(2, 2)));
            sim.Run(until: 0);
            sim.SubmitIntent(sim.Now, new MoveGroupIntent(1, new TileCoord(9, 9)));
            return sim;
        }

        var uninterrupted = Build();
        uninterrupted.Run();

        var midFlight = Build();
        midFlight.Run(until: 30); // mid-walk
        var bytes = Snapshot.Serialize(midFlight);
        var restored = Snapshot.Restore(bytes, seed: 1);
        restored.Run();

        Assert.Equal(Snapshot.Hash(uninterrupted), Snapshot.Hash(restored));
    }

    [Fact]
    public void GroupMove_CreditsRoad_PerMember()
    {
        // A group of N units arriving on a tile credits the road N times.
        // Diminishing returns naturally stack inside the burst.
        var (sim, world) = MakeWorld();
        var gid = FormAt(sim, world, new TileCoord(2, 2), 1, 2, 3);
        var tile = new TileCoord(3, 2);

        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(5, 2)));
        sim.Run(until: sim.Now + Road.EffectiveCost(world, tile, sim.Now));

        // The first hop credited the tile 3 times. With three members,
        // condition should be > one member's worth of gain.
        var cond = Road.ConditionAt(world, tile, sim.Now);
        Assert.True(cond > RoadConstants.BASE_GAIN,
            $"3-member group should credit more than one member alone; got {cond}");
    }
}
