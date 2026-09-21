using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Tests;

// M35 Phase D — the production taper reads LIVE FERTILITY per claim tile
// (docs/environmental-fertility.md decision 2):
//   output = ceil(rate × Σ_{in-band claims} fert_t / (ClaimCount × BandBaseline))
// ClaimTaperTests still pins the M15 count form (it holds exactly at uniform
// baseline fertility); this file pins what is new: a riverside claim
// out-produces the flat rate, a tiring field slows before its band flips,
// and the drift clamp still caps the number of tiles, not the bonus.
public class FertilityTaperTests
{
    private static readonly BiomeDegradationConfig Flat = new(
        ForestBaseline:    100,
        GrasslandBaseline:  50,
        DesertBaseline:     10,
        HillsBaseline:      30,
        MountainBaseline:   60,
        WaterBaseline:       0,
        ForestThreshold:    75,
        DesertThreshold:    25,
        RecoveryAmount:      1,
        RecoveryPeriod:     30,
        DegradePeriod:    5000,   // huge: no organic degradation during the test window
        DegradeRadius:       2);

    // Forest world, staffed camp armed via the real path (lazy auto-claim),
    // `workers` lumberjacks so the rate is large enough for the weighting to
    // show through the ceil.
    private static (Simulation sim, Extractor camp) MakeProducingCamp(BiomeDegradationConfig cfg, int workers, Action<TileGrid>? shape = null)
    {
        var grid = new TileGrid(9, 9, Biome.Forest);
        shape?.Invoke(grid);
        var world = new GameWorld(
            grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
            new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
            cfg);
        var camp = (Extractor)world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(4, 4)));
        var sim = new Simulation(world, seed: 1);
        for (var i = 1; i <= workers; i++)
        {
            var u = world.AddUnit(new Unit(i, camp.At) { Role = UnitRole.Lumberjack });
            camp.Workers.Add(u.Id);
        }
        camp.ArmIfDormant(sim);
        Assert.Equal(camp.Spec.ClaimCount, camp.ClaimTiles.Count);
        return (sim, camp);
    }

    private static long WorkerRate(Extractor camp, int workers) =>
        workers * ((long)camp.Spec.BaseRatePerWorker * camp.Spec.RoleBonusNumerator / camp.Spec.RoleBonusDenominator);

    // The formula, derived from the world so the expectation is config-driven.
    private static int Expected(Simulation sim, Extractor camp, long rate, BiomeDegradationConfig cfg)
    {
        long sum = 0;
        var counted = 0;
        foreach (var t in camp.ClaimTiles)
        {
            if (counted >= camp.Spec.ClaimCount) break;
            if (BiomeDegradation.BiomeAt(sim.World, t, sim.Now, cfg) != camp.Spec.RequiredBiome) continue;
            sum += BiomeDegradation.FertilityAt(sim.World, t, sim.Now, cfg);
            counted++;
        }
        var denom = (long)camp.Spec.ClaimCount * BiomeDegradation.BaselineFertility(camp.Spec.RequiredBiome, cfg);
        return (int)((rate * sum + denom - 1) / denom);
    }

    [Fact]
    public void RiversideClaim_OutproducesTheFlatRate()
    {
        // A river down the east edge of column 5: the claim tiles in column
        // 5 are banks (+40), those in column 4 and 6 are one ring off (+20).
        var cfg = Flat with { WaterFertilityRadius = 1, WaterFertilityBonus = 40 };
        const int workers = 10;
        var (sim, camp) = MakeProducingCamp(cfg, workers, grid =>
        {
            for (var y = 0; y < 9; y++)
            {
                grid.SetRiverEdges(new TileCoord(5, y), RiverEdge.East);
                grid.SetRiverEdges(new TileCoord(6, y), RiverEdge.West);
            }
        });
        var rate = WorkerRate(camp, workers);
        var expected = Expected(sim, camp, rate, cfg);
        Assert.True(expected > rate, $"riverside claim must beat the flat rate ({expected} vs {rate})");

        sim.Run(until: camp.Spec.ProductionPeriodTicks);   // exactly one tick

        Assert.Equal(expected, camp.Buffer);
        Assert.True(camp.TickArmed);
    }

    [Fact]
    public void TiringFields_SlowOutput_BeforeTheBandFlips()
    {
        // Four claim tiles at fertility 80: still Forest (>= 75), but the
        // sum is 4 × 20 short of full, so output drops below the flat rate
        // while the M15 in-band COUNT would still say "full claim".
        const int workers = 10;
        var (sim, camp) = MakeProducingCamp(Flat, workers);
        for (var i = 0; i < 4; i++)
            sim.World.Fertility[camp.ClaimTiles[i]] = new Fertility(deviation: -20, lastUpdateTick: sim.Now);
        Assert.Equal(camp.Spec.ClaimCount, Claims.InBandClaimCount(sim.World, camp, sim.Now));

        var rate = WorkerRate(camp, workers);
        var expected = Expected(sim, camp, rate, Flat);
        Assert.True(expected < rate, $"tiring fields must slow output ({expected} vs {rate})");
        Assert.True(expected >= 1);

        sim.Run(until: camp.Spec.ProductionPeriodTicks);

        Assert.Equal(expected, camp.Buffer);
    }

    [Fact]
    public void Strength0_UniformClaim_ProducesExactlyTheM15Rate()
    {
        const int workers = 3;
        var (sim, camp) = MakeProducingCamp(Flat, workers);
        sim.Run(until: camp.Spec.ProductionPeriodTicks);
        Assert.Equal((int)WorkerRate(camp, workers), camp.Buffer);
    }

    [Fact]
    public void DriftClamp_CapsTheTileCount_NotTheRiversideBonus()
    {
        // Extra in-band tiles beyond ClaimCount are ignored (M15 clamp), but
        // the counted tiles keep their above-baseline fertility.
        var cfg = Flat with { WaterFertilityRadius = 1, WaterFertilityBonus = 40 };
        const int workers = 10;
        var (sim, camp) = MakeProducingCamp(cfg, workers, grid =>
        {
            for (var y = 0; y < 9; y++)
            {
                grid.SetRiverEdges(new TileCoord(5, y), RiverEdge.East);
                grid.SetRiverEdges(new TileCoord(6, y), RiverEdge.West);
            }
        });
        var rate = WorkerRate(camp, workers);
        var expected = Expected(sim, camp, rate, cfg);          // over the first ClaimCount tiles
        camp.ClaimTiles.Add(new TileCoord(1, 1));
        camp.ClaimTiles.Add(new TileCoord(1, 2));

        sim.Run(until: camp.Spec.ProductionPeriodTicks);

        Assert.Equal(expected, camp.Buffer);
        Assert.True(camp.Buffer > rate);
    }
}
