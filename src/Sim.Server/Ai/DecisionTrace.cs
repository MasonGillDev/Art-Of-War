namespace Sim.Server.Ai;

// M17 — the arbitration debugger, built BEFORE it's needed (spec
// requirement). Every think records which rung fired, why, and what it
// emitted. Because the sim is deterministic and the intent log replays,
// "the AI did something dumb at tick T" reduces to re-running the seed
// and reading the entry at T — tuning arbitration is debugging, not
// divination. Ring buffer so a week-long server doesn't hoard history.
public sealed class DecisionTrace
{
    public readonly record struct Entry(long Tick, string Rung, string Why, string Intents);

    private readonly Entry[] _ring;
    private int _next;
    private int _count;

    public DecisionTrace(int capacity = 256) { _ring = new Entry[capacity]; }

    public void Record(long tick, string rung, string why, string intents)
    {
        _ring[_next] = new Entry(tick, rung, why, intents);
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    // Oldest → newest.
    public IReadOnlyList<Entry> Entries()
    {
        var list = new List<Entry>(_count);
        var start = (_next - _count + _ring.Length) % _ring.Length;
        for (var i = 0; i < _count; i++)
            list.Add(_ring[(start + i) % _ring.Length]);
        return list;
    }

    public string Dump() =>
        string.Join(Environment.NewLine,
            Entries().Select(e => $"t{e.Tick,8} [{e.Rung}] {e.Why} => {e.Intents}"));
}
