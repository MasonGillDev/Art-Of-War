using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests;

// Single-unit and structure-targeting intents must reject when the
// issuing PlayerId does not own the unit / structure being acted on.
// The Group-shaped intents (MoveGroupIntent, FormGroupIntent,
// DisbandGroupIntent) already check this; these tests pin the
// matching behaviour on MoveIntent, HaulIntent, AssignWorkers,
// UnassignWorkers, and AssignBuilders — closing an authorization
// hole where any player could retask any unit or sabotage any
// extractor / construction site.
public class IntentAuthorizationTests
{
    private static Simulation MakeSim(int w = 8, int h = 8)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 1);
    }

    [Fact]
    public void MoveIntent_OnOtherPlayersUnit_Rejected()
    {
        var sim = MakeSim();
        var otherUnit = sim.World.AddUnit(new Unit(2, new TileCoord(0, 0))
        {
            OwnerId = 1, Role = UnitRole.Builder,
        });
        var startPos = otherUnit.Position;

        sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(5, 5)) { PlayerId = 0 });
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Equal(startPos, otherUnit.Position);
    }

    [Fact]
    public void HaulIntent_OnOtherPlayersHauler_Rejected()
    {
        var sim = MakeSim();
        var otherUnit = sim.World.AddUnit(new Unit(2, new TileCoord(0, 0))
        {
            OwnerId = 1, Role = UnitRole.Hauler,
        });
        sim.World.AddStructure(new Castle(new TileCoord(0, 0)) { OwnerId = 1 })
            .Deposit(Resource.Wood, 10);
        sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)) { OwnerId = 1 });

        sim.SubmitIntent(0, new HaulIntent(
            2, new TileCoord(0, 0), new TileCoord(3, 0), Resource.Wood) { PlayerId = 0 });
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Equal(Activity.Idle, otherUnit.Activity);
    }

    [Fact]
    public void AssignWorkers_OnOtherPlayersExtractor_Rejected()
    {
        var sim = MakeSim();
        var ownUnit = sim.World.AddUnit(new Unit(1, new TileCoord(0, 0))
        {
            OwnerId = 0, Role = UnitRole.Lumberjack,
        });
        var campTile = new TileCoord(0, 0);
        sim.World.Grid.SetBiome(campTile, Biome.Forest);
        var camp = sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, campTile) { OwnerId = 1 });

        sim.SubmitIntent(0, new AssignWorkersIntent(campTile, new[] { 1 }) { PlayerId = 0 });
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Empty(camp.Workers);
        Assert.Equal(Activity.Idle, ownUnit.Activity);
    }

    [Fact]
    public void AssignWorkers_OtherPlayersUnitInIdList_SkippedSilently()
    {
        // Mixed list: own unit + other player's unit on the player's own
        // extractor. The other-player unit must be skipped (no assignment);
        // own unit must still be assigned (existing per-id skip pattern).
        var sim = MakeSim();
        var campTile = new TileCoord(0, 0);
        sim.World.Grid.SetBiome(campTile, Biome.Forest);
        var ownUnit = sim.World.AddUnit(new Unit(1, campTile)
        {
            OwnerId = 0, Role = UnitRole.Lumberjack,
        });
        var otherUnit = sim.World.AddUnit(new Unit(2, campTile)
        {
            OwnerId = 1, Role = UnitRole.Lumberjack,
        });
        var camp = sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, campTile) { OwnerId = 0 });

        sim.SubmitIntent(0, new AssignWorkersIntent(campTile, new[] { 1, 2 }) { PlayerId = 0 });
        sim.Run();

        Assert.False(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Contains(1, camp.Workers);
        Assert.DoesNotContain(2, camp.Workers);
        Assert.Equal(Activity.Idle, otherUnit.Activity);
    }

    [Fact]
    public void UnassignWorkers_OnOtherPlayersExtractor_Rejected()
    {
        // Stage another player's extractor with a worker already assigned.
        // The attacker tries to sabotage by unassigning. Must be rejected
        // and the worker must remain assigned.
        var sim = MakeSim();
        var campTile = new TileCoord(0, 0);
        sim.World.Grid.SetBiome(campTile, Biome.Forest);
        var otherUnit = sim.World.AddUnit(new Unit(2, campTile)
        {
            OwnerId = 1, Role = UnitRole.Lumberjack,
        });
        var camp = sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, campTile) { OwnerId = 1 });
        camp.Workers.Add(2);
        otherUnit.TrySetActivity(Activity.Working, campTile);

        sim.SubmitIntent(0, new UnassignWorkersIntent(campTile, new[] { 2 }) { PlayerId = 0 });
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Contains(2, camp.Workers);
        Assert.Equal(Activity.Working, otherUnit.Activity);
    }

    [Fact]
    public void AssignBuilders_OnOtherPlayersSite_Rejected()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(0, 0);
        var ownUnit = sim.World.AddUnit(new Unit(1, siteTile)
        {
            OwnerId = 0, Role = UnitRole.Builder,
        });
        sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp) { OwnerId = 1 });

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }) { PlayerId = 0 });
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Equal(Activity.Idle, ownUnit.Activity);
    }
}
