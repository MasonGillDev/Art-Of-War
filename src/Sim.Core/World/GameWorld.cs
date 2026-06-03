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

    // M6 — per-pair diplomacy + world-level config. Genesis seeds Config
    // from GenesisSpec; relationships start empty (every pair defaults to
    // Neutral until a transition fires). Diplomatic state is public
    // knowledge to all players — the PlayerView surfaces every relationship
    // and every pending war.
    public Diplomacy.Diplomacy Diplomacy { get; }

    // M7 — combat config (world-level, immutable post-genesis) +
    // per-tile contested-combat anchors + loose-tile resource piles
    // (capture economy). All three round-trip through Snapshot at
    // FormatVersion 4; CombatStates regenerates its event via
    // RegenerateQueue.From on restore (same M4 pattern as the
    // war-effective event).
    public Combat.CombatConfig CombatConfig { get; private set; }
    public Dictionary<TileCoord, Combat.CombatState> CombatStates { get; } = new();
    public Dictionary<TileCoord, SortedDictionary<Resource, int>> GroundResources { get; } = new();

    public GameWorld(TileGrid grid)
        : this(grid, new Diplomacy.DiplomacyConfig(), new Combat.CombatConfig()) { }

    public GameWorld(TileGrid grid, Diplomacy.DiplomacyConfig diplomacyConfig)
        : this(grid, diplomacyConfig, new Combat.CombatConfig()) { }

    public GameWorld(TileGrid grid, Diplomacy.DiplomacyConfig diplomacyConfig, Combat.CombatConfig combatConfig)
    {
        Grid = grid;
        Diplomacy = new Diplomacy.Diplomacy(diplomacyConfig);
        CombatConfig = combatConfig;
    }

    // Restore-only — used by Snapshot.Restore to swap in the serialized
    // CombatConfig after the world is constructed with a placeholder.
    internal void RestoreCombatConfig(Combat.CombatConfig config) => CombatConfig = config;

    public Unit AddUnit(int id, TileCoord position)
    {
        var u = new Unit(id, position);
        Units.Add(id, u);
        InitCombatStatsIfFresh(u);
        return u;
    }

    public Unit AddUnit(Unit unit)
    {
        Units.Add(unit.Id, unit);
        InitCombatStatsIfFresh(unit);
        return unit;
    }

    // M7 — auto-init Health from UnitCombatCatalog if the unit was
    // constructed without an explicit value (Health == 0 sentinel).
    // Snapshot.ReadUnits sets Health to the serialized value BEFORE
    // calling AddUnit, so restored damaged units don't get reset.
    private static void InitCombatStatsIfFresh(Unit u)
    {
        if (u.Health == 0)
            u.Health = Sim.Core.Combat.UnitCombatCatalog.Spec(u.Role).BaseHealth;
    }

    public T AddStructure<T>(T s) where T : Structure
    {
        Structures.Add(s.At, s);
        return s;
    }
}
