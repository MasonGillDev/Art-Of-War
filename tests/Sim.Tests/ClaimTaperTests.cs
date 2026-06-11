using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Tests;

// M15 Phase 5 — production taper: output = ceil(workerRate × inBand /
// ClaimCount). Claim tiles are pushed out of band via SEEDED fertility
// (staggered — homogeneous claims cross bands simultaneously and would
// hide the taper). All expectations derive from catalog + config.
public class ClaimTaperTests
{
    // Small-scale test config (the established pattern — production
    // defaults carry gameplay pacing).
    private static readonly BiomeDegradationConfig Cfg = new(
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

    // Forest world, staffed camp armed via the real path (lazy auto-claim).
    private static (Simulation sim, Extractor camp) MakeProducingCamp()
    {
        var grid = new TileGrid(9, 9, Biome.Forest);
        var world = new GameWorld(
            grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
            new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
            Cfg);
        var camp = (Extractor)world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(4, 4)));
        var sim = new Simulation(world, seed: 1);
        var u = world.AddUnit(new Unit(1, camp.At) { Role = UnitRole.Lumberjack });
        camp.Workers.Add(u.Id);
        camp.ArmIfDormant(sim);
        Assert.Equal(camp.Spec.ClaimCount, camp.ClaimTiles.Count);
        return (sim, camp);
    }

    // The worker rate of one preferred-role worker, from the catalog.
    private static long WorkerRate(Extractor camp) =>
        (long)camp.Spec.BaseRatePerWorker * camp.Spec.RoleBonusNumerator / camp.Spec.RoleBonusDenominator;

    private static int Ceil(long rate, int inBand, int count) =>
        (int)((rate * inBand + count - 1) / count);

    // Push `k` claim tiles below the Forest band by seeding stored
    // fertility in the Grassland band (test seam — anchored at `now`).
    private static void Exhaust(Simulation sim, Extractor camp, int k)
    {
        for (var i = 0; i < k; i++)
            sim.World.Fertility[camp.ClaimTiles[i]] = new Fertility(
                deviation: Cfg.GrasslandBaseline - Cfg.ForestBaseline, lastUpdateTick: sim.Now);
    }

    [Theory]
    [InlineData(0)]   // full claim → full rate
    [InlineData(2)]   // staggered partial exhaustion
    [InlineData(5)]   // one tile left → ceil keeps a trickle ≥ 1
    public void Output_ScalesWith_InBandClaimCount(int exhausted)
    {
        var (sim, camp) = MakeProducingCamp();
        Assert.True(exhausted < camp.Spec.ClaimCount, "leave at least one tile in band");
        Exhaust(sim, camp, exhausted);

        sim.Run(until: camp.Spec.ProductionPeriodTicks);   // exactly one tick

        var inBand = camp.Spec.ClaimCount - exhausted;
        var expected = Ceil(WorkerRate(camp), inBand, camp.Spec.ClaimCount);
        Assert.True(expected >= 1, "ceil must keep a trickle while land lives");
        Assert.Equal(expected, camp.Buffer);
        Assert.True(camp.TickArmed);   // still producing — not exhausted
    }

    [Fact]
    public void FullyExhausted_GoesDormant_NotZeroLoop()
    {
        // All claim tiles out of band → the dormancy guard fires (no
        // zero-output self-reschedule loop).
        var (sim, camp) = MakeProducingCamp();
        Exhaust(sim, camp, camp.Spec.ClaimCount);

        sim.Run(until: camp.Spec.ProductionPeriodTicks * 3);

        Assert.False(camp.TickArmed);
        Assert.Equal(0, camp.Buffer);
    }

    [Fact]
    public void DriftClamp_OversizedClaimList_DoesNotAmplify()
    {
        // A serialized claim list larger than the (hypothetically retuned-
        // down) ClaimCount must not produce MORE than the base rate:
        // inBand clamps to ClaimCount. Simulate by hand-adding extra
        // in-band claim tiles beyond the catalog count.
        var (sim, camp) = MakeProducingCamp();
        camp.ClaimTiles.Add(new TileCoord(1, 1));
        camp.ClaimTiles.Add(new TileCoord(1, 2));

        sim.Run(until: camp.Spec.ProductionPeriodTicks);

        Assert.Equal((int)WorkerRate(camp), camp.Buffer);   // exactly base rate, no amplification
    }
}
