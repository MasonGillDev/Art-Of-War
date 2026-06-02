namespace Sim.Core.World;

public sealed class GameWorld
{
    public TileGrid Grid { get; }

    // Sorted by id so snapshot canonicalization is order-stable.
    public SortedDictionary<int, Unit> Units { get; } = new();

    // Sparse: most tiles have no structure. Iterated in canonical (y, x) order
    // by Snapshot — see Persistence/Snapshot.cs.
    public Dictionary<TileCoord, Structure> Structures { get; } = new();

    // Sparse: only tiles with non-zero road condition live here. Mutated
    // exclusively by Roads.CreditTraffic (called from MoveArrivalEvent —
    // the one mutation point). Read by Roads.EffectiveCost / ConditionAt
    // from pathfinding and views — those reads must NEVER write. See
    // Roads/Roads.cs for the contract.
    public Dictionary<TileCoord, RoadState> Roads { get; } = new();

    // Player registry. Genesis seeds player 0; multi-player scenarios add
    // more. Minimal for M3 — no factions / economies / win conditions yet.
    public SortedDictionary<int, Player> Players { get; } = new();

    // M5 — Groups owned by a player; members are still individual Units
    // (referenced by id). Sparse: id 0 means "no group has this id," so
    // ids start from 1 and increment monotonically across the world's
    // lifetime. See Sim.Core.Groups for the orchestration.
    public SortedDictionary<int, Group> Groups { get; } = new();

    // Per-player explored-terrain memory (M3 Phase B). Sparse: most players
    // have explored some tiles, not most tiles. HashSet for O(1) inserts;
    // sorted at serialize time.
    //
    // INVERTED PURE-READ WALL: written ONLY by Vision.Reveal (which is
    // called from MoveArrivalEvent.Apply, BuildCompleteEvent.Apply,
    // Genesis.Build — the three event-driven sites). Read ONLY by views.
    // A view path writing here would corrupt snapshotted state. See
    // docs/persistence-model.md and Vision/Vision.cs.
    public Dictionary<int, HashSet<TileCoord>> Explored { get; } = new();

    public GameWorld(TileGrid grid) { Grid = grid; }

    public Unit AddUnit(int id, TileCoord position)
    {
        var u = new Unit(id, position);
        Units.Add(id, u);
        return u;
    }

    public Unit AddUnit(Unit unit)
    {
        Units.Add(unit.Id, unit);
        return unit;
    }

    public T AddStructure<T>(T s) where T : Structure
    {
        Structures.Add(s.At, s);
        return s;
    }
}
