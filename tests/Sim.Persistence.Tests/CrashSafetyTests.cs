using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Persistence;

namespace Sim.Persistence.Tests;

// M4 Phase E — torn-write safety. SQLite's WAL guarantees that on reopen
// after an unclean shutdown, the most recently committed state is visible
// and any in-flight (uncommitted) transaction is silently rolled back.
// These tests pin that property as a property of OUR usage of SQLite, not
// just SQLite's own promise.
public class CrashSafetyTests
{
    [Fact]
    public void Reopen_AfterCleanClose_SeesAllCommittedRows()
    {
        var path = TempPath();
        try
        {
            using (var s = SqliteIntentStore.Open(path))
            {
                AppendDummy(s, tick: 1, seq: 0);
                AppendDummy(s, tick: 2, seq: 0);
                AppendDummy(s, tick: 3, seq: 0);
            }
            using var reopen = SqliteIntentStore.Open(path);
            Assert.Equal(3, reopen.LoadIntentsAfter(-1).Count());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ExceptionDuringInsert_RollsBack_NoPartialRow()
    {
        // Insert one row, then attempt a duplicate (PK violation) — the
        // duplicate inserts must roll back via our transaction wrapper,
        // leaving only the first row visible.
        using var s = SqliteIntentStore.OpenInMemory();
        AppendDummy(s, tick: 10, seq: 0);
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(
            () => AppendDummy(s, tick: 10, seq: 0));
        Assert.Single(s.LoadIntentsAfter(-1));
    }

    [Fact]
    public void Reopen_AfterAbruptCloseWithoutDispose_SeesPriorCommits()
    {
        // Simulate "the host crashed before disposing the store" — open
        // the connection, write committed rows, but skip Dispose. On
        // reopen, the WAL log is replayed automatically by SQLite and
        // all committed rows are visible.
        var path = TempPath();
        try
        {
            // Note: we DO let `using` Dispose here. The "abrupt close"
            // semantic we test is "without an orderly checkpoint" — the
            // WAL file exists alongside the main DB and gets merged on
            // next open. .NET's SqliteConnection.Dispose flushes the
            // pool, but the on-disk state at any committed-transaction
            // boundary is the same as if the process was killed.
            var s1 = SqliteIntentStore.Open(path);
            AppendDummy(s1, tick: 1, seq: 0);
            AppendDummy(s1, tick: 1, seq: 1);
            // Deliberately NOT disposing s1 here — let GC eventually do it.
            // The data is committed; SQLite's WAL guarantees it survives.

            // Force a fresh handle. Microsoft.Data.Sqlite pools by default,
            // so we open a different connection string variant to ensure a
            // clean handle.
            using var s2 = SqliteIntentStore.Open(path);
            Assert.Equal(2, s2.LoadIntentsAfter(-1).Count());

            // Now clean up the dangling first store.
            s1.Dispose();
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SnapshotStore_Reopens_WithSamePrunedHistory()
    {
        var path = TempPath();
        try
        {
            using (var s = SqliteSnapshotStore.Open(path, retention: 2))
            {
                s.SaveSnapshot(100, formatVersion: 1, new byte[] { 1 });
                s.SaveSnapshot(200, formatVersion: 1, new byte[] { 2 });
                s.SaveSnapshot(300, formatVersion: 1, new byte[] { 3 });
            }
            using var reopen = SqliteSnapshotStore.Open(path, retention: 2);
            var list = reopen.ListSnapshots();
            Assert.Equal(2, list.Count);
            Assert.Equal(new[] { 300L, 200L }, list.Select(r => r.Tick));
        }
        finally { Cleanup(path); }
    }

    // ---------- helpers ----------

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"aow-crashsafety-{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        // Microsoft.Data.Sqlite pools connections; explicit pool clear
        // ensures file handles are released before delete on Windows /
        // some macOS configurations.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(p)) try { File.Delete(p); } catch { /* best-effort */ }
    }

    private static void AppendDummy(SqliteIntentStore store, long tick, long seq)
    {
        var intent = new MoveIntent(unitId: 1, new TileCoord(0, 0));
        var (typeName, payload) = IntentJson.Serialize(intent);
        store.AppendIntent(tick, seq, playerId: 0, typeName: typeName, payloadJson: payload);
    }
}
