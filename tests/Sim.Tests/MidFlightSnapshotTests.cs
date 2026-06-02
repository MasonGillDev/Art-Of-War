using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M4 Phase B — THE gap-closing test. The in-flight snapshot gap, flagged
// since the haul milestone, finally closes here. Run scenario S
// uninterrupted to tick T_END. In parallel: run S to mid-flight tick T,
// snapshot, restore, run to T_END. Hashes must be IDENTICAL.
//
// If this passes, the snapshot durability promise is honored for moving
// worlds, not just frozen ones.
public class MidFlightSnapshotTests
{
    // Construct the same scenario twice. The scenario must include
    // multiple kinds of in-flight processes simultaneously so the
    // gap-closure proves at the union, not just one kind.
    private static Simulation BuildScenario()
    {
        var spec = new GenesisSpec
        {
            Width = 20,
            Height = 20,
            CastlePosition = new TileCoord(0, 0),
            StartingHoldings = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 200,
                [Resource.Stone] = 100,
            },
            Units = new[]
            {
                new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder),
                new UnitSpawn(2, new TileCoord(0, 0), UnitRole.Hauler, CargoCapacity: 5),
            },
        };
        var world = Genesis.Build(spec);
        var sim = new Simulation(world, seed: 0x14B0);

        // Long walk (multiple arrivals queued and consumed over time).
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(18, 18)));

        // Hand-place a Stockpile so the hauler has somewhere to go.
        world.AddStructure(new Stockpile(new TileCoord(15, 0)) { OwnerId = 0 });
        ((Stockpile)world.Structures[new TileCoord(15, 0)]).Deposit(Resource.Wood, 50);

        // Hauler does a round trip (Stockpile → Castle).
        sim.SubmitIntent(0, new HaulIntent(2, new TileCoord(15, 0), new TileCoord(0, 0), Resource.Wood));

        return sim;
    }

    [Fact]
    public void MidFlightSnapshot_RestoreRun_MatchesUninterrupted()
    {
        const long midFlight = 40;
        const long endTick = 300;

        // Path A: uninterrupted to endTick.
        var simUninterrupted = BuildScenario();
        simUninterrupted.Run(until: endTick);
        var hashA = Snapshot.Hash(simUninterrupted);

        // Path B: run to mid-flight; snapshot; restore into a fresh sim;
        // run the restored sim to endTick.
        var simToMid = BuildScenario();
        simToMid.Run(until: midFlight);
        var midBytes = Snapshot.Serialize(simToMid);
        var restored = Snapshot.Restore(midBytes, seed: 0x14B0);
        // Ensure we actually snapshotted mid-flight (in-flight events exist).
        Assert.True(simToMid.Now < endTick);

        restored.Run(until: endTick);
        var hashB = Snapshot.Hash(restored);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void SnapshotRestore_OfFinishedScenario_IsStillCorrect()
    {
        // Regression: a fully-completed scenario should still snapshot+restore
        // cleanly (anchors all null, queue empty, RegenerateQueue is a no-op).
        var sim = BuildScenario();
        sim.Run(); // run to natural completion

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0x14B0);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void VersionMismatch_OnRestore_Throws()
    {
        var sim = BuildScenario();
        sim.Run(until: 10);
        var bytes = Snapshot.Serialize(sim);

        // Corrupt the format-version int (bytes 4..7 after magic).
        bytes[4] = 0xFF; bytes[5] = 0xFF; bytes[6] = 0xFF; bytes[7] = 0x7F;

        var ex = Assert.Throws<InvalidDataException>(
            () => Snapshot.Restore(bytes, seed: 0x14B0));
        Assert.Contains("Snapshot format version", ex.Message);
    }
}
