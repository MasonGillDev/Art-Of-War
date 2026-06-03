using Microsoft.Data.Sqlite;

namespace Sim.Persistence;

// Durable store of pure-state snapshots (the binary blobs produced by
// Sim.Core.Persistence.Snapshot.Serialize). One row per tick, with a
// retention policy that prunes to the last N.
//
// Constructor accepts a connection string AND a retention count; SaveSnapshot
// applies the retention on every write (cheap: one DELETE statement).
public sealed class SqliteSnapshotStore : ISnapshotStore, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly int _retention;

    public SqliteSnapshotStore(string connectionString, int retention = 3)
    {
        if (retention < 1)
            throw new ArgumentOutOfRangeException(nameof(retention),
                "Retention must be at least 1 (keep at least the latest snapshot).");
        _retention = retention;
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
    }

    public static SqliteSnapshotStore Open(string path, int retention = 3) =>
        new($"Data Source={path};Mode=ReadWriteCreate", retention);

    public static SqliteSnapshotStore OpenInMemory(int retention = 3) =>
        new("Data Source=:memory:", retention);

    internal void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS snapshots (
                tick       INTEGER PRIMARY KEY,
                version    INTEGER NOT NULL,
                blob       BLOB    NOT NULL,
                created_at TEXT    NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void SaveSnapshot(long tick, int formatVersion, byte[] blob)
    {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO snapshots (tick, version, blob, created_at)
                VALUES ($tick, $version, $blob, $created);
            ";
            cmd.Parameters.AddWithValue("$tick", tick);
            cmd.Parameters.AddWithValue("$version", formatVersion);
            cmd.Parameters.AddWithValue("$blob", blob);
            cmd.Parameters.AddWithValue("$created",
                DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        // Retention: delete all but the latest _retention rows.
        using (var prune = _conn.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = @"
                DELETE FROM snapshots
                WHERE tick NOT IN (
                    SELECT tick FROM snapshots ORDER BY tick DESC LIMIT $keep
                );
            ";
            prune.Parameters.AddWithValue("$keep", _retention);
            prune.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public SnapshotRecord? LoadLatest()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tick, version, blob
            FROM snapshots
            ORDER BY tick DESC
            LIMIT 1;
        ";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new SnapshotRecord(
            Tick:          reader.GetInt64(0),
            FormatVersion: reader.GetInt32(1),
            Blob:          (byte[])reader.GetValue(2));
    }

    public IReadOnlyList<SnapshotRecord> ListSnapshots()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tick, version, blob
            FROM snapshots
            ORDER BY tick DESC;
        ";
        using var reader = cmd.ExecuteReader();
        var rows = new List<SnapshotRecord>();
        while (reader.Read())
        {
            rows.Add(new SnapshotRecord(
                Tick:          reader.GetInt64(0),
                FormatVersion: reader.GetInt32(1),
                Blob:          (byte[])reader.GetValue(2)));
        }
        return rows;
    }

    public void PruneToLast(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM snapshots
            WHERE tick NOT IN (
                SELECT tick FROM snapshots ORDER BY tick DESC LIMIT $keep
            );
        ";
        cmd.Parameters.AddWithValue("$keep", n);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
