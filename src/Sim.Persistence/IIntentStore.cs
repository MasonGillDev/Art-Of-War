namespace Sim.Persistence;

// Durable, append-only log of player intents. The intent log is one of M4's
// two durable artifacts (the other being snapshots) — see
// docs/persistence-model.md and docs/m4-status.md.
//
// Append semantics are STRONG: AppendIntent must not return until the row
// is durably committed (transaction committed, fsync'd via SQLite WAL).
// The host's SubmitIntentDurable wraps this — log-then-apply.
//
// LoadIntentsAfter returns rows ordered by (tick ASC, seq ASC).
public interface IIntentStore
{
    void AppendIntent(long tick, long seq, int playerId, string typeName, string payloadJson);
    IEnumerable<IntentRecord> LoadIntentsAfter(long tick);
}

public sealed record IntentRecord(long Tick, long Seq, int PlayerId, string TypeName, string PayloadJson);
