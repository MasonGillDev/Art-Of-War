namespace Sim.Core.Battlefields;

// M41 — one unit as the turn resolver sees it: a snapshot read from the real
// Unit when the turn starts (docs/battlefield-grid.md §5).
//
//   Damage  — what it deals per turn BEFORE morale: CombatRules.EffectivePower
//             in the world (role base + sword/bow + fed house + king's aura).
//   Ranged  — UnitCombatCatalog's Ranged flag (the archer).
//   At      — its subtile. Off the board (just outside an edge) for a unit
//             waiting to come on in that lane.
//   CameFrom— the subtile it last moved from (off-board when it entered),
//             for "no passing through" (§3). Null if it has never moved on
//             this board (it was placed when the board opened).
public sealed record BoardUnit(int Id, int OwnerId, Subtile At, int Hp, int Damage, bool Ranged)
{
    public Subtile? CameFrom { get; init; }

    public bool OnBoard => At.IsOnBoard;
}

// M41 — a board at one instant: the subtile layer, the units on it (and the
// ones waiting just outside an edge), and who is hostile to whom. Immutable;
// the resolver returns a new one.
//
// OCCUPANCY (§3): at most one unit per OWNER per subtile. A subtile holding
// units of two hostile owners is a duel. The board is general in owners —
// bandits and two warring players can share one — so "side" is always an
// owner id and hostility is asked, never assumed.
public sealed class BoardState
{
    private readonly Dictionary<int, BoardUnit> _byId;
    private readonly Func<int, int, bool> _hostile;
    private readonly Func<int, int, bool> _allied;

    public SubtileLayer Layer { get; }

    // Every unit, on the board or waiting outside it, in id order.
    public IReadOnlyList<BoardUnit> Units { get; }

    // `areAllied` answers whether two owners are allies, for the layer's
    // owner-and-allies subtiles (walls, gates, towers); without it only the
    // layer's own owner counts as friendly.
    public BoardState(SubtileLayer layer, IEnumerable<BoardUnit> units, Func<int, int, bool> areHostile,
        Func<int, int, bool>? areAllied = null)
    {
        Layer = layer;
        _hostile = areHostile;
        _allied = areAllied ?? ((_, _) => false);
        Units = units.OrderBy(u => u.Id).ToList();
        _byId = new Dictionary<int, BoardUnit>();
        var taken = new HashSet<(int Owner, Subtile At)>();
        foreach (var u in Units)
        {
            if (!_byId.TryAdd(u.Id, u))
                throw new ArgumentException($"Unit {u.Id} appears twice on the board.");
            if (u.OnBoard && !taken.Add((u.OwnerId, u.At)))
                throw new ArgumentException($"Owner {u.OwnerId} has two units on {u.At}.");
        }
    }

    public bool AreHostile(int a, int b) => a != b && _hostile(a, b);

    // How the layer sees u: friendly to what stands here, and whether it is an archer.
    public BoardMover MoverOf(BoardUnit u) => new(
        Layer.Owner is { } o && (u.OwnerId == o || _allied(u.OwnerId, o)),
        u.Ranged);

    public bool CanStep(BoardUnit u, Subtile from, Subtile to) => Layer.CanStep(from, to, MoverOf(u));

    public bool CanStand(BoardUnit u, Subtile at) => Layer.CanStand(at, MoverOf(u));

    public BoardUnit? Get(int id) => _byId.GetValueOrDefault(id);

    public IEnumerable<BoardUnit> OnBoard => Units.Where(u => u.OnBoard);

    public BoardUnit? UnitOf(int ownerId, Subtile at)
    {
        foreach (var u in Units)
            if (u.OnBoard && u.OwnerId == ownerId && u.At == at) return u;
        return null;
    }

    public bool IsEmpty(Subtile at)
    {
        foreach (var u in Units)
            if (u.OnBoard && u.At == at) return false;
        return true;
    }

    // The unit u is duelling: the lowest-id unit hostile to u on u's own
    // subtile (with two owners there is only one; the id order settles a
    // three-owner subtile deterministically). Null when u is not in a duel.
    public BoardUnit? OpponentOf(BoardUnit u)
    {
        if (!u.OnBoard) return null;
        foreach (var o in Units)
            if (o.OnBoard && o.At == u.At && AreHostile(u.OwnerId, o.OwnerId)) return o;
        return null;
    }

    public bool InDuel(BoardUnit u) => OpponentOf(u) is not null;

    // Friends of u's own owner on the 4 orthogonal neighbours, on this board
    // only (a friend on the next world tile has no subtile). Feeds morale.
    public int FriendsAlongside(BoardUnit u)
    {
        if (!u.OnBoard) return 0;
        var n = 0;
        foreach (var h in Headings.All)
        {
            var q = u.At.Step(h);
            if (q.IsOnBoard && UnitOf(u.OwnerId, q) is not null) n++;
        }
        return n;
    }

    // Is any unit hostile to `ownerId` on the board?
    public bool HasHostileTo(int ownerId) => OnBoard.Any(o => AreHostile(ownerId, o.OwnerId));

    internal BoardState With(IEnumerable<BoardUnit> units) => new(Layer, units, _hostile, _allied);
}
