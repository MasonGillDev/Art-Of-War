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

    // M9 — per-player per-tile remembered biome, snapshotted at each
    // Sight.Reveal call. View.BuildPlayerView reports the stored biome (the
    // last-seen value) for remembered-but-not-visible tiles. Currently-
    // visible tiles always show derived BiomeAt(now) regardless. This is
    // what makes "the world changes behind the fog" work: a tile that
    // degraded while the player wasn't looking still shows its last-seen
    // biome until re-scouted.
    //
    // INVERTED PURE-READ WALL: same call sites as Explored. Written in
    // lock-step (every Reveal that adds/refreshes an Explored tile also
    // writes the biome here).
    public Dictionary<int, Dictionary<TileCoord, Biome>> RememberedBiome { get; } = new();

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

    // M8 — population config (lifespan, gestation, age gates) + monotonic
    // unit-id counter for births. Genesis seeds NextUnitId = max(spawned
    // ids) + 1; BirthEvent allocates fresh ids from here.
    public Population.PopulationConfig PopulationConfig { get; private set; }
    public int NextUnitId { get; internal set; } = 1;

    // M9 — biome-degradation config (thresholds, baselines, recovery, radius)
    // + sparse per-tile fertility deviation. Only tiles whose Deviation != 0
    // live in the dict (matches the Roads pattern). The latch is IMPLICIT
    // (no per-tile flag): a tile is "desert-latched" iff
    // (baseline + Deviation) < DesertThreshold. See Biomes/BiomeDegradation.cs.
    //
    // INVERTED PURE-READ WALL: written ONLY by BiomeDegradation.CatchUp
    // (called from extractor production-state transitions). Read by
    // BiomeDegradation.FertilityAt / BiomeAt — pure, no-mutation. A view or
    // intent writing here would corrupt snapshotted state.
    public Sim.Core.Biomes.BiomeDegradationConfig BiomeDegradationConfig { get; private set; }
    public Dictionary<TileCoord, Sim.Core.Biomes.Fertility> Fertility { get; } = new();

    // M18 — standing orders (player automation programs). Sparse by order
    // id; sorted so snapshot iteration is canonical. Mutated ONLY by
    // SetStandingOrderIntent / ClearStandingOrderIntent (definition) and
    // AdvanceOrderCursorIntent (cursor block) — see Automation/StandingOrder.cs
    // and docs/automation-layers.md. Sim.Core never evaluates these; the
    // server-side AutomationDriver does.
    public SortedDictionary<int, Sim.Core.Automation.StandingOrder> StandingOrders { get; } = new();

    // Monotonic order-id counter, same shape as NextUnitId. Ids start at 1
    // so 0 means "no order".
    public int NextOrderId { get; internal set; } = 1;

    public GameWorld(TileGrid grid)
        : this(grid, new Diplomacy.DiplomacyConfig(), new Combat.CombatConfig(), new Population.PopulationConfig(), new Sim.Core.Biomes.BiomeDegradationConfig()) { }

    public GameWorld(TileGrid grid, Diplomacy.DiplomacyConfig diplomacyConfig)
        : this(grid, diplomacyConfig, new Combat.CombatConfig(), new Population.PopulationConfig(), new Sim.Core.Biomes.BiomeDegradationConfig()) { }

    public GameWorld(TileGrid grid, Diplomacy.DiplomacyConfig diplomacyConfig, Combat.CombatConfig combatConfig)
        : this(grid, diplomacyConfig, combatConfig, new Population.PopulationConfig(), new Sim.Core.Biomes.BiomeDegradationConfig()) { }

    public GameWorld(TileGrid grid, Diplomacy.DiplomacyConfig diplomacyConfig, Combat.CombatConfig combatConfig, Population.PopulationConfig populationConfig)
        : this(grid, diplomacyConfig, combatConfig, populationConfig, new Sim.Core.Biomes.BiomeDegradationConfig()) { }

    public GameWorld(TileGrid grid, Diplomacy.DiplomacyConfig diplomacyConfig, Combat.CombatConfig combatConfig, Population.PopulationConfig populationConfig, Sim.Core.Biomes.BiomeDegradationConfig biomeDegradationConfig)
    {
        Grid = grid;
        Diplomacy = new Diplomacy.Diplomacy(diplomacyConfig);
        CombatConfig = combatConfig;
        PopulationConfig = populationConfig;
        BiomeDegradationConfig = biomeDegradationConfig;
    }

    // Restore-only — used by Snapshot.Restore to swap in the serialized
    // configs after the world is constructed with placeholders.
    internal void RestoreCombatConfig(Combat.CombatConfig config) => CombatConfig = config;
    internal void RestorePopulationConfig(Population.PopulationConfig config) => PopulationConfig = config;
    internal void RestoreBiomeDegradationConfig(Sim.Core.Biomes.BiomeDegradationConfig config) => BiomeDegradationConfig = config;

    public Unit AddUnit(int id, TileCoord position)
    {
        var u = new Unit(id, position);
        Units.Add(id, u);
        InitCombatStatsIfFresh(u);
        BumpPopulationCount(u);
        return u;
    }

    public Unit AddUnit(Unit unit)
    {
        Units.Add(unit.Id, unit);
        InitCombatStatsIfFresh(unit);
        BumpPopulationCount(unit);
        return unit;
    }

    // M13 — Player.PopulationCount is maintained as
    // (count of world.Units where OwnerId == player.Id, excluding boats).
    // AddUnit is the single increment site; Population.OnUnitRemoved is
    // the single decrement site. PopulationCount is not serialised:
    // Snapshot.Restore calls AddUnit for every persisted unit, rebuilding
    // the count from scratch. Defensive: skip if the owner has no Player
    // record yet (genesis adds the castle's Player before the units, so
    // this is hit only by edge-case test scenarios).
    //
    // M12 — boats are vehicles, not mouths: they don't count toward the
    // food consumption rate and aren't eligible starvation-death victims.
    private void BumpPopulationCount(Unit u)
    {
        if (u.Role == UnitRole.Boat) return;
        if (Players.TryGetValue(u.OwnerId, out var player))
            player.IncrementPopulation();
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
