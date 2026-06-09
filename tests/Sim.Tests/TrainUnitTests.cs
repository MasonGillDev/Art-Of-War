using Sim.Core.Engine;
using Sim.Core.Population;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Training — TrainUnitIntent flips a unit's UnitRole instantly when the
// unit is standing on an owned School tile.
public class TrainUnitTests
{
    private static (Simulation sim, School school, Unit citizen) MakeSchoolAndCitizen(
        int startingAge = 30)
    {
        var spec = new GenesisSpec
        {
            Width = 5, Height = 5,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(2, 2), UnitRole.None,
                            OwnerId: 0, StartingAgeYears: startingAge),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0x5C00);
        var school = sim.World.AddStructure(new School(new TileCoord(2, 2)) { OwnerId = 0 });
        return (sim, school, sim.World.Units[1]);
    }

    [Fact]
    public void TrainUnit_OnOwnSchool_FlipsRole()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Builder)
            { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        Assert.Equal(UnitRole.Builder, citizen.Role);
    }

    [Fact]
    public void TrainUnit_FreeRetrain_AllowsBuilderToFarmer()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        new TrainUnitIntent(citizen.Id, UnitRole.Builder) { PlayerId = 0 }.Resolve(sim);
        new TrainUnitIntent(citizen.Id, UnitRole.Farmer) { PlayerId = 0 }.Resolve(sim);
        Assert.Equal(UnitRole.Farmer, citizen.Role);
    }

    [Fact]
    public void TrainUnit_TooYoung_Rejected()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen(startingAge: 10);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Builder)
            { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.Equal(UnitRole.None, citizen.Role);
    }

    [Fact]
    public void TrainUnit_NotOnSchoolTile_Rejected()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        citizen.Position = new TileCoord(1, 1);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Builder)
            { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void TrainUnit_OnEnemySchool_Rejected()
    {
        // Two-faction scenario. Citizen of player 0 on a School owned by player 1.
        var spec = new GenesisSpec
        {
            Width = 5, Height = 5,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(2, 2), UnitRole.None, OwnerId: 0) } },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(4, 4) },
            },
        };
        var sim = new Simulation(spec, seed: 0x5C01);
        sim.World.AddStructure(new School(new TileCoord(2, 2)) { OwnerId = 1 });
        var outcome = new TrainUnitIntent(1, UnitRole.Builder) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void TrainUnit_NotIdle_Rejected()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        // Sneak the unit into Hauling via direct activity transition.
        citizen.TrySetActivity(Activity.Hauling);
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Builder)
            { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void TrainUnit_NotOwned_Rejected()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Builder)
            { PlayerId = 99 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void TrainUnit_IntoBoatRole_Rejected()
    {
        // Boats are dock-produced, not trained. Even if everything else
        // is valid, NewRole == Boat must reject.
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        var outcome = new TrainUnitIntent(citizen.Id, UnitRole.Boat)
            { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.Equal(UnitRole.None, citizen.Role);
    }

    [Fact]
    public void TrainUnit_BumpsAssignmentEpoch()
    {
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        var epochBefore = citizen.AssignmentEpoch;
        new TrainUnitIntent(citizen.Id, UnitRole.Hauler) { PlayerId = 0 }.Resolve(sim);
        Assert.NotEqual(epochBefore, citizen.AssignmentEpoch);
    }

    [Fact]
    public void TrainUnit_AfterTrain_NewlyEligibleForRoleAssignment()
    {
        // Practical end-to-end: a freshly-trained Builder can be assigned
        // to a construction site. (Sanity that Role flip stuck.)
        var (sim, _, citizen) = MakeSchoolAndCitizen();
        new TrainUnitIntent(citizen.Id, UnitRole.Builder) { PlayerId = 0 }.Resolve(sim);
        Assert.Equal(UnitRole.Builder, citizen.Role);
        Assert.True(Population.CanTrain(citizen, sim.Now, sim.World.PopulationConfig));
    }

    [Fact]
    public void School_SnapshotRoundTrip_Preserved()
    {
        var (sim, school, citizen) = MakeSchoolAndCitizen();
        new TrainUnitIntent(citizen.Id, UnitRole.Scout) { PlayerId = 0 }.Resolve(sim);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0x5C00);
        Assert.IsType<School>(restored.World.Structures[school.At]);
        Assert.Equal(UnitRole.Scout, restored.World.Units[1].Role);
    }

    [Fact]
    public void PlaceSiteIntent_CanPlaceSchool()
    {
        // School is player-buildable through the normal place-site flow.
        var spec = new GenesisSpec
        {
            Width = 5, Height = 5,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
            },
        };
        var sim = new Simulation(spec, seed: 0x5C02);
        var outcome = new Sim.Core.Logistics.PlaceSiteIntent(
            new TileCoord(2, 2), StructureKind.School) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
    }
}
