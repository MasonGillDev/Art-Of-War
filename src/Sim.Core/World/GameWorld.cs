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

    // Monotonic order-id counter, same shape as NextUnitId. Ids start at 1
    // so 0 means "no order".
    public int NextOrderId { get; internal set; } = 1;

    // THE ORDER TABLE (docs/automation-substrate.md, Layer 1) — player
    // automation on the substrate model. Sparse by order id; sorted so both
    // snapshot iteration AND driver evaluation are canonical (evaluation
    // order is arbitration: whoever evaluates first claims the scarce unit).
    // Mutated ONLY by SetOrderIntent / ClearOrderIntent (definition) and
    // OrderStatusIntent (the status block).
    //
    // Shares NextOrderId with the outgoing M18 StandingOrders so ids never
    // collide while both models coexist (substrate Phase D deletes the old).
    public SortedDictionary<int, Sim.Core.Automation.Order> Orders { get; } = new();

    // THE CLAIMS LEDGER (docs/automation-substrate.md) — which order is
    // responsible for which unit. Keyed BY UNIT so "at most one claim per
    // unit" is structural (a dictionary can't hold two values for a key)
    // rather than an invariant someone has to enforce; sorted so snapshot
    // iteration is canonical.
    //
    // Mutated ONLY by ClaimUnitIntent (server-internal, durable) — see
    // Automation/Claim.cs. Sim.Core never decides who to claim; the
    // server-side driver does, and the ledger just records the commitment
    // so every later evaluation in the pass sees a smaller pool.
    //
    // PURE-READ WALL: Claims.IsDormant / IsClaimed / HeldBy read this and
    // never write. A selector or view writing here would corrupt
    // snapshotted state — same contract as Explored / Fertility above.
    public SortedDictionary<int, Sim.Core.Automation.Claim> Claims { get; } = new();

    // M20 — scouting missions, keyed by the scout's own unit id (one slot
    // per scout). Sorted so snapshot iteration is canonical. The observation
    // log inside each mission is appended ONLY by ScoutObservation.Capture
    // (called from MoveArrivalEvent.Apply — the one write site, like Explored
    // / Sight.Reveal above). Sim.Core never reads the log to drive the sim;
    // the server-side claims compiler reads it on the presentation side. See
    // Scouting/ScoutMission.cs and docs/m20-scouting-reports-spec.md.
    public SortedDictionary<int, Sim.Core.Scouting.ScoutMission> ScoutMissions { get; } = new();

    // M22 — tiles whose worldgen biome is common-knowledge HIGH terrain
    // (Biomes.IsCommonKnowledgeTerrain — Mountain today). View.BuildPlayerView
    // reveals these in every player's RememberedTerrain regardless of fog, so
    // the scarce peaks are visible from the start — a race, not a discovery
    // (docs/high-terrain-visibility.md).
    //
    // Computed ONCE in the constructor and never mutated: Mountain tiles are
    // immutable (canals only flood non-Mountain land; nothing else changes the
    // grid). It is NOT serialized and NOT part of the sim hash — a read-side
    // memo of immutable worldgen data, recomputed identically from the grid on
    // restore. Eager (not lazy) so BuildPlayerView stays a strict pure read
    // with no cache write on the fog path.
    public IReadOnlySet<TileCoord> CommonKnowledgeTerrain { get; }

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
        CommonKnowledgeTerrain = ComputeCommonKnowledgeTerrain(grid);
    }

    // M22 — scan the frozen grid once for the common-knowledge high-terrain
    // band. Bounded O(W*H), one-time per world construction (genesis + each
    // restore); the result is immutable for the world's lifetime.
    private static HashSet<TileCoord> ComputeCommonKnowledgeTerrain(TileGrid grid)
    {
        var set = new HashSet<TileCoord>();
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var t = new TileCoord(x, y);
                if (Biomes.IsCommonKnowledgeTerrain(grid.BiomeAt(t))) set.Add(t);
            }
        return set;
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
        InitStructureHealthIfFresh(s);
        return s;
    }

    // M24 — auto-init Health from the catalog when the structure was
    // constructed without an explicit value (Health == 0 sentinel). Mirrors
    // InitCombatStatsIfFresh for units. Snapshot.ReadStructures writes the
    // serialised Health BEFORE calling AddStructure, so a damaged structure
    // restores at its persisted HP (this no-ops when Health > 0). Kinds with
    // BaseHealth == 0 (Cache, Canal, future Rubble) stay at Health == 0 and
    // the combat round treats them as indestructible.
    private static void InitStructureHealthIfFresh(Structure s)
    {
        if (s.Health != 0) return;
        if (!StructureCatalog.TryGetSpec(s.Kind, out var spec)) return;
        s.Health = spec.BaseHealth;
    }
}
