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

    // M8 — spec-aware ctor. Constructs the world via Genesis.Build, then
    // immediately rolls each genesis unit's lifespan from this sim's Rng,
    // in canonical (faction-id, unit-id) order. ALL genesis lifespan RNG
    // consumption happens here, inside the sim's owned construction —
    // never as a post-hoc wiring pass. See docs/population-model.md.
    //
    // The plain (GameWorld, seed) ctor is kept for Snapshot.Restore (which
    // doesn't roll; lifespans are restored from persisted state) and for
    // tests that hand-build worlds without caring about aging.
    public Simulation(Sim.Core.World.GenesisSpec spec, ulong seed)
        : this(Sim.Core.World.Genesis.Build(spec), seed)
    {
        // Iterate in canonical order — matches the snapshot's Units
        // iteration so the RNG-consumption sequence is grep-checkable.
        foreach (var unit in World.Units.Values)
        {
            Sim.Core.Population.Population.ScheduleLifespan(this, unit);
        }
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

    // How many events are currently queued. Public read — benign for hosts
    // and clients to display ("resumed with N in-flight events"). Reads
    // nothing privileged.
    public int QueuedEventCount => _queue.Count;

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
