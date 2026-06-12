using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M19 Phase 1 — home plumbing (docs/m19-per-house-food-spec.md): the
// three auto-assignment triggers, the single-mutation resident ledger,
// and the snapshot round-trip. Consumption stays castle-only until
// Phase 2; these tests pin WHO lives WHERE, not who eats what.
// All expectations derive from StructureCatalog.ResidentCap and
// FoodConsumptionConstants.HomeAssignRadius per the standing convention.
public class HomeAssignmentTests
{
    private const long Gestation = 50;
    private const int BirthFood = 5;

    private static readonly PopulationConfig Cfg = new(
        TicksPerYear: 10,
        MinTrainAge: 15,
        MinFertileAge: 18,
        MaxFertileAge: 40,
        GestationTicks: Gestation,
        BirthFoodCost: BirthFood,
        LifespanMinYears: 500,
        LifespanMaxYears: 500);

    private static int Cap => StructureCatalog.Spec(StructureKind.House).ResidentCap;
    private static int Radius => FoodConsumptionConstants.HomeAssignRadius;

    private static Simulation MakeWorld(params UnitSpawn[] spawns)
    {
        var spec = new GenesisSpec
        {
            Width = 40, Height = 40,
            Population = Cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Wood] = 100 },
                    StartingAgeYears = 25,
                    UnitSpawns = spawns,
                },
            },
        };
        return new Simulation(spec, seed: 0x40E5);
    }

    private static House AddHouse(Simulation sim, TileCoord at, int food = BirthFood)
    {
        var house = (House)sim.World.AddStructure(new House(at) { OwnerId = 0 });
        if (food > 0) house.Deposit(Resource.Food, food);
        return house;
    }

    // Fill beds with the single-mutation writer — legal setup, it IS the
    // one mutation point the design names.
    private static void FillBeds(Simulation sim, House house, int count, int firstUnitId)
    {
        for (var i = 0; i < count; i++)
            Population.SetHome(sim.World, sim.World.Units[firstUnitId + i], house.At);
    }

    // ---- the ledger itself ---------------------------------------------------

    [Fact]
    public void SetHome_MovesTheResidentLedger()
    {
        var sim = MakeWorld(new UnitSpawn(1, new TileCoord(5, 5)));
        var a = AddHouse(sim, new TileCoord(5, 5), food: 0);
        var b = AddHouse(sim, new TileCoord(6, 5), food: 0);
        var u = sim.World.Units[1];

        Population.SetHome(sim.World, u, a.At);
        Assert.Equal((1, 0), (a.ResidentCount, b.ResidentCount));
        Population.SetHome(sim.World, u, b.At);
        Assert.Equal((0, 1), (a.ResidentCount, b.ResidentCount));
        Population.SetHome(sim.World, u, null);   // death / fallback path
        Assert.Equal((0, 0), (a.ResidentCount, b.ResidentCount));
        Assert.Null(u.Home);
    }

    // ---- trigger 1: birth ----------------------------------------------------

    [Fact]
    public void Birth_HomesTheChildAtTheBirthHouse()
    {
        var tile = new TileCoord(5, 5);
        var sim = MakeWorld(
            new UnitSpawn(1, tile, UnitRole.Builder),
            new UnitSpawn(2, tile, UnitRole.Builder));
        var house = AddHouse(sim, tile);

        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: Gestation + 1);

        var child = sim.World.Units.Values.Single(u => u.BornTick == Gestation);
        Assert.Equal(tile, child.Home);
        Assert.Equal(1, house.ResidentCount);
    }

    [Fact]
    public void Birth_OverflowsToNearestBed_ThenCastle()
    {
        var tile = new TileCoord(5, 5);
        var spawns = new List<UnitSpawn>
        {
            new(1, tile, UnitRole.Builder),
            new(2, tile, UnitRole.Builder),
        };
        // Enough filler units to pack both houses to the cap.
        for (var i = 0; i < 2 * Cap; i++)
            spawns.Add(new UnitSpawn(10 + i, new TileCoord(1, 1)));
        var sim = MakeWorld(spawns.ToArray());

        var birthHouse = AddHouse(sim, tile);
        var nextDoor = AddHouse(sim, new TileCoord(5 + 2, 5), food: 0);
        FillBeds(sim, birthHouse, Cap, firstUnitId: 10);

        // Birth house full → the child homes next door.
        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: Gestation + 1);
        var child1 = sim.World.Units.Values.Single(u => u.BornTick == Gestation);
        Assert.Equal(nextDoor.At, child1.Home);

        // Both houses full → the castle (null).
        FillBeds(sim, nextDoor, Cap - 1, firstUnitId: 10 + Cap);
        birthHouse.Deposit(Resource.Food, BirthFood);
        var t2 = sim.Now;
        sim.SubmitIntent(t2, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: t2 + Gestation + 1);
        var child2 = sim.World.Units.Values.Single(u => u.BornTick == t2 + Gestation);
        Assert.Null(child2.Home);
        Assert.Equal(Cap, birthHouse.ResidentCount);
        Assert.Equal(Cap, nextDoor.ResidentCount);
    }

    // ---- trigger 2: home follows work -----------------------------------------

    [Fact]
    public void AssignWorkers_RehomesNearTheWorkplace_WithinRadiusOnly()
    {
        var post = new TileCoord(20, 20);
        var farTile = new TileCoord(30, 30);
        var sim = MakeWorld(
            new UnitSpawn(1, post),
            new UnitSpawn(2, farTile));
        var camp = sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, post) { OwnerId = 0 });
        sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, farTile) { OwnerId = 0 });
        // House within radius of `post`, NOT within radius of `farTile`.
        var house = AddHouse(sim, new TileCoord(20 + Radius, 20), food: 0);
        Assert.True(Math.Max(Math.Abs(house.At.X - farTile.X), Math.Abs(house.At.Y - farTile.Y)) > Radius,
            "fixture: the house must be out of range of the far post");

        sim.SubmitIntent(0, new AssignWorkersIntent(post, new[] { 1 }));
        sim.SubmitIntent(0, new AssignWorkersIntent(farTile, new[] { 2 }));
        sim.Run(until: 1);

        Assert.Equal(house.At, sim.World.Units[1].Home);   // re-homed by the bed nearby
        Assert.Null(sim.World.Units[2].Home);              // nothing in radius → castle
        Assert.Equal(1, house.ResidentCount);
    }

    // ---- trigger 3: house completion ------------------------------------------

    [Fact]
    public void HouseCompletion_MovesNearbyWorkersIn()
    {
        var post = new TileCoord(20, 20);
        var siteTile = new TileCoord(21, 20);
        var sim = MakeWorld(
            new UnitSpawn(1, post),
            new UnitSpawn(2, post),
            new UnitSpawn(3, siteTile, UnitRole.Builder));
        sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, post) { OwnerId = 0 });

        // Staff the camp FIRST — no house exists, so both stay castle-homed
        // (the gap trigger 3 exists to close).
        sim.SubmitIntent(0, new AssignWorkersIntent(post, new[] { 1, 2 }));
        sim.Run(until: 1);
        Assert.Null(sim.World.Units[1].Home);

        // Build the house next door: place, provision, assign the builder.
        sim.SubmitIntent(1, new PlaceSiteIntent(siteTile, StructureKind.House));
        sim.Run(until: 2);
        var site = (ConstructionSite)sim.World.Structures[siteTile];
        foreach (var (res, amt) in StructureCatalog.Spec(StructureKind.House).BuildCost)
            site.Deposit(res, amt);
        sim.SubmitIntent(2, new AssignBuildersIntent(siteTile, new[] { 3 }));
        sim.Run(until: 3 + StructureCatalog.Spec(StructureKind.House).BuildDurationTicks + 2);

        var house = Assert.IsType<House>(sim.World.Structures[siteTile]);
        // The working crew moved in (and the builder, who stood ON the
        // site, re-homed too via trigger 2/3 — all within the cap).
        Assert.Equal(siteTile, sim.World.Units[1].Home);
        Assert.Equal(siteTile, sim.World.Units[2].Home);
        Assert.True(house.ResidentCount >= 2 && house.ResidentCount <= Cap,
            $"resident ledger out of range: {house.ResidentCount}");
    }

    // ---- persistence -----------------------------------------------------------

    [Fact]
    public void Snapshot_RoundTrips_HomeAndResidentLedger()
    {
        var tile = new TileCoord(5, 5);
        var sim = MakeWorld(
            new UnitSpawn(1, tile, UnitRole.Builder),
            new UnitSpawn(2, tile, UnitRole.Builder));
        var house = AddHouse(sim, tile);
        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: Gestation + 1);
        Assert.Equal(1, house.ResidentCount);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0x40E5);

        var child = restored.World.Units.Values.Single(u => u.BornTick == Gestation);
        Assert.Equal(tile, child.Home);
        Assert.Equal(1, ((House)restored.World.Structures[tile]).ResidentCount);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
