using Microsoft.Data.Sqlite;

namespace Sim.Persistence;

// Durable, append-only intent log backed by SQLite in WAL mode.
//
// Schema: (tick, seq, player_id, type, payload) with PRIMARY KEY (tick, seq).
// JSON payload for auditability — intent rows are rare and grep-able.
//
// Transaction discipline: AppendIntent opens a transaction, inserts, commits
// before returning. SQLite WAL ensures durability on commit (the WAL is
// fsync'd by the engine).
//
// Lifecycle: implements IDisposable. Host creates one instance per data dir
// and disposes on shutdown.
public sealed class SqliteIntentStore : IIntentStore, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteIntentStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        EnsureSchema();
    }

    public static SqliteIntentStore Open(string path)
    {
        // Mode=ReadWriteCreate so the file is created on first use; the
        // pooling default keeps the connection cached. Using DataSource as
        // a literal path is fine on every platform we target.
        return new SqliteIntentStore($"Data Source={path};Mode=ReadWriteCreate");
    }

    public static SqliteIntentStore OpenInMemory()
    {
        // ":memory:" plus Cache=Shared (if you want cross-connection
        // visibility) would let multiple connections see the same in-mem
        // DB. We use one connection per store, so plain :memory: suffices.
        return new SqliteIntentStore("Data Source=:memory:");
    }

    internal void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS intents (
                tick      INTEGER NOT NULL,
                seq       INTEGER NOT NULL,
                player_id INTEGER NOT NULL,
                type      TEXT    NOT NULL,
                payload   TEXT    NOT NULL,
                PRIMARY KEY (tick, seq)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void AppendIntent(long tick, long seq, int playerId, string typeName, string payloadJson)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO intents (tick, seq, player_id, type, payload)
            VALUES ($tick, $seq, $player, $type, $payload);
        ";
        cmd.Parameters.AddWithValue("$tick", tick);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$player", playerId);
        cmd.Parameters.AddWithValue("$type", typeName);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public IEnumerable<IntentRecord> LoadIntentsAfter(long tick)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tick, seq, player_id, type, payload
            FROM intents
            WHERE tick > $tick
            ORDER BY tick ASC, seq ASC;
        ";
        cmd.Parameters.AddWithValue("$tick", tick);
        using var reader = cmd.ExecuteReader();
        var rows = new List<IntentRecord>();
        while (reader.Read())
        {
            rows.Add(new IntentRecord(
                Tick:        reader.GetInt64(0),
                Seq:         reader.GetInt64(1),
                PlayerId:    reader.GetInt32(2),
                TypeName:    reader.GetString(3),
                PayloadJson: reader.GetString(4)));
        }
        // Materialize into a list so the reader isn't held by the caller's
        // enumeration — keeps the call site simple and the connection
        // free for the next operation.
        return rows;
    }

    // Test-only helper for the insulation proof
    // (RecoveryTests.PreSnapshotIntentsCanBeDeleted_StillRecovers).
    // Production intent stores are append-only; this exists to simulate the
    // "operator pruned old intents" case that the insulation property
    // promises is safe.
    internal void DeleteIntentsAtOrBefore(long tick)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM intents WHERE tick <= $tick;";
        cmd.Parameters.AddWithValue("$tick", tick);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
