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

    public void Schedule(long at, ScheduledEvent e)
    {
        if (at < Now)
            throw new InvalidOperationException($"Cannot schedule event in the past (at={at}, now={Now})");
        e.At = at;
        e.Seq = _nextSeq++;
        _queue.Enqueue(e);
    }

    public void SubmitIntent(long at, Intent intent)
    {
        _intentLog.Add((at, intent));
        Schedule(at, new IntentEvent(intent));
    }

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
