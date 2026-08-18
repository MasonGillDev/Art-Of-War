using Sim.Core.Automation;

namespace Sim.Server.Automation;

// Append-only enum. What happened to an order on one think.
//
// The vocabulary IS the dashboard: each value maps 1:1 onto a player-facing
// status, so the driver must pick the value that tells the truth rather than
// leaning on the Detail string. (The first cut had no InFlight — "crew is on
// the trip" and "source has no Food" were both Waiting-with-a-detail, the
// client guessed which was which from the detail's presence, and a supply
// line pointed at an empty farm displayed as "Working" while its hauler sat
// idle. A missing state cannot be patched around in presentation.)
public enum JournalOutcome : byte
{
    Fired      = 1, // work dispatched THIS think (a haul sent, a crew pulled)
    Waiting    = 2, // trigger not met — the normal resting state, not a problem
    NoCrew     = 3, // trigger met but nobody to do it (pool dry / crew dead)
    Blocked    = 4, // trigger met, but the world can't serve it (empty source, laden hand)
    Suspended  = 5, // order disabled (retry budget exhausted)
    InFlight   = 6, // work dispatched EARLIER is still underway (crew mid-trip)
}

// One line of the morning report.
public readonly record struct JournalEntry(
    long Tick,
    int OrderId,
    int OwnerId,
    JournalOutcome Outcome,
    string Detail);

// THE MORNING REPORT'S BACKBONE (docs/automation-as-core-game.md).
//
// In an async game the player cannot watch their machine run, so reading
// what it DID is a primary game verb. This is the raw stream those reports
// are built from: every think, every order, what happened and why.
//
// Presentation-side ONLY — never sim state, never hashed, never replayed.
// It is an observation of the driver, not an input to it, so a server that
// drops the journal produces a byte-identical world. A ring buffer bounds
// it; the wire/report layer summarizes rather than shipping it raw.
public sealed class OrderJournal
{
    private readonly JournalEntry[] _ring;
    private int _next;
    private int _count;

    // Latest entry per order, kept alongside the ring so the view projector
    // can answer "what did this order do last think?" in O(1) without
    // walking (and allocating) the whole history every poll. The ring can
    // evict an order's only entry; this does not — a long-resting order
    // still reports "Waiting" rather than going blank, which is exactly the
    // state the dashboard must not lose.
    private readonly Dictionary<int, JournalEntry> _last = new();

    public OrderJournal(int capacity = 4096)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new JournalEntry[capacity];
    }

    public int Count => _count;
    public int Capacity => _ring.Length;

    public void Add(long tick, Order order, JournalOutcome outcome, string detail = "")
    {
        var entry = new JournalEntry(tick, order.OrderId, order.OwnerId, outcome, detail);
        _ring[_next] = entry;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        _last[order.OrderId] = entry;
    }

    // The order's most recent outcome, or null if it has never been thought
    // about (installed this tick, or the driver is off). Presentation-only,
    // like everything else here.
    public JournalEntry? Last(int orderId) =>
        _last.TryGetValue(orderId, out var e) ? e : null;

    // Drop last-entry rows for orders that no longer exist. An order id is
    // never reused (NextOrderId only climbs), so this is pure housekeeping:
    // it keeps the map from growing without bound across a long session of
    // installing and clearing orders.
    public void PruneTo(ICollection<int> liveOrderIds)
    {
        if (_last.Count == 0) return;
        List<int>? dead = null;
        foreach (var id in _last.Keys)
            if (!liveOrderIds.Contains(id))
                (dead ??= new List<int>()).Add(id);
        if (dead is null) return;
        foreach (var id in dead) _last.Remove(id);
    }

    // Oldest → newest. Allocates; call from reporting paths, not thinks.
    public List<JournalEntry> Entries()
    {
        var list = new List<JournalEntry>(_count);
        var start = _count < _ring.Length ? 0 : _next;
        for (var i = 0; i < _count; i++) list.Add(_ring[(start + i) % _ring.Length]);
        return list;
    }

    // Entries for one order, oldest → newest ("what has this line been
    // doing?" — the per-order drill-down in the report).
    public List<JournalEntry> For(int orderId)
    {
        var list = new List<JournalEntry>();
        foreach (var e in Entries())
            if (e.OrderId == orderId) list.Add(e);
        return list;
    }
}
