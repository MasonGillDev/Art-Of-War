namespace Sim.Core.World;

// What a unit is carrying: any mix of resources under ONE total capacity
// (docs/hauling-queue-and-routes.md). A route crew picks up ore and wood at
// one stop and drops them at two others, so a single resource/amount pair
// could not hold its load.
//
// Capacity is NOT stored here — it is Unit.CargoCapacity, derived live from
// role and buffs. Callers check space before Add; the hold only keeps the
// arithmetic honest (no negative amounts, no zero rows).
//
// Canonical order: a SortedDictionary keyed by the append-only Resource enum,
// so snapshot writes and death drops iterate by enum ordinal
// (architecture §3.6) with no sort at the call site.
//
// Enumerable only so object initializers can fill it
// (`Cargo = { { Resource.Wood, 10 } }`, via Add).
public sealed class CargoHold : IEnumerable<KeyValuePair<Resource, int>>
{
    private readonly SortedDictionary<Resource, int> _items = new();

    public int Total { get; private set; }
    public bool IsEmpty => Total == 0;

    // Ascending by Resource. Read-only view: every write goes through
    // Add / Take / Clear so Total cannot drift from the rows.
    public IReadOnlyDictionary<Resource, int> Items => _items;

    public int AmountOf(Resource resource) =>
        _items.TryGetValue(resource, out var amount) ? amount : 0;

    // The resource with the most units aboard (ties: lowest enum value).
    // For single-resource readers — the legacy wire field and log lines —
    // which is exact for every carrier except a mixed route crew.
    public Resource Dominant
    {
        get
        {
            var best = Resource.None;
            var bestAmount = 0;
            foreach (var (r, a) in _items)
                if (a > bestAmount) { best = r; bestAmount = a; }
            return best;
        }
    }

    public void Add(Resource resource, int amount)
    {
        if (amount <= 0) return;
        if (resource == Resource.None)
            throw new InvalidOperationException("CargoHold.Add with Resource.None");
        _items[resource] = AmountOf(resource) + amount;
        Total += amount;
    }

    // Removes up to `amount` of `resource`; returns how much was removed.
    public int Take(Resource resource, int amount)
    {
        if (amount <= 0 || !_items.TryGetValue(resource, out var held)) return 0;
        var taken = Math.Min(amount, held);
        if (taken == held) _items.Remove(resource); else _items[resource] = held - taken;
        Total -= taken;
        return taken;
    }

    public void Clear()
    {
        _items.Clear();
        Total = 0;
    }

    public IEnumerator<KeyValuePair<Resource, int>> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
