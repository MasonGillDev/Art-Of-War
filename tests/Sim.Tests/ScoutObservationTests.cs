using System.Linq;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Scouting;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M20 Phase 1 — the observation log is the canonical, fog-honest, fully
// deterministic artifact. These pins cover:
//   - content recording within the scout's live vision disc (and ONLY there)
//   - empty tiles omitted (observed-empty is implied by coverage, not stored)
//   - the scout never reports itself
//   - structures captured with target-kind + integer build progress
//   - one leg appended per arrival, in path order
//   - capture is a no-op for a unit with no mission
//   - twin-run hash equality (capture introduces no nondeterminism)
//   - snapshot round-trip at FormatVersion 14 preserves the log
public class ScoutObservationTests
{
    private const int ScoutId = 1;

    private static (GameWorld world, Simulation sim) MakeWorldWithScout(
        TileCoord scoutAt, ulong seed = 1)
    {
        var spec = new GenesisSpec
        {
            Width = 40, Height = 40,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(20, 20), // far from the test discs
                    UnitSpawns = new[] { new UnitSpawn(ScoutId, scoutAt, UnitRole.Scout) },
                },
            },
        };
        var world = Genesis.Build(spec);
        var sim = new Simulation(world, seed);
        return (world, sim);
    }

    private static ScoutMission StartMission(GameWorld world, Simulation sim)
    {
        var mission = new ScoutMission
        {
            ScoutUnitId = ScoutId,
            OwnerId = 0,
            DispatchTick = sim.Now,
        };
        world.ScoutMissions[ScoutId] = mission;
        return mission;
    }

    // ---- direct-call: precise content + coverage ----------------------

    [Fact]
    public void Capture_RecordsContentInDisc_SkipsEmptyAndOutOfDisc()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        var scout = world.Units[ScoutId];
        var mission = StartMission(world, sim);

        // In the radius-6 disc around (3,3): a foreign soldier and a House
        // under construction. Far outside it: another soldier.
        world.AddUnit(new Unit(50, new TileCoord(5, 3)) { OwnerId = 2, Role = UnitRole.Soldier });
        world.AddUnit(new Unit(51, new TileCoord(28, 28)) { OwnerId = 2, Role = UnitRole.Soldier });
        var site = world.AddStructure(new ConstructionSite(new TileCoord(3, 6), StructureKind.House));
        site.ProgressTicks = 3;

        ScoutObservation.Capture(sim, scout);

        Assert.Single(mission.Legs);
        var leg = mission.Legs[0];
        Assert.Equal(new TileCoord(3, 3), leg.Center);
        Assert.Equal(Sight.RadiusFor(UnitRole.Scout), leg.Radius);

        // The in-disc soldier is recorded with true id/owner/role.
        var soldierSight = leg.Sightings.Single(s => s.Tile == new TileCoord(5, 3));
        var seen = Assert.Single(soldierSight.Units);
        Assert.Equal(50, seen.UnitId);
        Assert.Equal(2, seen.OwnerId);
        Assert.Equal(UnitRole.Soldier, seen.Role);

        // The construction site is recorded with target-kind + progress.
        var siteSight = leg.Sightings.Single(s => s.Tile == new TileCoord(3, 6));
        Assert.NotNull(siteSight.Structure);
        Assert.Equal(StructureKind.ConstructionSite, siteSight.Structure!.Kind);
        Assert.Equal(StructureKind.House, siteSight.Structure.TargetKind);
        Assert.Equal(3, siteSight.Structure.ProgressTicks);
        Assert.Equal(site.BuildDurationTicks, siteSight.Structure.BuildDurationTicks);

        // The out-of-disc soldier is NOT recorded (fog-honest by construction).
        Assert.DoesNotContain(leg.Sightings, s => s.Tile == new TileCoord(28, 28));
        // Empty in-disc tiles are NOT recorded (observed-empty is implied by
        // the leg's coverage, never an explicit sighting).
        Assert.DoesNotContain(leg.Sightings, s => s.Tile == new TileCoord(4, 3));
        // The scout never reports its own tile (it is the only unit there).
        Assert.DoesNotContain(leg.Sightings, s => s.Tile == new TileCoord(3, 3));
    }

    [Fact]
    public void Capture_ActiveProgress_AddsLiveDelta()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        StartMission(world, sim);

        // An actively-building site near the path: 2 ticks banked, building
        // since tick 0. What the scout sees must be the EFFECTIVE progress at
        // the moment of observation (banked + elapsed), not the stale bank.
        var site = world.AddStructure(new ConstructionSite(new TileCoord(3, 5), StructureKind.House));
        site.ProgressTicks = 2;
        site.LastActiveAtTick = 0;

        sim.SubmitIntent(0, new MoveIntent(ScoutId, new TileCoord(9, 3)));
        sim.Run();

        // For every leg that saw the site, the captured progress equals
        // 2 + that leg's tick (capped) — proving the live delta is applied at
        // observation time, with no dependence on movement-cost internals.
        var sawIt = world.ScoutMissions[ScoutId].Legs
            .Where(l => l.Sightings.Any(s => s.Tile == new TileCoord(3, 5)))
            .ToList();
        Assert.NotEmpty(sawIt);
        foreach (var leg in sawIt)
        {
            var sighting = leg.Sightings.Single(s => s.Tile == new TileCoord(3, 5));
            var expected = System.Math.Min(2 + leg.Tick, site.BuildDurationTicks);
            Assert.Equal(expected, sighting.Structure!.ProgressTicks);
        }
    }

    [Fact]
    public void Capture_AppendsLegPerCall_InOrder()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        var scout = world.Units[ScoutId];
        var mission = StartMission(world, sim);
        world.AddUnit(new Unit(50, new TileCoord(5, 3)) { OwnerId = 2, Role = UnitRole.Soldier });

        ScoutObservation.Capture(sim, scout);
        scout.Position = new TileCoord(4, 3);
        ScoutObservation.Capture(sim, scout);

        Assert.Equal(2, mission.Legs.Count);
        Assert.Equal(new TileCoord(3, 3), mission.Legs[0].Center);
        Assert.Equal(new TileCoord(4, 3), mission.Legs[1].Center);
    }

    [Fact]
    public void Capture_OnlyWhileActive()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        var scout = world.Units[ScoutId];
        var mission = StartMission(world, sim);
        mission.State = ScoutMissionState.Returned;

        ScoutObservation.Capture(sim, scout);

        Assert.Empty(mission.Legs);
    }

    // ---- move-driven: integration + determinism -----------------------

    [Fact]
    public void Walk_CapturesAlongPath_AndIsFogHonest()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        StartMission(world, sim);
        // Off the path but within vision; never stepped on (no combat).
        world.AddUnit(new Unit(50, new TileCoord(6, 6)) { OwnerId = 2, Role = UnitRole.Soldier });
        // Far away — must never appear in the log.
        world.AddUnit(new Unit(51, new TileCoord(36, 36)) { OwnerId = 2, Role = UnitRole.Soldier });

        sim.SubmitIntent(0, new MoveIntent(ScoutId, new TileCoord(9, 3)));
        sim.Run();

        var mission = world.ScoutMissions[ScoutId];
        Assert.NotEmpty(mission.Legs);
        // Legs are arrival-ordered: each center one step on from the last,
        // ending on the scout's final tile.
        for (var i = 1; i < mission.Legs.Count; i++)
        {
            var a = mission.Legs[i - 1].Center;
            var b = mission.Legs[i].Center;
            Assert.True(System.Math.Max(System.Math.Abs(a.X - b.X), System.Math.Abs(a.Y - b.Y)) == 1);
        }
        Assert.Equal(world.Units[ScoutId].Position, mission.Legs[^1].Center);

        var allSightings = mission.Legs.SelectMany(l => l.Sightings).ToList();
        Assert.Contains(allSightings, s => s.Tile == new TileCoord(6, 6));
        Assert.DoesNotContain(allSightings, s => s.Tile == new TileCoord(36, 36));
    }

    [Fact]
    public void NoMission_LeavesNoLog()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        // No StartMission call.
        sim.SubmitIntent(0, new MoveIntent(ScoutId, new TileCoord(9, 3)));
        sim.Run();

        Assert.Empty(world.ScoutMissions);
    }

    [Fact]
    public void ObservationLog_TwinRun_HashesMatch()
    {
        string RunOnce()
        {
            var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
            StartMission(world, sim);
            world.AddUnit(new Unit(50, new TileCoord(6, 6)) { OwnerId = 2, Role = UnitRole.Soldier });
            sim.SubmitIntent(0, new MoveIntent(ScoutId, new TileCoord(9, 3)));
            sim.Run();
            return Snapshot.Hash(sim);
        }

        Assert.Equal(RunOnce(), RunOnce());
    }

    [Fact]
    public void Snapshot_RoundTrip_V14_PreservesLog()
    {
        var (world, sim) = MakeWorldWithScout(new TileCoord(3, 3));
        StartMission(world, sim);
        world.AddUnit(new Unit(50, new TileCoord(6, 6)) { OwnerId = 2, Role = UnitRole.Soldier });
        world.AddStructure(new ConstructionSite(new TileCoord(3, 6), StructureKind.House)).ProgressTicks = 4;
        sim.SubmitIntent(0, new MoveIntent(ScoutId, new TileCoord(9, 3)));
        sim.Run();

        // The log is non-trivial — otherwise this proves nothing.
        Assert.NotEmpty(world.ScoutMissions[ScoutId].Legs.SelectMany(l => l.Sightings));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xBADF00D);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.True(restored.World.ScoutMissions.ContainsKey(ScoutId));
    }
}
