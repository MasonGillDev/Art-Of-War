namespace Sim.Core.Engine;

// Single global priority queue, keyed on (At, Seq) so ties resolve in
// submission order. This is THE source of ordering — never bypass it.
public sealed class EventQueue
{
    private readonly PriorityQueue<ScheduledEvent, (long At, long Seq)> _q = new();

    public int Count => _q.Count;

    public void Enqueue(ScheduledEvent e) => _q.Enqueue(e, (e.At, e.Seq));

    public ScheduledEvent Dequeue() => _q.Dequeue();

    public bool TryPeek(out ScheduledEvent? e)
    {
        if (_q.TryPeek(out var ev, out _)) { e = ev; return true; }
        e = null;
        return false;
    }

    // Test-only inspector — returns the queued events in their dispatch order
    // (At ascending, Seq tiebreak). Non-destructive: drains a copy of the
    // underlying queue, doesn't touch the live one. Used by
    // RegenerateQueueTests to compare a live queue against a regenerated one.
    internal IReadOnlyList<ScheduledEvent> SnapshotInOrder()
    {
        var copy = new PriorityQueue<ScheduledEvent, (long, long)>();
        copy.EnqueueRange(_q.UnorderedItems.Select(p => (p.Element, p.Priority)));
        var list = new List<ScheduledEvent>(copy.Count);
        while (copy.Count > 0) list.Add(copy.Dequeue());
        return list;
    }
}
