namespace Sim.Core.World;

public sealed class GameWorld
{
    public TileGrid Grid { get; }

    // Sorted by id so snapshot canonicalization is order-stable.
    public SortedDictionary<int, Unit> Units { get; } = new();

    // Sparse: most tiles have no structure. Iterated in canonical (y, x) order
    // by Snapshot — see Persistence/Snapshot.cs.
    public Dictionary<TileCoord, Structure> Structures { get; } = new();

    // Sparse: only ARCS (the lane between two adjacent tiles, docs/roads-on-
    // edges.md) with non-zero road condition live here. Mutated
    // exclusively by Roads.CreditTraffic (called from MoveArrivalEvent —
    // the one mutation point). Read by Roads.EffectiveCost / ConditionAt
    // from pathfinding and views — those reads must NEVER write. See
    // Roads/Roads.cs for the contract.
    public Dictionary<TileEdge, RoadState> Roads { get; } = new();

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
    // M41 — open battlefields (CombatModel.Grid), by tile. Written only by
    // Sim.Core.Battlefields.Battlefields; snapshotted v41; turn events are
    // rebuilt from each board's anchor by RegenerateQueue.
    public SortedDictionary<TileCoord, Sim.Core.Battlefields.Battlefield> Battlefields { get; } =
        new(Comparer<TileCoord>.Create((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X)));
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

    // M36 — THE HAUL QUEUE (docs/hauling-queue-and-routes.md). Sparse by job
    // id; one owner's queue is its jobs sorted by (QueueStamp, JobId).
    // Mutated ONLY by Set/Clear/RequeueHaulJobIntent and HaulDepositEvent
    // (a Once job's Delivered count and removal). NextHaulStamp is the
    // monotonic "back of the queue" counter, shared by every owner.
    public SortedDictionary<int, Sim.Core.Hauling.HaulJob> HaulJobs { get; } = new();
    public int NextHaulJobId { get; internal set; } = 1;
    public long NextHaulStamp { get; internal set; } = 1;

    // M36 — NAMED HAUL ROUTES, sparse by route id. Mutated ONLY by
    // Set/ClearHaulRouteIntent, Add/RemoveRouteCrewIntent and
    // ServeRouteStopIntent (a crew's cursor).
    public SortedDictionary<int, Sim.Core.Hauling.HaulRoute> HaulRoutes { get; } = new();
    public int NextHaulRouteId { get; internal set; } = 1;

    // M37 — progression (docs/progression.md). The config is genesis-set and
    // snapshotted (v37); Milestones is DERIVED from it (the catalog rows are
    // code), never serialized and never part of the hash. Omens are live
    // state: announced threats and the raids they became, until resolved.
    // Mutated only by Sim.Core.Progression.Omens.
    public Sim.Core.Progression.ProgressionConfig ProgressionConfig { get; private set; } = new();
    public IReadOnlyList<Sim.Core.Progression.Milestone> Milestones { get; private set; } =
        Sim.Core.Progression.MilestoneCatalog.For(new());
    public SortedDictionary<int, Sim.Core.Progression.Omen> Omens { get; } = new();
    public int NextOmenId { get; internal set; } = 1;

    internal void RestoreProgressionConfig(Sim.Core.Progression.ProgressionConfig config)
    {
        ProgressionConfig = config;
        Milestones = Sim.Core.Progression.MilestoneCatalog.For(config);
    }

    // M20 — scouting missions, keyed by the scout's own unit id (one slot
    // per scout). Sorted so snapshot iteration is canonical. The observation
    // log inside each mission is appended ONLY by ScoutObservation.Capture
    // (called from MoveArrivalEvent.Apply — the one write site, like Explored
    // / Sight.Reveal above). Sim.Core never reads the log to drive the sim;
    // the server-side claims compiler reads it on the presentation side. See
    // Scouting/ScoutMission.cs and docs/m20-scouting-reports-spec.md.
    public SortedDictionary<int, Sim.Core.Scouting.ScoutMission> ScoutMissions { get; } = new();

    // M38 — each player's chart: the secrets their returned scouts reported,
    // keyed by tile in (y, x) order (docs/scouting-secrets.md). Mutated only by
    // Sim.Core.Scouting.Charts; snapshotted (v38).
    public SortedDictionary<int, SortedDictionary<TileCoord, Sim.Core.Scouting.ChartEntry>> Charts { get; } = new();

    // M38 — idols: the grade table (genesis-set, snapshotted) and the live
    // circles of sight they have granted. Mutated only by Sim.Core.Scouting.Idols.
    public Sim.Core.Scouting.IdolConfig IdolConfig { get; private set; } = new();
    public SortedDictionary<int, Sim.Core.Scouting.VisionGrant> VisionGrants { get; } = new();
    public int NextVisionGrantId { get; internal set; } = 1;
    internal void RestoreIdolConfig(Sim.Core.Scouting.IdolConfig config) => IdolConfig = config;

    // M39 — bandit camp knobs (genesis-set, snapshotted v39).
    public Sim.Core.Bandits.CampConfig CampConfig { get; private set; } = new();
    internal void RestoreCampConfig(Sim.Core.Bandits.CampConfig config) => CampConfig = config;

    // Two-act pacing — when the landing (day X) comes (genesis-set, snapshotted
    // v40). Default Tick 0 = a one-act world. docs/two-act-pacing.md.
    public Sim.Core.Landing.LandingConfig LandingConfig { get; private set; }
    internal void RestoreLandingConfig(Sim.Core.Landing.LandingConfig config) => LandingConfig = config;
    // The hosts the landing raised, one per kingdom it came for, by target owner
    // id. Written only by LandingRules; snapshotted (v40).
    public SortedDictionary<int, Sim.Core.Landing.LandingHost> LandingHosts { get; } = new();
    // The pending LandingEvent's Seq (its anchor): set at genesis, cleared when
    // it fires. Null in a one-act world.
    public long? LandingSeq { get; internal set; }

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

    // M31 — dynasty config (aura radius/bonus, majority age). Defaulted rather
    // than threaded through the constructor chain: the telescoping ctors above
    // are already five deep, and Genesis is the only caller that ever sets a
    // non-default. Restored by Snapshot (v28), same shape as the configs above.
    public Sim.Core.Royalty.RoyaltyConfig RoyaltyConfig { get; private set; } = new();

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
    internal void RestoreRoyaltyConfig(Sim.Core.Royalty.RoyaltyConfig config) => RoyaltyConfig = config;

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
