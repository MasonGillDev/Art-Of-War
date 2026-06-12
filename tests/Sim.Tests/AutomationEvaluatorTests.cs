using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// M18 Phase C — per-atom contracts for the server-side evaluator and the
// action→intent factory. The evaluator is a pure read (100×-no-mutation
// pinned below) and enforces the fog contract: structure-subject
// conditions on unseen tiles are NOT met.
public class AutomationEvaluatorTests
{
    private static readonly IReadOnlySet<TileCoord> SeeAll = AllTiles();
    private static readonly IReadOnlySet<TileCoord> SeeNothing = new HashSet<TileCoord>();

    private static HashSet<TileCoord> AllTiles()
    {
        var set = new HashSet<TileCoord>();
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                set.Add(new TileCoord(x, y));
        return set;
    }

    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(10, 10, Biome.Grassland);
        grid.SetBiome(new TileCoord(5, 5), Biome.Forest);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);

        var castle = world.AddStructure(new Castle(new TileCoord(2, 2)));
        castle.Deposit(Resource.Wood, 30);

        var camp = world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(5, 5)));
        camp.Buffer = 7;

        world.AddUnit(new Unit(3, new TileCoord(1, 1)) { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(9, new TileCoord(8, 8)) { Role = UnitRole.Hauler, OwnerId = 1 });
        world.NextUnitId = 10;
        return new Simulation(world, seed: 11);
    }

    private static bool Eval(Simulation sim, ConditionSpec c, IReadOnlySet<TileCoord> visible,
        long stepEnteredTick = 0, long now = 0) =>
        ConditionEvaluator.IsMet(sim.World, ownerId: 0, c, stepEnteredTick, now, visible);

    // ---- condition atoms ---------------------------------------------------

    [Fact]
    public void Always_IsMet() =>
        Assert.True(Eval(BuildWorld(), ConditionSpec.Always(), SeeNothing));

    [Fact]
    public void Store_OnStorageHoldings()
    {
        var sim = BuildWorld();
        var tile = new TileCoord(2, 2); // castle: 30 Wood
        Assert.True(Eval(sim, ConditionSpec.StoreAtLeast(tile, Resource.Wood, 30), SeeAll));
        Assert.False(Eval(sim, ConditionSpec.StoreAtLeast(tile, Resource.Wood, 31), SeeAll));
        Assert.True(Eval(sim, ConditionSpec.StoreBelow(tile, Resource.Wood, 31), SeeAll));
        Assert.False(Eval(sim, ConditionSpec.StoreBelow(tile, Resource.Wood, 30), SeeAll));
        // A resource the castle doesn't hold reads as 0.
        Assert.True(Eval(sim, ConditionSpec.StoreBelow(tile, Resource.Stone, 1), SeeAll));
    }

    [Fact]
    public void Store_OnExtractorBuffer_OutputResourceOnly()
    {
        var sim = BuildWorld();
        var tile = new TileCoord(5, 5); // lumber camp: buffer 7
        var output = StructureCatalog.Spec(StructureKind.LumberCamp).OutputResource;
        Assert.True(Eval(sim, ConditionSpec.StoreAtLeast(tile, output, 7), SeeAll));
        Assert.False(Eval(sim, ConditionSpec.StoreAtLeast(tile, output, 8), SeeAll));
        // The buffer only counts as the camp's OWN output kind.
        Assert.False(Eval(sim, ConditionSpec.StoreAtLeast(tile, Resource.Stone, 1), SeeAll));
    }

    [Fact]
    public void Store_EmptyTile_ReadsAsZero()
    {
        var sim = BuildWorld();
        var empty = new TileCoord(7, 7);
        Assert.False(Eval(sim, ConditionSpec.StoreAtLeast(empty, Resource.Wood, 1), SeeAll));
        Assert.True(Eval(sim, ConditionSpec.StoreBelow(empty, Resource.Wood, 1), SeeAll));
    }

    [Fact]
    public void FogContract_UnseenTile_IsNeverMet()
    {
        var sim = BuildWorld();
        var tile = new TileCoord(2, 2); // castle: 30 Wood — but unseen
        // BOTH directions read not-met: unknown is never true, so a player
        // can't use a StoreBelow rule as a fog probe.
        Assert.False(Eval(sim, ConditionSpec.StoreAtLeast(tile, Resource.Wood, 1), SeeNothing));
        Assert.False(Eval(sim, ConditionSpec.StoreBelow(tile, Resource.Wood, 1_000), SeeNothing));
    }

    [Fact]
    public void Cargo_FullAndEmpty()
    {
        var sim = BuildWorld();
        var unit = sim.World.Units[3];
        Assert.True(Eval(sim, ConditionSpec.CargoEmpty(3), SeeNothing));
        Assert.False(Eval(sim, ConditionSpec.CargoFull(3), SeeNothing));

        unit.CargoResource = Resource.Wood;
        unit.CargoAmount = unit.CargoCapacity;
        Assert.True(Eval(sim, ConditionSpec.CargoFull(3), SeeNothing));
        Assert.False(Eval(sim, ConditionSpec.CargoEmpty(3), SeeNothing));
    }

    [Fact]
    public void UnitConditions_MissingOrForeignUnit_NotMet()
    {
        var sim = BuildWorld();
        Assert.False(Eval(sim, ConditionSpec.CargoEmpty(77), SeeNothing));  // missing (died)
        Assert.False(Eval(sim, ConditionSpec.CargoEmpty(9), SeeNothing));   // player 1's unit
        Assert.False(Eval(sim, ConditionSpec.UnitAtTile(9, new TileCoord(8, 8)), SeeNothing));
    }

    [Fact]
    public void UnitAtTile_MatchesPosition()
    {
        var sim = BuildWorld();
        Assert.True(Eval(sim, ConditionSpec.UnitAtTile(3, new TileCoord(1, 1)), SeeNothing));
        Assert.False(Eval(sim, ConditionSpec.UnitAtTile(3, new TileCoord(2, 1)), SeeNothing));
    }

    [Fact]
    public void ElapsedTicks_BoundaryInclusive()
    {
        var sim = BuildWorld();
        var c = ConditionSpec.ElapsedTicks(100);
        Assert.False(Eval(sim, c, SeeNothing, stepEnteredTick: 50, now: 149));
        Assert.True(Eval(sim, c, SeeNothing, stepEnteredTick: 50, now: 150));
    }

    // ---- pure-read wall ----------------------------------------------------

    [Fact]
    public void Evaluator_IsPureRead_NoMutation()
    {
        var sim = BuildWorld();
        var hashBefore = Snapshot.Hash(sim);
        var conditions = new[]
        {
            ConditionSpec.Always(),
            ConditionSpec.StoreAtLeast(new TileCoord(2, 2), Resource.Wood, 10),
            ConditionSpec.StoreBelow(new TileCoord(5, 5), Resource.Wood, 10),
            ConditionSpec.CargoFull(3),
            ConditionSpec.CargoEmpty(3),
            ConditionSpec.UnitAtTile(3, new TileCoord(1, 1)),
            ConditionSpec.ElapsedTicks(5),
        };
        for (var i = 0; i < 100; i++)
            foreach (var c in conditions)
                Eval(sim, c, SeeAll, stepEnteredTick: 0, now: i);
        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    // ---- the factory table -------------------------------------------------

    [Fact]
    public void Factory_MapsEveryAtom_VoicedAsOwner()
    {
        var src = new TileCoord(5, 5);
        var dst = new TileCoord(2, 2);

        var move = Assert.IsType<MoveIntent>(IntentFactory.Create(ActionSpec.MoveTo(3, dst), ownerId: 4));
        Assert.Equal(3, move.UnitId);
        Assert.Equal(dst, move.Destination);
        Assert.Equal(4, move.PlayerId);

        var haul = Assert.IsType<HaulIntent>(IntentFactory.Create(ActionSpec.HaulTrip(3, src, dst, Resource.Wood), 4));
        Assert.Equal(3, haul.HaulerId);
        Assert.Equal(src, haul.SourceTile);
        Assert.Equal(dst, haul.DestTile);
        Assert.Equal(Resource.Wood, haul.Resource);
        Assert.Equal(4, haul.PlayerId);

        var load = Assert.IsType<LoadCargoIntent>(IntentFactory.Create(ActionSpec.LoadCargo(3, Resource.Food), 4));
        Assert.Equal(3, load.UnitId);
        Assert.Equal(Resource.Food, load.Resource);

        var unload = Assert.IsType<UnloadCargoIntent>(IntentFactory.Create(ActionSpec.UnloadCargo(3), 4));
        Assert.Equal(3, unload.UnitId);

        var train = Assert.IsType<TrainUnitIntent>(IntentFactory.Create(ActionSpec.Train(3, UnitRole.Soldier), 4));
        Assert.Equal(3, train.UnitId);
        Assert.Equal(UnitRole.Soldier, train.NewRole);

        var craft = Assert.IsType<CraftEquipmentIntent>(IntentFactory.Create(ActionSpec.Craft(dst, Resource.Sword), 4));
        Assert.Equal(dst, craft.BarracksTile);
        Assert.Equal(Resource.Sword, craft.Item);

        var assign = Assert.IsType<AssignWorkersIntent>(IntentFactory.Create(ActionSpec.AssignWorkers(3, src), 4));
        Assert.Equal(src, assign.StructureTile);
        Assert.Equal(new[] { 3 }, assign.WorkerIds);

        var unassign = Assert.IsType<UnassignWorkersIntent>(IntentFactory.Create(ActionSpec.UnassignWorkers(3, src), 4));
        Assert.Equal(src, unassign.StructureTile);
        Assert.Equal(new[] { 3 }, unassign.WorkerIds);
        Assert.Equal(4, unassign.PlayerId);
    }
}
