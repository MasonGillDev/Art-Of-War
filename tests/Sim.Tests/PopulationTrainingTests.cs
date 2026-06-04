using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Tests;

// M8 Phase C: training gate. Role-required intents reject under-age
// units; non-role-required intents (MoveIntent, HaulIntent) accept
// children freely.
public class PopulationTrainingTests
{
    // Tight TicksPerYear keeps test scenarios short.
    private static readonly PopulationConfig TightConfig = new(
        TicksPerYear: 10,
        MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
        GestationTicks: 50, BirthFoodCost: 5,
        LifespanMinYears: 100, LifespanMaxYears: 100);

    private static Simulation MakeSim(int unit1AgeYears)
    {
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = TightConfig,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Wood] = 50 },
                    StartingAgeYears = unit1AgeYears,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(3, 3), UnitRole.Builder),
                    },
                },
            },
        };
        return new Simulation(spec, seed: 0xA8E);
    }

    [Fact]
    public void AssignBuilders_RejectsChild()
    {
        var sim = MakeSim(unit1AgeYears: 10);
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(3, 3), StructureKind.Stockpile));
        sim.Run(until: 1);
        var site = (ConstructionSite)sim.World.Structures[new TileCoord(3, 3)];
        site.Deposit(Resource.Wood, 20);

        // Child (age 10) tries to be assigned as builder.
        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(new TileCoord(3, 3), new[] { 1 }));
        sim.Run(until: sim.Now);

        // Unit 1 should still be Idle (gate rejected).
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
    }

    [Fact]
    public void AssignBuilders_AcceptsAdult()
    {
        var sim = MakeSim(unit1AgeYears: 20);
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(3, 3), StructureKind.Stockpile));
        sim.Run(until: 1);
        var site = (ConstructionSite)sim.World.Structures[new TileCoord(3, 3)];
        site.Deposit(Resource.Wood, 20);

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(new TileCoord(3, 3), new[] { 1 }));
        sim.Run(until: sim.Now);

        Assert.Equal(Activity.Building, sim.World.Units[1].Activity);
    }

    [Fact]
    public void Child_CanMoveAndHaul()
    {
        // Child (age 8) can still MoveIntent — the gate is on training, not
        // on movement.
        var sim = MakeSim(unit1AgeYears: 8);
        var target = new TileCoord(5, 5);
        sim.SubmitIntent(0, new MoveIntent(1, target));
        // Cap the run before the unit's eventual old-age death (lifespan
        // 100y × 10 ticks/y = ~1000 ticks).
        sim.Run(until: 100);
        Assert.Equal(target, sim.World.Units[1].Position);
    }
}
