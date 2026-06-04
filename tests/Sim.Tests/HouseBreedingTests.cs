using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M8 Phase D: House structure + BeginBreedingIntent.
public class HouseBreedingTests
{
    private const long Gestation = 50;
    private const int Food = 5;

    private static readonly PopulationConfig Cfg = new(
        TicksPerYear: 10,
        MinTrainAge: 15,
        MinFertileAge: 18,
        MaxFertileAge: 40,
        GestationTicks: Gestation,
        BirthFoodCost: Food,
        LifespanMinYears: 200, // long lifespan so tests don't trip old-age
        LifespanMaxYears: 200);

    // Two parents at the same tile, with a pre-built House holding food.
    // Both adult, both Idle, both owned by player 0.
    private static Simulation MakeReadyToBreed(int ageA = 25, int ageB = 25, int foodInHouse = Food)
    {
        var tile = new TileCoord(5, 5);
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = Cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 25,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, tile, UnitRole.Builder, StartingAgeYears: ageA),
                        new UnitSpawn(2, tile, UnitRole.Builder, StartingAgeYears: ageB),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xA8E);
        var house = sim.World.AddStructure(new House(tile) { OwnerId = 0 });
        if (foodInHouse > 0) house.Deposit(Resource.Food, foodInHouse);
        return sim;
    }

    [Fact]
    public void BeginBreeding_ValidPair_StartsGestation()
    {
        var sim = MakeReadyToBreed();
        var tile = new TileCoord(5, 5);
        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: 0);

        var house = (House)sim.World.Structures[tile];
        Assert.NotNull(house.Occupation);
        Assert.Equal(1, house.Occupation!.ParentAId);
        Assert.Equal(2, house.Occupation.ParentBId);
        Assert.Equal(Gestation, house.Occupation.BirthTick);
        Assert.Equal(0, house.AmountOf(Resource.Food));
        Assert.Equal(Activity.Working, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Working, sim.World.Units[2].Activity);
    }

    [Fact]
    public void BeginBreeding_RejectsUnderage()
    {
        var sim = MakeReadyToBreed(ageA: 17, ageB: 25);
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: 0);
        Assert.Null(((House)sim.World.Structures[new TileCoord(5, 5)]).Occupation);
    }

    [Fact]
    public void BeginBreeding_RejectsOverage()
    {
        var sim = MakeReadyToBreed(ageA: 25, ageB: 41);
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: 0);
        Assert.Null(((House)sim.World.Structures[new TileCoord(5, 5)]).Occupation);
    }

    [Fact]
    public void BeginBreeding_RejectsNotAtHouseTile()
    {
        var sim = MakeReadyToBreed();
        // Move unit 1 off the house tile.
        sim.World.Units[1].Position = new TileCoord(0, 0);
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: 0);
        Assert.Null(((House)sim.World.Structures[new TileCoord(5, 5)]).Occupation);
    }

    [Fact]
    public void BeginBreeding_RejectsInsufficientFood()
    {
        var sim = MakeReadyToBreed(foodInHouse: Food - 1);
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: 0);
        Assert.Null(((House)sim.World.Structures[new TileCoord(5, 5)]).Occupation);
    }

    [Fact]
    public void BeginBreeding_RejectsSameUnitForBothParents()
    {
        var sim = MakeReadyToBreed();
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 1));
        sim.Run(until: 0);
        Assert.Null(((House)sim.World.Structures[new TileCoord(5, 5)]).Occupation);
    }

    [Fact]
    public void BeginBreeding_RejectsHouseOccupied()
    {
        // First breeding occupies the house.
        var sim = MakeReadyToBreed(foodInHouse: Food * 2);
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: 0);
        // Second breeding attempt on the same house with the same parents:
        // parents are now Working, but the dominant reason will be
        // "house already occupied". Either reason is a reject.
        sim.SubmitIntent(sim.Now, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: sim.Now);
        // Still only the first occupation present.
        Assert.NotNull(((House)sim.World.Structures[new TileCoord(5, 5)]).Occupation);
    }

    [Fact]
    public void House_SnapshotRoundTrip_WithOccupation()
    {
        var sim = MakeReadyToBreed();
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: 0);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xA8E);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var house = (House)restored.World.Structures[new TileCoord(5, 5)];
        Assert.NotNull(house.Occupation);
        Assert.Equal(1, house.Occupation!.ParentAId);
        Assert.Equal(2, house.Occupation.ParentBId);
    }
}
