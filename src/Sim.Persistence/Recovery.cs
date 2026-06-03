using Sim.Core.Engine;
using Sim.Core.Persistence;

namespace Sim.Persistence;

// The recovery orchestrator. Combines a snapshot store, an intent store,
// and a seed into a ready-to-run Simulation that's an exact resumption of
// whatever was last running against the same data dir.
//
// Recovery anchors on the LATEST SNAPSHOT — never on genesis. Pre-snapshot
// intents can be deleted from the log without breaking live recovery
// (proven by RecoveryTests.PreSnapshotIntentsCanBeDeleted_StillRecovers).
//
// Cold-start policy: throws RecoveryException if no snapshot exists. The
// host owns the cold-start fallback (Genesis.Build + initial snapshot).
public static class Recovery
{
    // Loads the latest snapshot, restores the sim from it (Snapshot.Restore
    // internally calls RegenerateQueue.From, rebuilding the in-flight queue
    // from per-entity anchors), then replays every intent with
    // tick > snapshot.tick by submitting each via the standard sim path.
    //
    // The returned sim is ready to run forward. If targetTick is supplied,
    // also calls sim.Run(until: targetTick).
    public static Simulation Recover(
        IIntentStore intents,
        ISnapshotStore snapshots,
        ulong seed,
        long? targetTick = null)
    {
        var snap = snapshots.LoadLatest()
            ?? throw new RecoveryException(
                "no snapshot to recover from — host should fall back to genesis");

        // Snapshot.Restore validates the magic + version header and throws
        // InvalidDataException on mismatch. We let that bubble — recovery
        // can't paper over a version mismatch, the operator must run
        // snapshot-on-deploy under the producing code.
        Simulation sim;
        try
        {
            sim = Snapshot.Restore(snap.Blob, seed);
        }
        catch (Exception ex) when (ex is InvalidDataException)
        {
            throw new RecoveryException(
                $"failed to restore snapshot at tick {snap.Tick}: {ex.Message}", ex);
        }

        // Replay the intent tail. Each stored intent gets a fresh Seq from
        // the post-restore counter — that's correct: regenerated in-flight
        // events (from snapshot anchors) carry their original older Seqs
        // via Snapshot.Restore → RegenerateQueue.From; the post-restore
        // intent-tail events get newer Seqs continuing the monotonic
        // sequence, so within-tick ordering across the snapshot boundary
        // remains deterministic.
        foreach (var row in intents.LoadIntentsAfter(snap.Tick))
        {
            var intent = IntentJson.Deserialize(row.TypeName, row.PayloadJson);
            sim.SubmitIntent(row.Tick, intent);
        }

        if (targetTick.HasValue)
            sim.Run(until: targetTick.Value);

        return sim;
    }
}
