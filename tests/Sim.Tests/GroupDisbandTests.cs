using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M5 Phase D — DisbandGroupIntent + solo-intent rejection.
public class GroupDisbandTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 12, int h = 12)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var sim = new Simulation(world, seed: 1);
        return (sim, world);
    }

    private static int FormAt(Simulation sim, GameWorld world, TileCoord tile, params int[] memberIds)
    {
        foreach (var id in memberIds)
            world.AddUnit(new Unit(id, tile) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(memberIds, tile));
        sim.Run(until: 0);
        return world.Groups.Keys.Last();
    }

    [Fact]
    public void Disband_IdleGroup_MembersBecomeSolo_GroupRemoved()
    {
        var (sim, world) = MakeWorld();
        var gid = FormAt(sim, world, new TileCoord(5, 5), 1, 2, 3);

        sim.SubmitIntent(sim.Now, new DisbandGroupIntent(gid));
        sim.Run();

        Assert.False(world.Groups.ContainsKey(gid));
        Assert.Null(world.Units[1].GroupId);
        Assert.Null(world.Units[2].GroupId);
        Assert.Null(world.Units[3].GroupId);
        // Members stay where they were (group's tile).
        Assert.Equal(new TileCoord(5, 5), world.Units[1].Position);
    }

    [Fact]
    public void Disband_MovingGroup_FencesArrival_MembersStopAtLastTile()
    {
        var (sim, world) = MakeWorld();
        var gid = FormAt(sim, world, new TileCoord(2, 2), 1, 2);
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(9, 2)));
        // Run for a few ticks to put the group mid-walk.
        sim.Run(until: sim.Now + 30);
        var midPosition = world.Groups[gid].Position;

        sim.SubmitIntent(sim.Now, new DisbandGroupIntent(gid));
        sim.Run();

        Assert.False(world.Groups.ContainsKey(gid));
        Assert.Null(world.Units[1].GroupId);
        Assert.Null(world.Units[2].GroupId);
        // Members stopped at the last arrival tile (Disband fences the next).
        Assert.Equal(midPosition, world.Units[1].Position);
        Assert.Equal(midPosition, world.Units[2].Position);
        // A stale GroupArrivalEvent fenced cleanly. After Disband removes
        // the group, the "group no longer exists" check fires before the
        // epoch check — both are valid fail-clean paths.
        Assert.Contains(sim.ResolvedLog.OfType<GroupArrivalEvent>(),
            e => e.Outcome.IsRejected);
    }

    [Fact]
    public void Disband_FormingGroup_StopsRendezvousWalks()
    {
        var (sim, world) = MakeWorld();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(9, 9)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, new TileCoord(5, 5)));
        sim.Run(until: 0);
        var gid = world.Groups.Keys.Last();
        Assert.Equal(GroupState.Forming, world.Groups[gid].State);

        // Mid-walk, disband.
        sim.Run(until: sim.Now + 20);
        var u1Mid = world.Units[1].Position;
        var u2Mid = world.Units[2].Position;

        sim.SubmitIntent(sim.Now, new DisbandGroupIntent(gid));
        sim.Run();

        Assert.False(world.Groups.ContainsKey(gid));
        Assert.Null(world.Units[1].GroupId);
        Assert.Null(world.Units[2].GroupId);
        // Members stay near where they were mid-walk (frozen at their last
        // completed arrival).
        Assert.Equal(u1Mid, world.Units[1].Position);
        Assert.Equal(u2Mid, world.Units[2].Position);
    }

    [Fact]
    public void Disband_NonexistentGroup_Rejected()
    {
        var (sim, _) = MakeWorld();
        sim.SubmitIntent(0, new DisbandGroupIntent(999));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void Disband_WrongOwner_Rejected()
    {
        var (sim, world) = MakeWorld();
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(1, new TileCoord(5, 5)) { Role = UnitRole.Builder, OwnerId = 1 });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(5, 5)) { PlayerId = 1 });
        sim.Run(until: 0);
        var gid = world.Groups.Keys.Last();

        sim.SubmitIntent(sim.Now, new DisbandGroupIntent(gid) { PlayerId = 0 });
        sim.Run();
        Assert.True(sim.ResolvedLog.OfType<IntentEvent>()
            .Last(e => e.Intent is DisbandGroupIntent)
            .Outcome.IsRejected);
        Assert.True(world.Groups.ContainsKey(gid));
    }

    // -------- Solo-intent rejection on grouped units --------

    [Fact]
    public void MoveIntent_OnGroupedUnit_Rejected()
    {
        var (sim, world) = MakeWorld();
        var gid = FormAt(sim, world, new TileCoord(5, 5), 1);
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(7, 7)));
        sim.Run();
        var moveOutcome = sim.ResolvedLog.OfType<IntentEvent>()
            .Last(e => e.Intent is MoveIntent).Outcome;
        Assert.True(moveOutcome.IsRejected);
        Assert.Contains("group", moveOutcome.Reason ?? "");
    }

    [Fact]
    public void HaulIntent_OnGroupedUnit_Rejected()
    {
        var (sim, world) = MakeWorld();
        // Place a Stockpile so the haul has a valid source.
        world.AddStructure(new Stockpile(new TileCoord(0, 0)) { OwnerId = 0 });
        ((Stockpile)world.Structures[new TileCoord(0, 0)]).Deposit(Resource.Wood, 10);
        world.AddStructure(new Stockpile(new TileCoord(5, 5)) { OwnerId = 0 });

        // Form a haul-capable unit into a group.
        world.AddUnit(new Unit(1, new TileCoord(5, 5)) { Role = UnitRole.Hauler });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(5, 5)));
        sim.Run(until: 0);

        sim.SubmitIntent(sim.Now, new HaulIntent(1, new TileCoord(0, 0), new TileCoord(5, 5), Resource.Wood));
        sim.Run();
        var outcome = sim.ResolvedLog.OfType<IntentEvent>()
            .Last(e => e.Intent is HaulIntent).Outcome;
        Assert.True(outcome.IsRejected);
        Assert.Contains("group", outcome.Reason ?? "");
    }

    [Fact]
    public void AssignBuildersIntent_OnGroupedUnit_SkipsThatUnit()
    {
        var (sim, world) = MakeWorld();
        var siteTile = new TileCoord(5, 5);
        var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
        var spec = StructureCatalog.Spec(StructureKind.Stockpile);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);

        // Two builders on the site; one grouped, one solo.
        world.AddUnit(new Unit(1, siteTile) { Role = UnitRole.Builder });   // will be grouped
        world.AddUnit(new Unit(2, siteTile) { Role = UnitRole.Builder });   // solo
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, siteTile));
        sim.Run(until: 0);

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(siteTile, new[] { 1, 2 }));
        sim.Run();
        // Unit 1 stays Idle (grouped, skipped); unit 2 was assigned.
        // The site eventually completes since spec requires 1 builder.
        Assert.NotEqual(Activity.Building, world.Units[1].Activity);
    }

    [Fact]
    public void AssignWorkersIntent_OnGroupedUnit_SkipsThatUnit()
    {
        var (sim, world) = MakeWorld();
        var campTile = new TileCoord(5, 5);
        world.Grid.SetBiome(campTile, Biome.Forest);
        var ex = world.AddStructure(new Extractor(StructureKind.LumberCamp, campTile));

        world.AddUnit(new Unit(1, campTile) { Role = UnitRole.Lumberjack });  // will be grouped
        world.AddUnit(new Unit(2, campTile) { Role = UnitRole.Lumberjack });  // solo
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, campTile));
        sim.Run(until: 0);

        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(campTile, new[] { 1, 2 }));
        sim.Run(until: sim.Now + 1);
        Assert.False(ex.Workers.Contains(1));
        Assert.Contains(2, ex.Workers);
    }

    [Fact]
    public void TwinRun_FormMoveDisbandCycle_HashesMatch()
    {
        Simulation Build()
        {
            var (sim, world) = MakeWorld();
            world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(2, new TileCoord(0, 0)) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, new TileCoord(0, 0)));
            sim.SubmitIntent(0, new MoveGroupIntent(1, new TileCoord(9, 9)));
            sim.Run();
            sim.SubmitIntent(sim.Now, new DisbandGroupIntent(1));
            sim.Run();
            return sim;
        }
        var a = Build();
        var b = Build();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }
}
