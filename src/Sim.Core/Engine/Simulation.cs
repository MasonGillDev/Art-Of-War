namespace Sim.Core.Engine;
public sealed class Simulation
{
    public GameWorld World { get; }
    public Rng Rng { get; }
    public long Now { get; private set; }

    private readonly EventQueue _queue = new();
    private long _nextSeq;

    // Exposed for canonical serialization (Persistence/Snapshot.cs). Not part
    // of the public sim contract.
    internal long NextSeq => _nextSeq;

    private readonly List<ScheduledEvent> _resolvedLog = new();
    public IReadOnlyList<ScheduledEvent> ResolvedLog => _resolvedLog;

    private readonly List<(long At, Intent Intent)> _intentLog = new();
    public IReadOnlyList<(long At, Intent Intent)> IntentLog => _intentLog;

    public Simulation(GameWorld world, ulong seed)
    {
        World = world;
        Rng = new Rng(seed);
    }

    // Schedule an event with the next monotonic Seq. Returns the Seq actually
    // assigned so callers can stash it on their in-flight anchor for M4
    // recovery (see Unit.NextArrivalSeq, Extractor.NextProductionTickSeq,
    // ConstructionSite.BuildCompleteSeq). Old callers that ignore the value
    // continue to work as statements.
    public long Schedule(long at, ScheduledEvent e)
    {
        if (at < Now)
            throw new InvalidOperationException($"Cannot schedule event in the past (at={at}, now={Now})");
        e.At = at;
        e.Seq = _nextSeq++;
        _queue.Enqueue(e);
        return e.Seq;
    }

    // M4 regen-only. Used ONLY by Persistence/RegenerateQueue when rebuilding
    // the in-flight queue from snapshot state. Assigns the caller-supplied
    // Seq instead of consuming a fresh one — preserves same-tick ordering
    // across recovery. Does NOT bump _nextSeq (which was restored from
    // snapshot to the value the LIVE sim had at snapshot time).
    //
    // Not part of the public sim contract; not called by intent / event
    // code in normal operation.
    internal void ScheduleWithSeq(long at, long seq, ScheduledEvent e)
    {
        if (at < Now)
            throw new InvalidOperationException($"Cannot schedule event in the past (at={at}, now={Now})");
        e.At = at;
        e.Seq = seq;
        _queue.Enqueue(e);
    }

    public void SubmitIntent(long at, Intent intent)
    {
        _intentLog.Add((at, intent));
        Schedule(at, new IntentEvent(intent));
    }

    // Test-only inspector for the queued events. Used by RegenerateQueueTests
    // to compare a live sim's queue against a regenerated one. Not part of
    // the public sim contract.
    internal IReadOnlyList<ScheduledEvent> QueuedEventsSnapshot() => _queue.SnapshotInOrder();

    public void Run(long? until = null)
    {
        while (_queue.Count > 0)
        {
            if (_queue.TryPeek(out var next) && until.HasValue && next!.At > until.Value)
                break;
            var e = _queue.Dequeue();
            Now = e.At;
            e.Apply(this);
            _resolvedLog.Add(e);
        }
    }

    // Restore-only. Used by Snapshot.Restore to rebuild a Simulation without
    // re-running its event history. Not part of the public sim contract.
    internal void RestoreClocks(long now, long nextSeq) { Now = now; _nextSeq = nextSeq; }
}
