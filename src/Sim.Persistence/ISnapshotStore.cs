namespace Sim.Persistence;

// Durable store of pure-state snapshots. The blob is the output of
// Sim.Core.Persistence.Snapshot.Serialize — a magic + version + canonical
// state encoding. SnapshotStore owns durability; Snapshot.cs owns format.
//
// Retention: SaveSnapshot enforces a "keep last N" policy (configurable
// in SqliteSnapshotStore's constructor). Older snapshots are pruned.
public interface ISnapshotStore
{
    void SaveSnapshot(long tick, int formatVersion, byte[] blob);
    SnapshotRecord? LoadLatest();
    IReadOnlyList<SnapshotRecord> ListSnapshots();
    void PruneToLast(int n);
}

public sealed record SnapshotRecord(long Tick, int FormatVersion, byte[] Blob);
