namespace Sim.Core.World;

// Append-only enum (serialized).
public enum UnitRole : byte
{
    None = 0,
    Builder = 1,
    Farmer = 2,
    Miner = 3,
    Lumberjack = 4,
    Quarryman = 5,
    Hauler = 6,
}

public sealed class Unit
{
    public int Id { get; }
    public TileCoord Position { get; set; }
    public UnitRole Role { get; init; } = UnitRole.None;
    public int CargoCapacity { get; init; } = 1;

    public Activity Activity { get; private set; } = Activity.Idle;
    // The structure tile this unit is currently bound to (Working at an
    // extractor, Building at a construction site). Null when Idle/Moving/Hauling.
    public TileCoord? Assignment { get; private set; }

    public Resource CargoResource { get; set; }
    public int CargoAmount { get; set; }

    public Unit(int id, TileCoord position) { Id = id; Position = position; }

    // The single mutation path for Activity. Intents call this rather than
    // poking the property; the transition table catches illegal hops before
    // they corrupt state.
    public bool TrySetActivity(Activity next, TileCoord? assignment = null)
    {
        if (!ActivityTransitions.CanTransition(Activity, next)) return false;
        Activity = next;
        Assignment = next == Activity.Idle ? null : assignment;
        return true;
    }
}
