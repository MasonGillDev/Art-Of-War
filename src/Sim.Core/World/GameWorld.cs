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
