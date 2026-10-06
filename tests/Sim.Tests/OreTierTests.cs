using Sim.Core;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Logistics;
using Sim.Core.Mining;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M51 — ore tiers: copper, iron, steel (docs/m51-ore-tiers-spec.md). Veins are ranked
// by remoteness into three ores; one mine per ore, each built with the metal below; one
// smelter for every ore, the best first; a straight stronger sword per metal, and a
// better sword swaps in. Expectations read the catalogs and VeinConfig, never constants.
public class OreTierTests
{
    // ---- which ore a vein holds ------------------------------------------------

    private static GameWorld Generated(VeinConfig? veins = null)
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 128, MapHeight = 128, MapSeed = 7, AiPlayers = 2 });
        var spec = veins is null ? build.Spec : build.Spec with { Veins = veins };
        return Genesis.Build(spec);
    }

    [Fact]
    public void Veins_AreRankedByRemoteness_IntoTheConfiguredShares()
    {
        var world = Generated();
        var cfg = world.VeinConfig;
        var n = world.Veins.Count;
        Assert.True(n > 20, $"the test map should have veins ({n})");
        Assert.Equal(world.Veins, world.VeinOre.Keys);

        int Count(Resource ore) => world.VeinOre.Values.Count(o => o == ore);
        Assert.Equal(n * cfg.SteelSharePercent / 100, Count(Resource.SteelOre));
        Assert.Equal(n * cfg.IronSharePercent / 100, Count(Resource.IronOre));
        Assert.Equal(n - Count(Resource.SteelOre) - Count(Resource.IronOre), Count(Resource.CopperOre));

        // Rarer ore lies farther out: no copper vein is more remote than an iron
        // one, and no iron vein more remote than a steel one.
        int Remote(TileCoord t) => world.Wilderness.MinutesAt(t) ?? int.MaxValue;
        int Max(Resource ore) => world.VeinOre.Where(kv => kv.Value == ore).Max(kv => Remote(kv.Key));
        int Min(Resource ore) => world.VeinOre.Where(kv => kv.Value == ore).Min(kv => Remote(kv.Key));
        Assert.True(Max(Resource.CopperOre) <= Min(Resource.IronOre));
        Assert.True(Max(Resource.IronOre) <= Min(Resource.SteelOre));
    }

    [Fact]
    public void TheShares_AreADial()
    {
        var allCopper = Generated(new VeinConfig(Seed: 7, IronSharePercent: 0, SteelSharePercent: 0));
        Assert.All(allCopper.VeinOre.Values, o => Assert.Equal(Resource.CopperOre, o));

        var noCopper = Generated(new VeinConfig(Seed: 7, IronSharePercent: 50, SteelSharePercent: 50));
        Assert.DoesNotContain(Resource.CopperOre, noCopper.VeinOre.Values);
    }

    [Fact]
    public void Genesis_IsDeterministic_AndTheOreSurvivesASnapshot()
    {
        var a = Generated();
        var b = Generated();
        Assert.Equal(a.VeinOre, b.VeinOre);

        var build = WorldFactory.Build(new ServerOptions { MapWidth = 128, MapHeight = 128, MapSeed = 7, AiPlayers = 2 });
        var sim = new Simulation(build.Spec, seed: 5);
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 5);
        Assert.Equal(sim.World.VeinOre, restored.World.VeinOre);
        Assert.Equal(sim.World.VeinConfig, restored.World.VeinConfig);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    // ---- surveys and mines -----------------------------------------------------

    private static readonly TileCoord Keep = new(4, 4);
    private static readonly TileCoord Slope = new(14, 6);

    // A meadow with a mountain block (x 12..18, y 2..10), a castle and a Miner, the
    // map explored; veins and their ores laid by hand.
    private static Simulation MountainWorld(params (TileCoord Tile, Resource Ore)[] veins)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        for (var y = 2; y <= 10; y++)
            for (var x = 12; x <= 18; x++)
                grid.SetBiome(new TileCoord(x, y), Biome.Mountain);
        var world = new GameWorld(grid);
        foreach (var (t, ore) in veins)
        {
            world.Veins.Add(t);
            world.VeinOre[t] = ore;
        }
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Miner, OwnerId = 0 });
        world.NextUnitId = 2;
        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;
        return new Simulation(world, seed: 11);
    }

    [Fact]
    public void ASurvey_ReportsTheOre()
    {
        var vein = new TileCoord(15, 7);
        var sim = MountainWorld((vein, Resource.IronOre));
        sim.SubmitIntent(sim.Now, new SurveyIntent(1, Slope) { PlayerId = 0 });
        sim.Run(sim.Now + 20 * Time.Day);

        var report = Assert.Single(sim.ResolvedLog.OfType<SurveyReportEvent>());
        Assert.Equal(vein, report.Vein);
        Assert.Equal(Resource.IronOre, report.Ore);
        Assert.Equal(Resource.IronOre, Veins.OreAt(sim.World, vein));
    }

    [Theory]
    [InlineData(StructureKind.CopperMine, Resource.CopperOre, true)]
    [InlineData(StructureKind.CopperMine, Resource.IronOre, false)]
    [InlineData(StructureKind.IronMine, Resource.IronOre, true)]
    [InlineData(StructureKind.IronMine, Resource.SteelOre, false)]
    [InlineData(StructureKind.SteelMine, Resource.SteelOre, true)]
    [InlineData(StructureKind.SteelMine, Resource.CopperOre, false)]
    public void AMine_StandsOnlyOnItsOwnOre(StructureKind mine, Resource ore, bool allowed)
    {
        var vein = new TileCoord(15, 7);
        var sim = MountainWorld((vein, ore));
        sim.World.KnownVeins[0] = new SortedSet<TileCoord>(TileOrder.Instance) { vein };

        var outcome = new PlaceSiteIntent(vein, mine) { PlayerId = 0 }.Resolve(sim);

        Assert.Equal(allowed, outcome.IsApplied);
        if (!allowed) Assert.Contains("holds", outcome.Reason);
    }

    [Fact]
    public void EachMine_IsBuiltWithTheMetalBelow_AndMinesItsOre()
    {
        var copper = StructureCatalog.Spec(StructureKind.CopperMine);
        var iron = StructureCatalog.Spec(StructureKind.IronMine);
        var steel = StructureCatalog.Spec(StructureKind.SteelMine);

        Assert.DoesNotContain(copper.BuildCost.Keys, r => r is Resource.Bronze or Resource.Iron or Resource.Steel);
        Assert.True(iron.BuildCost.GetValueOrDefault(Resource.Bronze) > 0);
        Assert.True(steel.BuildCost.GetValueOrDefault(Resource.Iron) > 0);
        Assert.Equal(Resource.CopperOre, copper.OutputResource);
        Assert.Equal(Resource.IronOre, iron.OutputResource);
        Assert.Equal(Resource.SteelOre, steel.OutputResource);
    }

    // The whole life of a higher mine: placed, built (its bronze delivered), standing as
    // an extractor of its ore, mining, and back from a snapshot. Every kind switch that
    // builds, restores or draws a structure must know the new mines.
    [Theory]
    [InlineData(StructureKind.IronMine, Resource.IronOre)]
    [InlineData(StructureKind.SteelMine, Resource.SteelOre)]
    public void AHigherMine_IsBuilt_Mines_AndSurvivesASnapshot(StructureKind kind, Resource ore)
    {
        var vein = new TileCoord(15, 7);
        var sim = MountainWorld((vein, ore));
        sim.World.KnownVeins[0] = new SortedSet<TileCoord>(TileOrder.Instance) { vein };
        Assert.True(new PlaceSiteIntent(vein, kind) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var site = (ConstructionSite)sim.World.Structures[vein];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 0; i < site.RequiredBuilderCount; i++)
        {
            var b = new Unit(100 + i, vein) { Role = UnitRole.Builder, OwnerId = 0 };
            sim.World.AddUnit(b);
            b.TrySetActivity(Activity.Building, vein);
        }
        site.StartOrResume(sim);
        sim.Run(sim.Now + site.BuildDurationTicks + 2);

        var mine = Assert.IsType<Extractor>(sim.World.Structures[vein]);
        Assert.Equal(kind, mine.Kind);
        Assert.NotNull(Sim.Core.Battlefields.Footprints.Pattern(kind));

        var miner = sim.World.Units[1];
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(vein, new[] { miner.Id }) { PlayerId = 0 });
        sim.Run(sim.Now + 10 * Time.Day);
        Assert.True(mine.OutputOf(ore) > 0, $"the {kind} mined no {ore}");

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 11);
        var back = Assert.IsType<Extractor>(restored.World.Structures[vein]);
        Assert.Equal(kind, back.Kind);
        Assert.Equal(mine.Output, back.Output);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    // ---- one smelter, every ore, the best first ------------------------------------

    private static StructureSpec SmelterSpec => StructureCatalog.Spec(StructureKind.Smelter);
    private static RefineRecipe RecipeFor(Resource bar) => SmelterSpec.Recipes.Single(r => r.Output == bar);

    private static (Simulation sim, Extractor smelter) Smelter()
    {
        var world = new GameWorld(new TileGrid(8, 8, Biome.Grassland));
        world.Players[0] = new Player(0);
        var at = new TileCoord(3, 3);
        var smelter = (Extractor)world.AddStructure(new Extractor(StructureKind.Smelter, at) { OwnerId = 0 });
        var sim = new Simulation(world, seed: 1);
        world.AddUnit(new Unit(1, at) { Role = UnitRole.Farmer, OwnerId = 0 });
        sim.SubmitIntent(0, new AssignWorkersIntent(at, new[] { 1 }));
        return (sim, smelter);
    }

    private static void Stock(Extractor smelter, Resource bar, int batches)
    {
        foreach (var (r, n) in RecipeFor(bar).Inputs)
            Assert.Equal(n * batches, smelter.DepositInput(r, n * batches));
    }

    [Theory]
    [InlineData(Resource.Bronze, Resource.CopperOre)]
    [InlineData(Resource.Iron, Resource.IronOre)]
    [InlineData(Resource.Steel, Resource.SteelOre)]
    public void EachOre_SmeltsToItsBar_AtItsWoodPrice(Resource bar, Resource ore)
    {
        var (sim, smelter) = Smelter();
        Stock(smelter, bar, 1);
        sim.Run(SmelterSpec.ProductionPeriodTicks);

        Assert.Equal(1, smelter.OutputOf(bar));
        Assert.Equal(0, smelter.InputOf(ore));
        Assert.Equal(0, smelter.InputOf(Resource.Wood));
    }

    [Fact]
    public void BetterOre_BurnsMoreWood()
    {
        int Wood(Resource bar) => RecipeFor(bar).Inputs[Resource.Wood];
        Assert.True(Wood(Resource.Bronze) < Wood(Resource.Iron));
        Assert.True(Wood(Resource.Iron) < Wood(Resource.Steel));
    }

    [Fact]
    public void WithSeveralOres_TheBestGoesFirst_ThenTheNext()
    {
        var (sim, smelter) = Smelter();
        Stock(smelter, Resource.Bronze, 1);
        Stock(smelter, Resource.Iron, 1);

        sim.Run(SmelterSpec.ProductionPeriodTicks);
        Assert.Equal(1, smelter.OutputOf(Resource.Iron));
        Assert.Equal(0, smelter.OutputOf(Resource.Bronze));

        sim.Run(2 * SmelterSpec.ProductionPeriodTicks);
        Assert.Equal(1, smelter.OutputOf(Resource.Bronze));
        Assert.Equal(2, smelter.Buffer);
    }

    [Fact]
    public void ARazedSmelter_SpillsEveryBar()
    {
        var (sim, smelter) = Smelter();
        smelter.AddOutput(Resource.Bronze, 2);
        smelter.AddOutput(Resource.Steel, 1);

        Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, smelter);

        var pile = sim.World.GroundResources[smelter.At];
        Assert.Equal(2, pile[Resource.Bronze]);
        Assert.Equal(1, pile[Resource.Steel]);
    }

    [Fact]
    public void MixedBars_SurviveASnapshot()
    {
        var (sim, smelter) = Smelter();
        Stock(smelter, Resource.Steel, 1);
        Stock(smelter, Resource.Bronze, 2);
        sim.Run(SmelterSpec.ProductionPeriodTicks);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        var copy = (Extractor)restored.World.Structures[smelter.At];

        Assert.Equal(smelter.Output, copy.Output);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        sim.Run(3 * SmelterSpec.ProductionPeriodTicks);
        restored.Run(3 * SmelterSpec.ProductionPeriodTicks);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    // ---- a straight stronger sword per metal ----------------------------------------

    [Fact]
    public void EachMetal_MakesAStrongerSword()
    {
        var bronze = EquipmentCatalog.Spec(Resource.BronzeSword);
        var iron = EquipmentCatalog.Spec(Resource.IronSword);
        var steel = EquipmentCatalog.Spec(Resource.SteelSword);

        Assert.True(bronze.PowerModifier < iron.PowerModifier && iron.PowerModifier < steel.PowerModifier);
        Assert.Equal(3, iron.CraftCost[Resource.Iron]);
        Assert.Equal(3, steel.CraftCost[Resource.Steel]);
        Assert.All(new[] { bronze, iron, steel }, s => Assert.Equal(StructureKind.Smithy, s.CraftedAt));
    }

    [Fact]
    public void TheSmithy_ForgesAnIronSword_FromIron()
    {
        var world = new GameWorld(new TileGrid(8, 8, Biome.Grassland));
        world.Players[0] = new Player(0);
        var smithy = (StorageStructure)world.AddStructure(new Smithy(new TileCoord(2, 2)) { OwnerId = 0 });
        foreach (var (r, n) in EquipmentCatalog.Spec(Resource.IronSword).CraftCost) smithy.Deposit(r, n);
        var sim = new Simulation(world, seed: 1);

        Assert.True(new CraftEquipmentIntent(smithy.At, Resource.IronSword) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(1, smithy.AmountOf(Resource.IronSword));
        Assert.Equal(0, smithy.AmountOf(Resource.Iron));
    }

    private static (Simulation sim, Unit soldier, StorageStructure store) Armoury(params (Resource Item, int Count)[] stock)
    {
        var sim = new Simulation(new GameWorld(new TileGrid(8, 8, Biome.Grassland)), seed: 1);
        var store = (StorageStructure)sim.World.AddStructure(new Barracks(new TileCoord(2, 2)));
        foreach (var (item, count) in stock) store.Deposit(item, count);
        var soldier = sim.World.AddUnit(new Unit(1, store.At) { Role = UnitRole.Soldier });
        return (sim, soldier, store);
    }

    [Fact]
    public void ABetterSword_SwapsIn_AndTheOldOneGoesBackToTheStore()
    {
        var (sim, soldier, store) = Armoury((Resource.BronzeSword, 1), (Resource.IronSword, 1), (Resource.Shield, 1));
        Assert.True(new EquipUnitIntent(soldier.Id, Resource.BronzeSword) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new EquipUnitIntent(soldier.Id, Resource.Shield) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var health = soldier.Health;

        Assert.True(new EquipUnitIntent(soldier.Id, Resource.IronSword) { PlayerId = 0 }.Resolve(sim).IsApplied);

        Assert.Equal(2, soldier.Buffs.Count);   // the sword's slot was reused, the shield kept
        Assert.Contains(soldier.Buffs, b => b.Kind == EquipmentCatalog.Spec(Resource.IronSword).BuffKind);
        Assert.DoesNotContain(soldier.Buffs, b => b.Kind == EquipmentCatalog.Spec(Resource.BronzeSword).BuffKind);
        Assert.Equal(1, store.AmountOf(Resource.BronzeSword));
        Assert.Equal(0, store.AmountOf(Resource.IronSword));
        Assert.Equal(health, soldier.Health);
        Assert.Equal(
            UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower + EquipmentCatalog.Spec(Resource.IronSword).PowerModifier,
            CombatRules.EffectivePower(soldier, sim.Now));
    }

    [Theory]
    [InlineData(Resource.IronSword, Resource.IronSword)]
    [InlineData(Resource.IronSword, Resource.BronzeSword)]
    [InlineData(Resource.SteelSword, Resource.IronSword)]
    public void ASameOrLesserSword_IsRefused(Resource worn, Resource offered)
    {
        var (sim, soldier, store) = Armoury((worn, 1), (offered, 1));
        Assert.True(new EquipUnitIntent(soldier.Id, worn) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var outcome = new EquipUnitIntent(soldier.Id, offered) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Equal(1, store.AmountOf(offered));
        Assert.Single(soldier.Buffs);
    }

    [Fact]
    public void AFallenSoldier_DropsTheSwordHeCarried()
    {
        var (sim, soldier, _) = Armoury((Resource.SteelSword, 1));
        Assert.True(new EquipUnitIntent(soldier.Id, Resource.SteelSword) { PlayerId = 0 }.Resolve(sim).IsApplied);

        Equipment.DropEquipmentToGround(sim.World, soldier, soldier.Position);

        Assert.Equal(1, sim.World.GroundResources[soldier.Position][Resource.SteelSword]);
    }
}
