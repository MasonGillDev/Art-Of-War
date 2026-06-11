using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// The E3 scenario from the revised M1 doc, end-to-end:
//   1. Build a LumberCamp on Forest.
//   2. Place a Farm construction site on Grassland (no materials yet).
//   3. Staff the LumberCamp with a Lumberjack.
//   4. Wait for production to fill enough Wood for the Farm.
//   5. Haul Wood from LumberCamp to Farm-site → deposit triggers build start.
//   6. Wait for the Farm to complete.
//   7. Staff the Farm with a Farmer.
//   8. Wait for Food to appear in the Farm's buffer.
//
// This composes every cross-system hook the milestone built:
//   - AssignWorkers → ProductionTickEvent (Phase D)
//   - HaulPickupEvent.Apply → ArmIfDormant (Phase D)
//   - HaulDepositEvent.Apply → StartOrResume (Phase C)
//   - BuildCompleteEvent → Extractor exists for next step (Phase C)
//   - AssignWorkers on the new Farm → ProductionTickEvent (Phase D)
public class EndToEndLoopTests
{
    private const int WorldSize = 12;
    private static readonly TileCoord CastleAt   = new(0, 0);
    private static readonly TileCoord CampAt     = new(3, 3);
    private static readonly TileCoord FarmSiteAt = new(6, 0); // Grassland, not on Forest band

    private static (Simulation sim, ConstructionSite farmSite) Bootstrap()
    {
        var grid = new TileGrid(WorldSize, WorldSize, Biome.Grassland);
        // M15: the LumberCamp claims ClaimCount Forest tiles in range —
        // paint its full claim box (derived from the spec), not one tile.
        var paintSpec = StructureCatalog.Spec(StructureKind.LumberCamp);
        for (var dy = -paintSpec.ClaimRange; dy <= paintSpec.ClaimRange; dy++)
            for (var dx = -paintSpec.ClaimRange; dx <= paintSpec.ClaimRange; dx++)
                grid.SetBiome(new TileCoord(CampAt.X + dx, CampAt.Y + dy), Biome.Forest);
        var world = new GameWorld(grid);

        // Castle holds enough Wood to build the LumberCamp directly.
        var castle = world.AddStructure(new Castle(CastleAt));
        var lumberCampSpec = StructureCatalog.Spec(StructureKind.LumberCamp);
        foreach (var (r, n) in lumberCampSpec.BuildCost) castle.Deposit(r, n);

        var sim = new Simulation(world, seed: 0xE2E);

        // Units:
        //   1 Builder (at the LumberCamp tile, will build it)
        //   2 Lumberjack (at the LumberCamp tile, will staff once built)
        //   3 Hauler (at the LumberCamp tile, will haul Wood to the Farm site)
        //   4 Builder (at the Farm-site tile, will build the Farm when materials arrive)
        //   5 Farmer (at the Farm-site tile, will staff the Farm once built)
        world.AddUnit(new Unit(1, CampAt)     { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, CampAt)     { Role = UnitRole.Lumberjack });
        world.AddUnit(new Unit(3, CampAt)     { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(4, FarmSiteAt) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(5, FarmSiteAt) { Role = UnitRole.Farmer });

        // Pre-deposit LumberCamp's build cost directly onto its construction
        // site (Phase E proper would haul from castle; we shortcut for clarity).
        var campSite = world.AddStructure(new ConstructionSite(CampAt, StructureKind.LumberCamp));
        foreach (var (r, n) in lumberCampSpec.BuildCost) campSite.Deposit(r, n);

        // Farm site goes up empty — the haul has to fill it.
        var farmSite = world.AddStructure(new ConstructionSite(FarmSiteAt, StructureKind.Farm));

        return (sim, farmSite);
    }

    [Fact]
    public void FullLoop_LumberCampThenFarm_ProducesFood()
    {
        var (sim, farmSite) = Bootstrap();
        var farmSpec = StructureCatalog.Spec(StructureKind.Farm);
        var campSpec = StructureCatalog.Spec(StructureKind.LumberCamp);
        var farmWoodNeed = farmSpec.BuildCost[Resource.Wood];

        // Step 1: assign builder to LumberCamp site. Build runs to completion.
        sim.SubmitIntent(0, new AssignBuildersIntent(CampAt, new[] { 1 }));
        sim.Run();
        Assert.IsType<Extractor>(sim.World.Structures[CampAt]);

        // Step 2: staff the LumberCamp.
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(CampAt, new[] { 2 }));

        // Step 3: run until camp has enough Wood for the Farm.
        var camp = (Extractor)sim.World.Structures[CampAt];
        var safetyTick = sim.Now + 10_000;
        while (camp.Buffer < farmWoodNeed && sim.Now < safetyTick)
            sim.Run(until: sim.Now + campSpec.ProductionPeriodTicks);
        Assert.True(camp.Buffer >= farmWoodNeed, "LumberCamp never produced enough Wood");

        // Step 4: haul Wood from camp to Farm site. Pickup triggers
        // ArmIfDormant (if dormant); deposit triggers StartOrResume on
        // the Farm site (builder 4 is already on the site tile, Idle).
        Assert.False(farmSite.IsActive);
        sim.SubmitIntent(sim.Now, new HaulIntent(3, CampAt, FarmSiteAt, Resource.Wood));
        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(FarmSiteAt, new[] { 4 }));
        sim.Run();

        Assert.IsType<Extractor>(sim.World.Structures[FarmSiteAt]);
        var farm = (Extractor)sim.World.Structures[FarmSiteAt];
        Assert.Equal(StructureKind.Farm, farm.Kind);

        // Step 5: staff the Farm with Unit 5 (the Farmer).
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(FarmSiteAt, new[] { 5 }));
        sim.Run(until: sim.Now + farmSpec.ProductionPeriodTicks * 2);

        Assert.True(farm.Buffer > 0, "Farm did not produce any Food");
        Assert.Equal(Resource.Food, farm.Spec.OutputResource);
    }

    [Fact]
    public void FullLoop_TwinRunHashesMatch()
    {
        Simulation Run()
        {
            var (sim, farmSite) = Bootstrap();
            var farmSpec = StructureCatalog.Spec(StructureKind.Farm);
            var campSpec = StructureCatalog.Spec(StructureKind.LumberCamp);
            var farmWoodNeed = farmSpec.BuildCost[Resource.Wood];

            sim.SubmitIntent(0, new AssignBuildersIntent(CampAt, new[] { 1 }));
            sim.Run();
            sim.SubmitIntent(sim.Now, new AssignWorkersIntent(CampAt, new[] { 2 }));

            var camp = (Extractor)sim.World.Structures[CampAt];
            var safetyTick = sim.Now + 10_000;
            while (camp.Buffer < farmWoodNeed && sim.Now < safetyTick)
                sim.Run(until: sim.Now + campSpec.ProductionPeriodTicks);

            sim.SubmitIntent(sim.Now, new HaulIntent(3, CampAt, FarmSiteAt, Resource.Wood));
            sim.SubmitIntent(sim.Now, new AssignBuildersIntent(FarmSiteAt, new[] { 4 }));
            sim.Run();

            sim.SubmitIntent(sim.Now, new AssignWorkersIntent(FarmSiteAt, new[] { 5 }));
            sim.Run(until: sim.Now + farmSpec.ProductionPeriodTicks * 2);
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }
}
