using Sim.Core.Engine;
using Sim.Core.Intents;

namespace Sim.Persistence;

// Host-side helper for the log-then-apply discipline. Durably commits the
// intent BEFORE handing it to the sim — if we crash after log+commit but
// before apply, recovery replays the intent from the log; if we crash
// before commit, the player hasn't been acknowledged so it's "as if never
// sent."
//
// Lives in Sim.Persistence (not Sim.Core) so the engine stays
// durability-free. Sim.Host calls this; tests construct stores directly.
public static class DurableSubmit
{
    // Serializes the intent, durably commits to the store, then submits to
    // the simulation. The intent goes onto the live queue with a fresh Seq
    // (sim.SubmitIntent's standard behavior); the durable row records
    // whatever seq we stored — historically that's been the live Seq, so
    // we pass it through here too for traceability.
    //
    // Throws if the durable write fails; the sim is NOT mutated in that
    // case (log-then-apply). If the apply itself throws (it shouldn't —
    // sim mutations don't throw at submission time), the row is still
    // durable; recovery will replay it. That's the correct behavior.
    public static void SubmitIntentDurable(
        Simulation sim, IIntentStore store, long at, Intent intent)
    {
        var (typeName, payload) = IntentJson.Serialize(intent);

        // The seq stored in the durable row is the seq the IntentEvent
        // gets when scheduled. We don't know that until SubmitIntent runs,
        // but SubmitIntent's contract is "consume the next monotonic seq."
        // Peek by reading sim.NextSeq (the value that the next Schedule
        // call will assign). The order is: (1) read seq, (2) write durable,
        // (3) apply. If we crash between (2) and (3), recovery sees the
        // row with this seq and replays — re-creating an IntentEvent that
        // gets a NEW seq from the restored sim. That's fine: in-flight
        // events at the snapshot tick come from RegenerateQueue with their
        // original seqs; post-snapshot intents from the tail get fresh
        // seqs continuing the monotonic sequence. Same-tick fairness
        // across the snapshot boundary survives because pre-snapshot
        // events have older seqs than any post-restore intent.
        var seq = sim.NextSeq;
        store.AppendIntent(at, seq, intent.PlayerId, typeName, payload);
        sim.SubmitIntent(at, intent);
    }
}
