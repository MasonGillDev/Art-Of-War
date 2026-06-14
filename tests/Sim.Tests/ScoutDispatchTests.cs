using System.Collections.Generic;
using System.Linq;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Scouting;
using Sim.Core.World;
using Sim.Server.Scouting;

namespace Sim.Tests;

// M20 Phase 3 — dispatch + the in-sim mission runner. Pins: validation
// (Lodge-gated, scout-only, bounds), waypoint progression and return-home,
// the recall rules (hostile sighted, elapsed budget), end-to-end compile on
// return (Phase 1 → 2 → 3), twin-run determinism, and snapshot round-trip
// mid-mission.
public class ScoutDispatchTests
{
    private const int ScoutId = 5;
    private static readonly TileCoord Home = new(12, 12);

    private static (GameWorld world, Simulation sim) MakeWorld(bool withLodge = true)
    {
        var spec = new GenesisSpec
        {
            Width = 60, Height = 60,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(10, 10),
                    UnitSpawns = new[] { new UnitSpawn(ScoutId, Home, UnitRole.Scout) },
                },
            },
        };
        var world = Genesis.Build(spec);
        if (withLodge) world.AddStructure(new Lodge(new TileCoord(11, 11)) { OwnerId = 0 });
        return (world, new Simulation(world, seed: 1));
    }

    private static List<TileCoord> Route(params (int x, int y)[] pts) =>
        pts.Select(p => new TileCoord(p.x, p.y)).ToList();

    // ---- validation (direct Resolve) ----------------------------------

    [Fact]
    public void Dispatch_RejectedWithoutLodge()
    {
        var (world, sim) = MakeWorld(withLodge: false);
        var outcome = new DispatchScoutIntent(ScoutId, Route((20, 12))).Resolve(sim);
        Assert.True(outcome.IsRejected);
        Assert.Empty(world.ScoutMissions);
    }

    [Fact]
    public void Dispatch_RejectedForNonScout()
    {
        var (world, sim) = MakeWorld();
        world.AddUnit(new Unit(99, new TileCoord(12, 13)) { OwnerId = 0, Role = UnitRole.Hauler });
        var outcome = new DispatchScoutIntent(99, Route((20, 12))).Resolve(sim);
        Assert.True(outcome.IsRejected);
    }

    [Fact]
    public void Dispatch_RejectedForEmptyOrOutOfBoundsWaypoints()
    {
        var (_, sim) = MakeWorld();
        Assert.True(new DispatchScoutIntent(ScoutId, new List<TileCoord>()).Resolve(sim).IsRejected);
        Assert.True(new DispatchScoutIntent(ScoutId, Route((999, 999))).Resolve(sim).IsRejected);
    }

    [Fact]
    public void Dispatch_ValidCreatesActiveMissionAndLaunches()
    {
        var (world, sim) = MakeWorld();
        var outcome = new DispatchScoutIntent(ScoutId, Route((20, 12), (20, 20))).Resolve(sim);
        Assert.True(outcome.IsApplied);
        var mission = world.ScoutMissions[ScoutId];
        Assert.Equal(ScoutMissionState.Active, mission.State);
        Assert.Equal(Home, mission.HomeTile);
        Assert.Equal(2, mission.Waypoints.Count);
        // Launched: the scout has a committed path toward waypoint 0.
        Assert.NotNull(world.Units[ScoutId].PathRemaining);
    }

    // ---- progression + return -----------------------------------------

    [Fact]
    public void Mission_VisitsWaypoints_ThenReturnsHome()
    {
        var (world, sim) = MakeWorld();
        sim.SubmitIntent(0, new DispatchScoutIntent(ScoutId, Route((20, 12), (20, 20))));
        sim.Run();

        var mission = world.ScoutMissions[ScoutId];
        Assert.Equal(ScoutMissionState.Returned, mission.State);
        Assert.Equal(Home, world.Units[ScoutId].Position);
        Assert.NotEmpty(mission.Legs);
    }

    [Fact]
    public void HostileSighted_RecallsEarly()
    {
        var (world, sim) = MakeWorld();
        // A bandit (hostile to all) right beside the first waypoint.
        world.AddUnit(new Unit(80, new TileCoord(21, 12)) { OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId, Role = UnitRole.Bandit });
        sim.SubmitIntent(0, new DispatchScoutIntent(
            ScoutId, Route((20, 12), (20, 20)), ScoutReturnRule.HostileSighted));
        sim.Run();

        var mission = world.ScoutMissions[ScoutId];
        Assert.Equal(ScoutMissionState.Returned, mission.State);
        Assert.Equal(Home, world.Units[ScoutId].Position);
        // Turned back at the first waypoint — never advanced to the second.
        Assert.Equal(0, mission.WaypointCursor);
    }

    [Fact]
    public void ElapsedTicks_RecallsAfterBudget()
    {
        var (world, sim) = MakeWorld();
        sim.SubmitIntent(0, new DispatchScoutIntent(
            ScoutId, Route((20, 12), (20, 20)), ScoutReturnRule.ElapsedTicks, elapsedLimitTicks: 100));
        sim.Run();

        var mission = world.ScoutMissions[ScoutId];
        Assert.Equal(ScoutMissionState.Returned, mission.State);
        Assert.Equal(Home, world.Units[ScoutId].Position);
        // The ~8-tile leg to the first waypoint already blows the 100-tick
        // budget, so it recalls there.
        Assert.Equal(0, mission.WaypointCursor);
    }

    // ---- end-to-end: dispatch → travel → compile ----------------------

    [Fact]
    public void ReturnedMission_CompilesToAReport_WithForeignSightings()
    {
        var (world, sim) = MakeWorld();
        // A foreign dwelling under construction, off the path but in sight of
        // the second waypoint.
        world.AddStructure(new ConstructionSite(new TileCoord(23, 20), StructureKind.House) { OwnerId = 1 })
            .ProgressTicks = 1500;
        sim.SubmitIntent(0, new DispatchScoutIntent(ScoutId, Route((20, 12), (20, 20))));
        sim.Run();

        var report = ClaimsCompiler.Compile(world, world.ScoutMissions[ScoutId]);
        Assert.NotEmpty(report.CanonicalSentences);
        Assert.Contains(report.Claims, c => c.Kind == ClaimKind.Structure);
    }

    // ---- determinism + persistence ------------------------------------

    [Fact]
    public void Dispatch_TwinRun_HashesMatch()
    {
        string RunOnce()
        {
            var (world, sim) = MakeWorld();
            world.AddUnit(new Unit(80, new TileCoord(22, 12)) { OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId, Role = UnitRole.Bandit });
            sim.SubmitIntent(0, new DispatchScoutIntent(ScoutId, Route((20, 12), (20, 20))));
            sim.Run();
            return Snapshot.Hash(sim);
        }
        Assert.Equal(RunOnce(), RunOnce());
    }

    [Fact]
    public void Snapshot_RoundTrip_MidMission()
    {
        var (world, sim) = MakeWorld();
        sim.SubmitIntent(0, new DispatchScoutIntent(ScoutId, Route((20, 12), (20, 20))));
        sim.Run(until: 100); // mid-journey, before reaching the first waypoint

        var mission = world.ScoutMissions[ScoutId];
        Assert.NotEqual(ScoutMissionState.Returned, mission.State); // still travelling

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xBADF00D);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var rm = restored.World.ScoutMissions[ScoutId];
        Assert.Equal(2, rm.Waypoints.Count);
        Assert.Equal(Home, rm.HomeTile);
    }
}
