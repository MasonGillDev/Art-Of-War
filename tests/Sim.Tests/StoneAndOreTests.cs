using Sim.Core;
using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Tests;

// M44 — stone from Hills, ore from Mountain (docs/m44-stone-and-ore-spec.md,
// docs/stone-and-ore-land.md). Phase 1 pins: the biome→resource swap, and
// the Quarry's hill claim — exclusive land like a farm's, but land that
// NEVER wears out (slow, not scarce). Expectations derive from the catalog
// and config, never hard-coded numbers.
public class StoneAndOreTests
{
    private static readonly StructureSpec QuarrySpec = StructureCatalog.Spec(StructureKind.Quarry);
    private static readonly StructureSpec MineSpec = StructureCatalog.Spec(StructureKind.CopperMine);

    private static Simulation HillsSim(int size = 12)
    {
        var world = new GameWorld(new TileGrid(size, size, Biome.Hills));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 1);
    }

    // ---- the swap ----

    [Fact]
    public void Hills_YieldStone_Mountain_YieldsOre()
    {
        Assert.Equal(Resource.Stone, Biomes.Resource(Biome.Hills));
        Assert.Equal(Resource.CopperOre, Biomes.Resource(Biome.Mountain));
        Assert.Equal(Biome.Hills, QuarrySpec.RequiredBiome);
        Assert.Equal(Resource.Stone, QuarrySpec.OutputResource);
        Assert.Equal(Biome.Mountain, MineSpec.RequiredBiome);
        Assert.Equal(Resource.CopperOre, MineSpec.OutputResource);
    }

    [Fact]
    public void Quarry_ClaimsHills_ButNeverDegrades_MineDoesNotClaim()
    {
        Assert.True(QuarrySpec.ClaimCount > 0);
        Assert.True(QuarrySpec.ClaimRange > 0);
        Assert.Equal(0, QuarrySpec.DegradeAmount);
        Assert.Equal(0, MineSpec.ClaimCount);
        Assert.Equal(0, MineSpec.DegradeAmount);
    }

    // ---- placement ----

    [Fact]
    public void Quarry_OnMountain_Rejected_OnHills_ReservesHillClaim()
    {
        var sim = HillsSim();
        sim.World.Grid.SetBiome(new TileCoord(2, 2), Biome.Mountain);
        var onMountain = new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Quarry)
            { PlayerId = 0 }.Resolve(sim);
        Assert.False(onMountain.IsApplied);

        var at = new TileCoord(7, 7);
        var placed = new PlaceSiteIntent(at, StructureKind.Quarry) { PlayerId = 0 }.Resolve(sim);
        Assert.True(placed.IsApplied, placed.Reason);
        var site = (ConstructionSite)sim.World.Structures[at];
        Assert.Equal(QuarrySpec.ClaimCount, site.ClaimTiles.Count);
        Assert.All(site.ClaimTiles, t => Assert.Equal(Biome.Hills, sim.World.Grid.BiomeAt(t)));
    }

    [Fact]
    public void Quarry_NeedsAFullHillPocket_NoPartialClaims()
    {
        // A lone hill in a meadow can't host the claim: the land IS the cost.
        var world = new GameWorld(new TileGrid(9, 9, Biome.Grassland));
        world.Players[0] = new Player(0);
        var sim = new Simulation(world, seed: 1);
        var at = new TileCoord(4, 4);
        sim.World.Grid.SetBiome(at, Biome.Hills);
        for (var i = 0; i < QuarrySpec.ClaimCount - 1; i++)   // one tile short
            sim.World.Grid.SetBiome(new TileCoord(2 + i % 5, 2), Biome.Hills);

        var outcome = new PlaceSiteIntent(at, StructureKind.Quarry) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.False(sim.World.Structures.ContainsKey(at));
    }

    [Fact]
    public void QuarryClaim_BlocksOtherBuildings_AnyOwner()
    {
        var sim = HillsSim();
        var site = new PlaceSiteIntent(new TileCoord(5, 5), StructureKind.Quarry) { PlayerId = 0 };
        Assert.True(site.Resolve(sim).IsApplied);
        var claimed = ((ConstructionSite)sim.World.Structures[new TileCoord(5, 5)]).ClaimTiles[0];

        var rival = new PlaceSiteIntent(claimed, StructureKind.Quarry) { PlayerId = 1 }.Resolve(sim);
        Assert.False(rival.IsApplied);
        var own = new PlaceSiteIntent(claimed, StructureKind.Stockpile) { PlayerId = 0 }.Resolve(sim);
        Assert.False(own.IsApplied);
        Assert.Contains("claimed by", own.Reason);
    }

    // ---- no wear ----

    [Fact]
    public void Quarry_RunsAYear_HillsNeverWear_RateStaysFull()
    {
        var sim = HillsSim();
        var quarry = (Extractor)sim.World.AddStructure(
            new Extractor(StructureKind.Quarry, new TileCoord(5, 5)) { OwnerId = 0 });
        var u = sim.World.AddUnit(new Unit(1, quarry.At) { Role = UnitRole.Quarryman, OwnerId = 0 });
        quarry.Workers.Add(u.Id);
        quarry.ArmIfDormant(sim);   // lazy auto-claim, the hand-built path
        Assert.Equal(QuarrySpec.ClaimCount, quarry.ClaimTiles.Count);

        var fullRate = ProductionRate.PerPeriod(sim.World, quarry, sim.Now);
        var workerRate = (long)QuarrySpec.BaseRatePerWorker
            * QuarrySpec.RoleBonusNumerator / QuarrySpec.RoleBonusDenominator;
        Assert.True(fullRate >= workerRate, "the hill claim never tapers below the worker rate");

        // A year of production, emptying the buffer every period so the
        // quarry never idles on a full pile.
        var period = QuarrySpec.ProductionPeriodTicks;
        long produced = 0;
        for (var t = period; t <= Time.Year; t += period)
        {
            sim.Run(until: t);
            produced += quarry.Buffer;
            quarry.Buffer = 0;   // a hauler took it
            Assert.True(quarry.TickArmed, $"quarry went dormant at tick {t}");
        }

        var cfg = sim.World.BiomeDegradationConfig;
        foreach (var t in quarry.ClaimTiles)
        {
            Assert.Equal(Biome.Hills, BiomeDegradation.BiomeAt(sim.World, t, sim.Now, cfg));
            Assert.Equal(BiomeDegradation.BaselineFertility(Biome.Hills, cfg),
                BiomeDegradation.FertilityAt(sim.World, t, sim.Now, cfg));
            Assert.False(sim.World.Fertility.ContainsKey(t), "hills never get a fertility entry");
        }
        Assert.Equal(fullRate, ProductionRate.PerPeriod(sim.World, quarry, sim.Now));
        Assert.Equal(fullRate * (Time.Year / period), produced);
    }
}
