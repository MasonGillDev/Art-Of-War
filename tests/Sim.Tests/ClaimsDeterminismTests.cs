using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M15 closure gates: the determinism contract re-proven over claims.
// Twin full-pipeline runs hash-equal; a mid-production snapshot (claims
// partially degraded, production armed) recovers to the identical
// outcome; views never perturb claim state.
public class ClaimsDeterminismTests
{
    // Small-scale demo-pace config so exhaustion happens in a short run.
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
        DegradePeriod:      40,
        DegradeRadius:       2);

    // The full pipeline as a deterministic scenario: place a camp via
    // intent (auto-claims), build it, staff it, and let it produce with
    // an auto-drain hauling stand-in until the claim exhausts.
    private static Simulation RunPipeline(long until)
    {
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            DefaultBiome = Biome.Forest,
            BiomeDegradation = Cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(4, 4), UnitRole.Builder),
                        new UnitSpawn(2, new TileCoord(4, 4), UnitRole.Lumberjack),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xC1A1);

        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(4, 4), StructureKind.LumberCamp));
        sim.Run(until: 0);
        var site = (ConstructionSite)sim.World.Structures[new TileCoord(4, 4)];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(new TileCoord(4, 4), new[] { 1 }));
        sim.Run(until: site.BuildDurationTicks);
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(new TileCoord(4, 4), new[] { 2 }));
        sim.Run(until: until);
        return sim;
    }

    [Fact]
    public void Twin_FullClaimPipeline_IdenticalHash()
    {
        var horizon = StructureCatalog.Spec(StructureKind.LumberCamp).BuildDurationTicks
                    + 60 * Cfg.DegradePeriod;   // well past claim exhaustion
        var a = RunPipeline(horizon);
        var b = RunPipeline(horizon);

        // Sanity: the pipeline really happened — claims exist on the camp.
        var camp = (Extractor)a.World.Structures[new TileCoord(4, 4)];
        Assert.Equal(camp.Spec.ClaimCount, camp.ClaimTiles.Count);

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // ====== THE MILESTONE HEADLINE ======
    [Fact]
    public void MidProduction_ClaimsPartiallyDegraded_SnapshotRoundTrip_Identical()
    {
        var build = StructureCatalog.Spec(StructureKind.LumberCamp).BuildDurationTicks;
        var midTick = build + 10 * Cfg.DegradePeriod;   // producing, claims wounded
        var endTick = build + 80 * Cfg.DegradePeriod;   // past exhaustion

        // Path A: uninterrupted.
        var a = RunPipeline(endTick);
        var hashA = Snapshot.Hash(a);

        // Path B: snapshot mid-production, restore, continue. Claims +
        // fertility anchors + the armed ProductionTick all ride the
        // snapshot; RegenerateQueue rebuilds the tick with its Seq.
        var b = RunPipeline(midTick);
        var camp = (Extractor)b.World.Structures[new TileCoord(4, 4)];
        Assert.True(camp.TickArmed, "must snapshot mid-production");
        // The arm-time ANCHOR entries (deviation 0, lastUpdate = arm tick)
        // must survive the round trip — this headline is what exposed the
        // M9 serializer dropping them (see Snapshot.WriteFertility).
        Assert.NotEmpty(b.World.Fertility);
        Assert.True(b.World.Fertility[camp.ClaimTiles[0]].LastUpdateTick > 0,
            "claim tiles must be anchored at the arm tick");
        var restored = Snapshot.Restore(Snapshot.Serialize(b), seed: 0xC1A1);
        // Round-trip identity — bisects serialization gaps from
        // event-regeneration gaps whenever this headline fails.
        Assert.Equal(Snapshot.Hash(b), Snapshot.Hash(restored));
        Assert.Equal(
            b.World.Fertility[camp.ClaimTiles[0]].LastUpdateTick,
            restored.World.Fertility[camp.ClaimTiles[0]].LastUpdateTick);
        restored.Run(until: endTick);

        Assert.Equal(hashA, Snapshot.Hash(restored));
    }

    [Fact]
    public void Views_DoNotAffectClaimState()
    {
        var build = StructureCatalog.Spec(StructureKind.LumberCamp).BuildDurationTicks;
        var sim = RunPipeline(build + 10 * Cfg.DegradePeriod);   // mid-production

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
            View.BuildPlayerView(sim.World, 0, sim.Now);
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
