using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// Refining (docs/refining-structures.md): the Smelter is an Extractor with an
// INPUT STORE. Its production tick pays one batch of inputs per unit of
// output, goes dormant when it cannot afford a batch, and is re-armed by the
// haul deposit that restocks the missing input. These pin the acceptance
// tests listed in the decision doc.
public class RefiningTests
{
    private static StructureSpec SmelterSpec => StructureCatalog.Spec(StructureKind.Smelter);
    private static long Period => SmelterSpec.ProductionPeriodTicks;

    private static (Simulation sim, Extractor smelter) MakeSmelter(int workers = 1, UnitRole role = UnitRole.Farmer)
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var at = new TileCoord(3, 3);
        var smelter = (Extractor)world.AddStructure(new Extractor(StructureKind.Smelter, at) { OwnerId = 0 });
        var sim = new Simulation(world, seed: 1);
        for (var i = 1; i <= workers; i++)
        {
            world.AddUnit(new Unit(i, at) { Role = role, OwnerId = 0 });
            sim.SubmitIntent(0, new AssignWorkersIntent(at, new[] { i }));
        }
        return (sim, smelter);
    }

    private static void Feed(Extractor smelter, int ore, int wood)
    {
        Assert.Equal(ore, smelter.DepositInput(Resource.Ore, ore));
        Assert.Equal(wood, smelter.DepositInput(Resource.Wood, wood));
    }

    [Fact]
    public void Catalog_Smelter_IsARefiner_WithNoBiome()
    {
        Assert.True(SmelterSpec.IsRefiner);
        Assert.Equal(Biome.None, SmelterSpec.RequiredBiome);
        Assert.Equal(0, SmelterSpec.ClaimCount);
        Assert.Equal(Resource.Iron, SmelterSpec.OutputResource);
        Assert.True(SmelterSpec.InputCost.ContainsKey(Resource.Ore));
        Assert.True(SmelterSpec.InputCost.ContainsKey(Resource.Wood));
        // Ordinary extractors carry no recipe.
        Assert.False(StructureCatalog.Spec(StructureKind.Mine).IsRefiner);
    }

    [Fact]
    public void Smelter_OneTick_PaysOneBatch_DepositsOneIron()
    {
        // One worker at base rate 1: one batch per period. Stock exactly
        // two batches of ore and one of wood — wood is the binding input.
        var (sim, smelter) = MakeSmelter();
        var oreCost = SmelterSpec.InputCost[Resource.Ore];
        var woodCost = SmelterSpec.InputCost[Resource.Wood];
        Feed(smelter, ore: 2 * oreCost, wood: 1 * woodCost);

        sim.Run(until: Period);

        Assert.Equal(1, smelter.Buffer);
        Assert.Equal(oreCost, smelter.InputOf(Resource.Ore));
        Assert.Equal(0, smelter.InputOf(Resource.Wood));
        // Wood ran out: no batch affordable, so the tick did NOT reschedule.
        Assert.False(smelter.TickArmed);
        Assert.Null(smelter.NextProductionTickSeq);
    }

    [Fact]
    public void Smelter_NoWorkers_DoesNotTick()
    {
        var (sim, smelter) = MakeSmelter(workers: 0);
        Feed(smelter, ore: 10, wood: 10);

        sim.Run(until: Period * 3);

        Assert.Equal(0, smelter.Buffer);
        Assert.Equal(10, smelter.InputOf(Resource.Ore));
        Assert.False(smelter.TickArmed);
    }

    [Fact]
    public void Smelter_WorkersButNoInputs_ArmDeclines_ThenDepositArms()
    {
        // Assigning a worker to an EMPTY smelter must not arm a tick that
        // would only fire to find nothing (the same "decline to arm" rule
        // the claim-exhausted extractor uses).
        var (sim, smelter) = MakeSmelter();
        sim.Run(until: 0);
        Assert.Single(smelter.Workers);
        Assert.False(smelter.TickArmed);

        // A hauler drops a full batch on it via the shared deposit path.
        var oreCost = SmelterSpec.InputCost[Resource.Ore];
        var woodCost = SmelterSpec.InputCost[Resource.Wood];
        Assert.Equal(oreCost, CargoTransfer.DepositInto(sim, smelter, Resource.Ore, oreCost));
        Assert.False(smelter.TickArmed); // half a batch is not a batch
        Assert.Equal(woodCost, CargoTransfer.DepositInto(sim, smelter, Resource.Wood, woodCost));
        Assert.True(smelter.TickArmed);   // the completing delivery armed it

        sim.Run(until: Period);
        Assert.Equal(1, smelter.Buffer);
    }

    [Fact]
    public void Smelter_HaulDeposit_ReArms_OnePeriodAfterDelivery()
    {
        // Starve the smelter of wood, then haul wood in from a stockpile
        // next door. The next tick fires one full period after the DEPOSIT,
        // never immediately and never on the old cadence.
        var (sim, smelter) = MakeSmelter();
        var oreCost = SmelterSpec.InputCost[Resource.Ore];
        var woodCost = SmelterSpec.InputCost[Resource.Wood];
        Feed(smelter, ore: 4 * oreCost, wood: woodCost);
        sim.Run(until: Period);
        Assert.Equal(1, smelter.Buffer);
        Assert.False(smelter.TickArmed);

        var stockAt = new TileCoord(4, 3);
        var stock = sim.World.AddStructure(new Stockpile(stockAt) { OwnerId = 0 });
        stock.Deposit(Resource.Wood, 3 * woodCost);
        sim.World.AddUnit(new Unit(50, stockAt) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.SubmitIntent(sim.Now, new HaulIntent(50, stockAt, smelter.At, Resource.Wood));

        // Walk one tile: well under a period. Run until the deposit lands.
        sim.Run(until: Period * 2 - 1);
        Assert.True(smelter.InputOf(Resource.Wood) > 0, "wood should have been delivered");
        Assert.True(smelter.TickArmed);
        var deliveredAt = smelter.LastProductionTick; // unchanged by the deposit
        Assert.Equal(Period, deliveredAt);
        Assert.Equal(1, smelter.Buffer); // nothing fires before the new period elapses

        sim.Run(until: Period * 3);
        Assert.True(smelter.Buffer >= 2);
    }

    [Fact]
    public void Smelter_OutputCappedByBuffer_ConsumesOnlyMatchingInputs()
    {
        // Two Miners (preferred role, 2x) would make 4/period; room for 1.
        var (sim, smelter) = MakeSmelter(workers: 2, role: UnitRole.Miner);
        var oreCost = SmelterSpec.InputCost[Resource.Ore];
        var woodCost = SmelterSpec.InputCost[Resource.Wood];
        Feed(smelter, ore: 10 * oreCost, wood: 10 * woodCost);
        smelter.Buffer = SmelterSpec.BufferCap - 1;

        sim.Run(until: Period);

        Assert.Equal(SmelterSpec.BufferCap, smelter.Buffer);
        Assert.Equal(9 * oreCost, smelter.InputOf(Resource.Ore));
        Assert.Equal(9 * woodCost, smelter.InputOf(Resource.Wood));
        Assert.False(smelter.TickArmed); // buffer full → dormant
    }

    [Fact]
    public void Smelter_DepositInput_RejectsOffRecipe_AndHonoursCap()
    {
        var (_, smelter) = MakeSmelter(workers: 0);
        Assert.Equal(0, smelter.DepositInput(Resource.Stone, 5));
        Assert.Equal(0, smelter.DepositInput(Resource.Iron, 5));
        Assert.Equal(0, smelter.InputTotal());

        var accepted = smelter.DepositInput(Resource.Ore, SmelterSpec.InputCap + 7);
        Assert.Equal(SmelterSpec.InputCap, accepted);
        Assert.Equal(0, smelter.FreeInputSpace(Resource.Ore));
        // The cap is PER INPUT: a store full of ore still has room for fuel,
        // so the fuel leg can never be starved out by the ore leg (or vice
        // versa — the first host smoke wedged exactly that way).
        Assert.Equal(SmelterSpec.InputCap, smelter.FreeInputSpace(Resource.Wood));
        Assert.Equal(1, smelter.DepositInput(Resource.Wood, 1));
        Assert.Equal(0, smelter.FreeInputSpace(Resource.Stone));
    }

    [Fact]
    public void OrdinaryExtractor_RejectsInputDeposits()
    {
        var grid = new TileGrid(4, 4, Biome.Hills);
        var world = new GameWorld(grid);
        var mine = (Extractor)world.AddStructure(new Extractor(StructureKind.Mine, new TileCoord(1, 1)));
        Assert.False(mine.IsRefiner);
        Assert.Equal(0, mine.DepositInput(Resource.Ore, 5));
        Assert.Equal(0, mine.AffordableBatches());
    }

    [Fact]
    public void Smelter_Razed_SpillsOutputAndInputs()
    {
        var (sim, smelter) = MakeSmelter(workers: 0);
        Feed(smelter, ore: 6, wood: 3);
        smelter.Buffer = 4;

        SiegeDamage.RazeStructure(sim, smelter);

        var pile = sim.World.GroundResources[smelter.At];
        Assert.Equal(4, pile[Resource.Iron]);
        Assert.Equal(6, pile[Resource.Ore]);
        Assert.Equal(3, pile[Resource.Wood]);
        Assert.IsType<Rubble>(sim.World.Structures[smelter.At]);
    }

    [Fact]
    public void Smelter_PlacedAnywhere_BuildsAsExtractor()
    {
        // No RequiredBiome: grassland is fine. The site completes into an
        // Extractor of kind Smelter via BuildCompleteEvent's dispatch.
        var grid = new TileGrid(6, 6, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(0, 0)) { OwnerId = 0 });
        var sim = new Simulation(world, seed: 3);
        var at = new TileCoord(2, 2);

        sim.SubmitIntent(0, new PlaceSiteIntent(at, StructureKind.Smelter) { PlayerId = 0 });
        sim.Run(until: 0);
        var site = Assert.IsType<ConstructionSite>(sim.World.Structures[at]);
        Assert.Equal(StructureKind.Smelter, site.TargetKind);

        // Hand-deliver materials and a builder; let the build run out.
        foreach (var (r, n) in site.Required) CargoTransfer.DepositInto(sim, site, r, n);
        world.AddUnit(new Unit(9, at) { Role = UnitRole.Builder, OwnerId = 0 });
        sim.SubmitIntent(0, new AssignBuildersIntent(at, new[] { 9 }) { PlayerId = 0 });
        sim.Run(until: site.BuildDurationTicks + 1);

        var built = Assert.IsType<Extractor>(sim.World.Structures[at]);
        Assert.Equal(StructureKind.Smelter, built.Kind);
        Assert.True(built.IsRefiner);
    }

    [Fact]
    public void Workshop_And_Smithy_Build_AsStorage()
    {
        var grid = new TileGrid(6, 6, Biome.Grassland);
        var world = new GameWorld(grid);
        Assert.IsAssignableFrom<StorageStructure>(world.AddStructure(new Workshop(new TileCoord(1, 1))));
        Assert.IsAssignableFrom<StorageStructure>(world.AddStructure(new Smithy(new TileCoord(2, 2))));
        Assert.True(StructureCatalog.Spec(StructureKind.Workshop).IsPlayerBuildable);
        Assert.True(StructureCatalog.Spec(StructureKind.Smithy).IsPlayerBuildable);
        // The Smithy must be affordable before the first ingot exists.
        Assert.False(StructureCatalog.Spec(StructureKind.Smithy).BuildCost.ContainsKey(Resource.Iron));
    }

    // ---- persistence ----

    [Fact]
    public void Snapshot_RoundTrips_ArmedAndDormantSmelters()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(0, 0)) { OwnerId = 0 });
        var armedAt = new TileCoord(2, 2);
        var dormantAt = new TileCoord(5, 5);
        var armed = (Extractor)world.AddStructure(new Extractor(StructureKind.Smelter, armedAt) { OwnerId = 0 });
        var dormant = (Extractor)world.AddStructure(new Extractor(StructureKind.Smelter, dormantAt) { OwnerId = 0 });
        world.AddStructure(new Workshop(new TileCoord(6, 1)) { OwnerId = 0 }).Deposit(Resource.Cart, 1);
        world.AddStructure(new Smithy(new TileCoord(1, 6)) { OwnerId = 0 }).Deposit(Resource.Iron, 3);
        var sim = new Simulation(world, seed: 7);

        armed.DepositInput(Resource.Ore, 4 * SmelterSpec.InputCost[Resource.Ore]);
        armed.DepositInput(Resource.Wood, 4 * SmelterSpec.InputCost[Resource.Wood]);
        // A Farmer, not a Miner: base rate 1, so exactly one batch per period.
        world.AddUnit(new Unit(1, armedAt) { Role = UnitRole.Farmer, OwnerId = 0 });
        sim.SubmitIntent(0, new AssignWorkersIntent(armedAt, new[] { 1 }));
        dormant.DepositInput(Resource.Ore, 1); // half a batch — dormant on purpose
        dormant.Buffer = 2;
        sim.Run(until: 0);
        Assert.True(armed.TickArmed);
        Assert.False(dormant.TickArmed);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 7);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var rArmed = Assert.IsType<Extractor>(restored.World.Structures[armedAt]);
        Assert.Equal(4 * SmelterSpec.InputCost[Resource.Ore], rArmed.InputOf(Resource.Ore));
        Assert.Equal(4 * SmelterSpec.InputCost[Resource.Wood], rArmed.InputOf(Resource.Wood));
        Assert.True(rArmed.TickArmed);
        Assert.Equal(armed.NextProductionTickSeq, rArmed.NextProductionTickSeq);
        var rDormant = Assert.IsType<Extractor>(restored.World.Structures[dormantAt]);
        Assert.Equal(1, rDormant.InputOf(Resource.Ore));
        Assert.Equal(2, rDormant.Buffer);

        // Both worlds produce the same iron on the same tick after restore.
        sim.Run(until: Period);
        restored.Run(until: Period);
        Assert.Equal(1, rArmed.Buffer);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void TwinRun_SmelterChain_HashesEqual()
    {
        static string RunOnce()
        {
            var (sim, smelter) = MakeSmelter(workers: 2, role: UnitRole.Miner);
            Feed(smelter, ore: 20, wood: 10);
            var stockAt = new TileCoord(4, 3);
            sim.World.AddStructure(new Stockpile(stockAt) { OwnerId = 0 });
            sim.World.AddUnit(new Unit(50, smelter.At) { Role = UnitRole.Hauler, OwnerId = 0 });
            sim.SubmitIntent(Period + 1, new HaulIntent(50, smelter.At, stockAt, Resource.Iron));
            sim.Run(until: Period * 6);
            return Snapshot.Hash(sim);
        }
        Assert.Equal(RunOnce(), RunOnce());
    }
}
