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
}
