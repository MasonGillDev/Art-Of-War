using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Persistence;

// Phase-D smoke: M0 walk + Phase-C build + Phase-D production.
//
// Genesis seeds: Builder (unit 2) and Lumberjack (unit 4) both on the Forest
// tile we'll build the LumberCamp on. The scenario:
//   1. Place a LumberCamp construction site at (3,3).
//   2. Pre-deposit build materials (Phase E will haul this for real).
//   3. Assign builder, build completes.
//   4. Assign the Lumberjack as worker â€” production arms.
//   5. Run until the camp's buffer caps and production goes dormant.
//
// Twin runs must produce identical hashes; the final state must round-trip
// through Serialize/Restore.

static GenesisSpec MakeSpec()
{
    var biomes = new Dictionary<TileCoord, Biome>();
    for (var i = 1; i < 9; i++) biomes[new TileCoord(i, i)] = Biome.Forest;
    return new GenesisSpec
    {
        Width = 10,
        Height = 10,
        Biomes = biomes,
        FactionStarts = new[]
        {
            new FactionStartSpec
            {
                OwnerId = 0,
                CastlePosition = new TileCoord(0, 0),
                CastleHoldings = new SortedDictionary<Resource, int>
                {
                    [Resource.Wood] = 40,
                    [Resource.Stone] = 20,
                    [Resource.Food] = 10,
                },
                UnitSpawns = new[]
                {
                    new UnitSpawn(Id: 1, new TileCoord(0, 0), UnitRole.Builder),
                    new UnitSpawn(Id: 2, new TileCoord(3, 3), UnitRole.Builder),    // builds the camp
                    new UnitSpawn(Id: 3, new TileCoord(0, 0), UnitRole.Hauler),
                    new UnitSpawn(Id: 4, new TileCoord(3, 3), UnitRole.Lumberjack), // staffs the camp post-build
                },
            },
        },
    };
}

static Simulation RunScenario(Action<string>? log = null)
{
    var siteTile = new TileCoord(3, 3);
    var sim = new Simulation(MakeSpec(), seed: 0xC0FFEE);

    // M0 layer: unit 1 walks across the grid.
    sim.SubmitIntent(at: 0, new MoveIntent(unitId: 1, new TileCoord(9, 9)));

    // Phase C layer: place LumberCamp site.
    sim.SubmitIntent(at: 0, new PlaceSiteIntent(siteTile, StructureKind.LumberCamp));
    sim.Run(until: 0);

    // Pre-deposit build materials (Phase E will haul these). Bypasses
    // physicality on purpose for the smoke.
    var site = (ConstructionSite)sim.World.Structures[siteTile];
    var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
    foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);

    sim.SubmitIntent(at: sim.Now, new AssignBuildersIntent(siteTile, new[] { 2 }));
    // Run until the build is done â€” sim.Now will land at BuildDurationTicks.
    sim.Run(until: spec.BuildDurationTicks);

    // Phase D layer: assign the Lumberjack to the now-built camp.
    sim.SubmitIntent(at: sim.Now, new AssignWorkersIntent(siteTile, new[] { 4 }));
    // Let production run long enough to fill the buffer and go dormant.
    sim.Run(until: sim.Now + spec.ProductionPeriodTicks * 25);

    // Phase E layer: haul Wood from the LumberCamp back to the Castle.
    // Pickup should free buffer and re-arm production; deposit lands in the
    // Castle's holdings.
    sim.SubmitIntent(at: sim.Now,
        new HaulIntent(haulerId: 3, sourceTile: siteTile, destTile: new TileCoord(0, 0), Resource.Wood));
    sim.Run(); // run to completion of the haul

    // M2 layer: a few more haul round trips. Each one walks the same path
    // (Castle â†” LumberCamp), crediting the same tiles. Condition rises.
    for (var i = 0; i < 5; i++)
    {
        sim.SubmitIntent(at: sim.Now,
            new HaulIntent(haulerId: 3, sourceTile: siteTile, destTile: new TileCoord(0, 0), Resource.Wood));
        sim.Run();
    }

    if (log != null)
    {
        log("");
        log("--- Road conditions after sustained hauling ---");
        PrintRouteConditions(sim, log);
    }

    // M2 layer: long silence. Schedule a no-op far in the future, run to it.
    const long silenceDuration = 200_000;
    var startSilence = sim.Now;
    sim.Schedule(sim.Now + silenceDuration, new NoOpEvent());
    sim.Run();

    if (log != null)
    {
        log("");
        log($"--- Road conditions after {sim.Now - startSilence} ticks of silence (decay) ---");
        // Observed via pure-read ConditionAt â€” does NOT mutate stored state.
        // Stale RoadStates with stored Condition>0 are still in the dict
        // (lazy decay only removes on touch); the read just returns 0 for them.
        PrintRouteConditions(sim, log);
    }

    return sim;
}

static void PrintRouteConditions(Simulation sim, Action<string> log)
{
    // Pure-read observation of the route tiles. ConditionAt computes any
    // pending decay without writing.
    var routeTiles = sim.World.Roads.Keys
        .OrderBy(t => t.Y).ThenBy(t => t.X)
        .ToList();
    if (routeTiles.Count == 0) { log("  (no road tiles)"); return; }
    foreach (var t in routeTiles)
    {
        var condition = Road.ConditionAt(sim.World, t, sim.Now);
        log($"  ({t.X},{t.Y}): condition={condition}");
    }
}

static void Print(string label, Simulation sim)
{
    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"Intents submitted:     {sim.IntentLog.Count}");
    Console.WriteLine($"Events resolved:       {sim.ResolvedLog.Count}");
    Console.WriteLine($"Final sim tick:        {sim.Now}");

    var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
    Console.WriteLine($"Castle holdings:");
    foreach (var (r, n) in castle.Holdings) Console.WriteLine($"  {r}: {n}");

    var siteTile = new TileCoord(3, 3);
    if (sim.World.Structures.TryGetValue(siteTile, out var built) && built is Extractor ext)
    {
        Console.WriteLine($"Structure @ 3,3:       {ext.Kind}");
        Console.WriteLine($"  Workers:             [{string.Join(",", ext.Workers)}]");
        Console.WriteLine($"  Buffer / cap:        {ext.Buffer} / {ext.Spec.BufferCap}");
        Console.WriteLine($"  TickArmed:           {ext.TickArmed}");
        Console.WriteLine($"  LastProductionTick:  {ext.LastProductionTick}");
    }

    Console.WriteLine($"Units (id: role, position, activity):");
    foreach (var (id, u) in sim.World.Units)
        Console.WriteLine($"  {id}: {u.Role}, {u.Position.X},{u.Position.Y}, {u.Activity}");

    var rejects = sim.ResolvedLog.Count(e => e.Outcome.IsRejected);
    Console.WriteLine($"Rejected events:       {rejects}");
    Console.WriteLine($"Snapshot hash:         {Snapshot.Hash(sim)}");
    Console.WriteLine();
}

// Default mode: hand-authored 10x10 smoke (the cross-commit regression check).
// Pass `--generate` for the procedural-world demo (does not replace the
// regression smoke â€” generated maps are tuneable, so their hash isn't a
// stable regression target).
var generate = args.Length > 0 && args[0] == "--generate";
var groupsDemo = args.Length > 0 && args[0] == "--groups";
var degradationDemo = args.Length > 0 && args[0] == "--degradation";
var dataDirIdx = Array.IndexOf(args, "--data-dir");
var persistentDemo = dataDirIdx >= 0 && dataDirIdx + 1 < args.Length;

if (persistentDemo)
{
    PersistentDemo.Run(args[dataDirIdx + 1]);
}
else if (groupsDemo)
{
    GroupDemo.Run();
}
else if (degradationDemo)
{
    DegradationDemo.Run();
}
else if (!generate)
{
    var first = RunScenario(log: Console.WriteLine);
    Print("Run 1", first);

    var second = RunScenario();
    Print("Run 2 (identical intents)", second);

    if (Snapshot.Hash(first) != Snapshot.Hash(second))
    {
        Console.Error.WriteLine("DETERMINISM FAILURE: snapshot hashes diverged.");
        Environment.Exit(1);
    }

    var bytes = Snapshot.Serialize(first);
    var restored = Snapshot.Restore(bytes, seed: 0xC0FFEE);
    if (Snapshot.Hash(first) != Snapshot.Hash(restored))
    {
        Console.Error.WriteLine("ROUND-TRIP FAILURE: restored snapshot does not match original.");
        Environment.Exit(1);
    }

    Console.WriteLine("OK: identical scenario produced identical final state.");
    Console.WriteLine($"OK: serialized snapshot ({bytes.Length} bytes) restored to identical state.");
}
else
{
    GeneratedDemo.Run();
}

// Sentinel event for advancing the sim clock without state mutation.
// Used by the host smoke to observe road decay without further traffic.
sealed class NoOpEvent : ScheduledEvent
{
    public override void Apply(Simulation sim) { }
}

// M5 â€” Group lifecycle demo. Two scattered builders Form at a rendezvous,
// MoveGroup to a distant tile, Disband. Prints state transitions, asserts
// twin-run hash equality and snapshot round-trip on the final state.
static class GroupDemo
{
    public static void Run()
    {
        Simulation Build()
        {
            var grid = new TileGrid(12, 12, Biome.Grassland);
            var world = new GameWorld(grid);
            world.Players[0] = new Player(0);
            world.AddStructure(new Castle(new TileCoord(6, 6)) { OwnerId = 0 });
            world.AddUnit(new Unit(1, new TileCoord(1, 1)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(2, new TileCoord(10, 10)) { Role = UnitRole.Builder });
            return new Simulation(world, seed: 0xC0DE);
        }

        var sim = Build();
        var rendezvous = new TileCoord(6, 1);

        Console.WriteLine("--- Group Demo ---");
        Console.WriteLine($"Unit 1 starts at {sim.World.Units[1].Position.X},{sim.World.Units[1].Position.Y}");
        Console.WriteLine($"Unit 2 starts at {sim.World.Units[2].Position.X},{sim.World.Units[2].Position.Y}");
        Console.WriteLine($"FormGroup â†’ rendezvous {rendezvous.X},{rendezvous.Y}");

        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, rendezvous));
        sim.Run(until: 0);
        Console.WriteLine($"  After resolve: state={sim.World.Groups[1].State}, pending={sim.World.Groups[1].PendingArrivals}");

        sim.Run();
        Console.WriteLine($"  After rendezvous walks: state={sim.World.Groups[1].State}, position={sim.World.Groups[1].Position.X},{sim.World.Groups[1].Position.Y}");

        var destination = new TileCoord(6, 10);
        Console.WriteLine($"MoveGroup â†’ {destination.X},{destination.Y}");
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(1, destination));
        sim.Run();
        Console.WriteLine($"  After move: state={sim.World.Groups[1].State}, position={sim.World.Groups[1].Position.X},{sim.World.Groups[1].Position.Y}");
        Console.WriteLine($"  Member positions: U1={sim.World.Units[1].Position.X},{sim.World.Units[1].Position.Y}; U2={sim.World.Units[2].Position.X},{sim.World.Units[2].Position.Y}");

        Console.WriteLine("DisbandGroup");
        sim.SubmitIntent(sim.Now, new DisbandGroupIntent(1));
        sim.Run();
        Console.WriteLine($"  Group exists? {sim.World.Groups.ContainsKey(1)}");
        Console.WriteLine($"  U1.GroupId={sim.World.Units[1].GroupId?.ToString() ?? "null"}; U2.GroupId={sim.World.Units[2].GroupId?.ToString() ?? "null"}");
        Console.WriteLine($"Final hash: {Snapshot.Hash(sim)}");

        // Twin-run check.
        var sim2 = Build();
        sim2.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, rendezvous));
        sim2.Run();
        sim2.SubmitIntent(sim2.Now, new MoveGroupIntent(1, destination));
        sim2.Run();
        sim2.SubmitIntent(sim2.Now, new DisbandGroupIntent(1));
        sim2.Run();

        if (Snapshot.Hash(sim) != Snapshot.Hash(sim2))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE in group demo");
            Environment.Exit(1);
        }
        Console.WriteLine("OK: twin-run hashes match");

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xC0DE);
        if (Snapshot.Hash(sim) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE in group demo");
            Environment.Exit(1);
        }
        Console.WriteLine($"OK: snapshot round-trips ({bytes.Length} bytes)");
    }
}

// Generated-world demo. Runs the procedural pipeline, prints the biome map
// + chosen start, walks the builder somewhere reachable, asserts the
// twin-run + snapshot round-trip both still hold on the generated genesis.
static class GeneratedDemo
{
    public static void Run()
    {
        var cfg = new GenerationConfig { Seed = 7 };
        Console.WriteLine($"--- Generated continent (seed={cfg.Seed}, {cfg.Width}x{cfg.Height}) ---");
        var map = MapGenerator.Build(cfg);
        Console.WriteLine($"Castle start: {map.Start.X},{map.Start.Y}");
        PrintBiomeMap(map);

        var a = BuildSim(cfg, map);
        var b = BuildSim(cfg, map);
        var goal = FindFarReachableTile(map);
        a.SubmitIntent(0, new MoveIntent(1, goal));
        b.SubmitIntent(0, new MoveIntent(1, goal));
        a.Run();
        b.Run();

        Console.WriteLine();
        Console.WriteLine($"Both sims ran a walk from {map.Start.X},{map.Start.Y} â†’ {goal.X},{goal.Y}.");
        Console.WriteLine($"Run A hash: {Snapshot.Hash(a)}");
        Console.WriteLine($"Run B hash: {Snapshot.Hash(b)}");
        if (Snapshot.Hash(a) != Snapshot.Hash(b))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE on generated world â€” generator may be touching replay path.");
            Environment.Exit(1);
        }

        var bytes = Snapshot.Serialize(a);
        var restored = Snapshot.Restore(bytes, seed: 0xCAFE);
        if (Snapshot.Hash(a) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE on generated world.");
            Environment.Exit(1);
        }

        Console.WriteLine($"OK: twin-run match on generated world.");
        Console.WriteLine($"OK: serialized snapshot ({bytes.Length} bytes) restored to identical state.");
    }

    private static Simulation BuildSim(GenerationConfig cfg, GeneratedMap map)
    {
        var spec = new GenesisSpec
        {
            Width = map.Width,
            Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = map.Start,
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Wood] = 20,
                    },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, map.Start, UnitRole.Builder),
                    },
                },
            },
        };
        return new Simulation(spec, seed: 0xCAFE);
    }

    private static TileCoord FindFarReachableTile(GeneratedMap map)
    {
        // Just pick the opposite corner that's not Water (cheap enough that
        // the demo runs in reasonable time). Water IS passable here, so
        // pathfinding will find a route regardless.
        for (var y = map.Height - 1; y >= 0; y--)
            for (var x = map.Width - 1; x >= 0; x--)
                if (map.Grid[x, y] != Biome.Mountain) // grassland-ish destination
                    return new TileCoord(x, y);
        return new TileCoord(map.Width - 1, map.Height - 1);
    }

    private static void PrintBiomeMap(GeneratedMap map)
    {
        var glyphs = new Dictionary<Biome, char>
        {
            [Biome.Grassland] = '.',
            [Biome.Forest]    = 'T',
            [Biome.Hills]     = 'h',
            [Biome.Mountain]  = 'M',
            [Biome.Water]     = '~',
            [Biome.Desert]    = 'd',
        };
        var sb = new System.Text.StringBuilder();
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (x == map.Start.X && y == map.Start.Y) sb.Append('@');
                else sb.Append(glyphs.GetValueOrDefault(map.Grid[x, y], '?'));
            }
            sb.AppendLine();
        }
        Console.Write(sb.ToString());
    }
}

// M4 Phase E â€” persistent host mode. Usage:
//
//   dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo
//
// On start:
//   * If <data-dir>/sim.db exists and has a snapshot: Recover from it +
//     intent tail. Prints "Recovered at tick T; resumed with N in-flight
//     events."
//   * Else: Genesis seeds a small scenario, an initial snapshot is taken,
//     and a scripted set of intents is submitted durably.
//
// Main loop advances the sim in small batches, snapshotting on the
// configured cadence (5000 ticks OR 100 intents). SIGTERM (or Ctrl+C)
// triggers a clean shutdown: the current event finishes, a final snapshot
// is taken, and the process exits.
//
// The demo's scenario is intentionally long-running so a human can SIGKILL
// the process mid-flight and restart to observe recovery. For automated
// runs, the `--target-tick N` flag stops cleanly at tick N.
static class PersistentDemo
{
    const long DefaultTargetTick = 5000;
    const ulong Seed = 0xA0F;

    public static void Run(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "sim.db");
        var snapDbPath = Path.Combine(dataDir, "snapshots.db");

        using var intentStore = SqliteIntentStore.Open(dbPath);
        using var snapStore   = SqliteSnapshotStore.Open(snapDbPath);

        Simulation sim;
        if (snapStore.LoadLatest() is not null)
        {
            sim = Recovery.Recover(intentStore, snapStore, Seed);
            var inFlight = sim.QueuedEventCount;
            Console.WriteLine(
                $"Recovered at tick {sim.Now}; resumed with {inFlight} in-flight events.");
        }
        else
        {
            sim = ColdStart(intentStore, snapStore);
            Console.WriteLine(
                $"Genesis seeded at {dataDir}; initial snapshot at tick 0.");
        }

        var cadence = new SnapshotCadence();
        var shutdown = new ManualResetEventSlim(false);
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => shutdown.Set());
        using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT, c => { c.Cancel = true; shutdown.Set(); });

        const long batch = 100;
        while (sim.Now < DefaultTargetTick && !shutdown.IsSet)
        {
            var nextTick = Math.Min(sim.Now + batch, DefaultTargetTick);
            var preNow = sim.Now;
            sim.Run(until: nextTick);
            cadence.AccumulateTicks(sim.Now - preNow);
            if (cadence.ShouldSnapshot())
            {
                snapStore.SaveSnapshot(sim.Now, Snapshot.FormatVersion, Snapshot.Serialize(sim));
                cadence.Reset();
                Console.WriteLine($"  [tick {sim.Now}] snapshot saved.");
            }
            if (sim.Now == preNow) break; // queue empty
        }

        // Final snapshot on the way out â€” clean shutdown always leaves a
        // recoverable state.
        snapStore.SaveSnapshot(sim.Now, Snapshot.FormatVersion, Snapshot.Serialize(sim));
        Console.WriteLine(
            shutdown.IsSet
                ? $"Shutdown requested; final snapshot at tick {sim.Now}."
                : $"Target tick reached; final snapshot at tick {sim.Now}.");
        Console.WriteLine($"Final hash: {Snapshot.Hash(sim)}");
    }

    // Cold-start: builds a fresh world and submits a script of intents
    // durably. The scenario stays small and predictable so recovery is
    // easy to reason about by hand.
    static Simulation ColdStart(IIntentStore intents, ISnapshotStore snaps)
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Wood] = 100,
                    },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder),
                        new UnitSpawn(2, new TileCoord(0, 0), UnitRole.Hauler),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: Seed);
        // Pre-place a stockpile for the hauler to walk to.
        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(15, 0)) { OwnerId = 0 });
        stockpile.Deposit(Resource.Wood, 50);
        // Initial snapshot (BEFORE intents â€” so intent log replay handles
        // the seed-time submissions just as it would post-crash).
        snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(sim));

        // Two long-running intents that overlap. Both get logged AND
        // applied via the durable path.
        DurableSubmit.SubmitIntentDurable(sim, intents, 0,
            new MoveIntent(1, new TileCoord(18, 18)));
        DurableSubmit.SubmitIntentDurable(sim, intents, 0,
            new HaulIntent(2, new TileCoord(15, 0), new TileCoord(0, 0), Resource.Wood));

        return sim;
    }
}

// M9 â€” biome-degradation smoke. Builds a LumberCamp on Forest with one
// Lumberjack and lets production run to the dormancy point. Prints the
// own-tile biome and fertility at intervals â€” should show Forest â†’ Grassland
// (the M9 headline "extract-forever fix") and the eventual biome-mismatch
// dormancy.
static class DegradationDemo
{
    public static void Run()
    {
        // Genesis spec: a Forest world with a Castle, a Builder, a
        // Lumberjack, and a Hauler. The Builder constructs the LumberCamp;
        // the Lumberjack staffs it; the Hauler drains the buffer so the
        // camp keeps producing past its tiny buffer cap and we see the
        // own-tile degrade through Forest â†’ Grassland â†’ biome-mismatch
        // dormancy.
        var campAt = new TileCoord(4, 4);
        var spec = new GenesisSpec
        {
            Width = 10,
            Height = 10,
            DefaultBiome = Sim.Core.World.Biome.Forest,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Wood] = 50,
                    },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(Id: 1, campAt, UnitRole.Builder),
                        new UnitSpawn(Id: 2, campAt, UnitRole.Lumberjack),
                        new UnitSpawn(Id: 3, campAt, UnitRole.Hauler),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xDEAD_BEEF);

        // Place the LumberCamp construction site at campAt, hand-deposit the
        // materials (skipping the haul-from-Castle ceremony for clarity),
        // and start the build.
        sim.SubmitIntent(0, new PlaceSiteIntent(campAt, StructureKind.LumberCamp));
        sim.Run(until: 0);
        var site = (ConstructionSite)sim.World.Structures[campAt];
        foreach (var (r, n) in StructureCatalog.Spec(StructureKind.LumberCamp).BuildCost)
            site.Deposit(r, n);
        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(campAt, new[] { 1 }));
        sim.Run(until: StructureCatalog.Spec(StructureKind.LumberCamp).BuildDurationTicks);

        // Staff with Lumberjack â€” production arms here, M9 catches up the
        // (still-Forest) radius.
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(campAt, new[] { 2 }));
        sim.Run(until: sim.Now);

        Console.WriteLine("M9 degradation smoke: LumberCamp on Forest, single Lumberjack.");
        Console.WriteLine("Each step: run 5 production ticks, manually drain the buffer (simulating");
        Console.WriteLine("instant haul), re-arm. Stops when the camp goes dormant via biome-mismatch.");
        Console.WriteLine();
        Console.WriteLine("step | sim.Now | TickArmed | Buffer | own-tile biome | own-tile fertility");
        Console.WriteLine("-----+---------+-----------+--------+----------------+--------------------");
        PrintRow(0, sim, campAt);

        var cfg = sim.World.BiomeDegradationConfig;
        var step = 1;
        var totalProduced = 0;
        // Bounded loop. Each iteration advances sim by ~5 production periods
        // (50 ticks), drains the buffer. Re-arm only if the tile is still
        // the right biome; otherwise the camp has gone dormant via biome-
        // mismatch (the M9 headline) and we exit.
        var period = StructureCatalog.Spec(StructureKind.LumberCamp).ProductionPeriodTicks;
        while (sim.Now < 5_000)
        {
            sim.Run(until: sim.Now + period * 5);
            var camp = (Extractor)sim.World.Structures[campAt];
            totalProduced += camp.Buffer;
            camp.Buffer = 0;   // instant haul

            PrintRow(step, sim, campAt);

            // EXIT CHECK before re-arming: did the camp go dormant via
            // biome-mismatch? Two signals together:
            //   - !camp.TickArmed (dormancy fired this iteration)
            //   - The own tile's derived biome no longer matches Forest.
            var currentBiome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(sim.World, campAt, sim.Now, cfg);
            if (!camp.TickArmed && currentBiome != Biome.Forest)
            {
                Console.WriteLine();
                Console.WriteLine($"Headline: LumberCamp went dormant via biome-mismatch.");
                Console.WriteLine($"  Wood produced (cumulative across drains): {totalProduced}");
                Console.WriteLine($"  Own-tile biome now: {currentBiome}");
                Console.WriteLine($"  Sim ticks elapsed:  {sim.Now}");
                Console.WriteLine();
                Console.WriteLine("No infinite single-tile extraction. The player must relocate.");
                return;
            }
            // Otherwise re-arm and continue (M1 buffer-full dormancy is the
            // re-armable kind â€” that's not the M9 headline).
            sim.SubmitIntent(sim.Now, new AssignWorkersIntent(campAt, new[] { 2 }));
            sim.Run(until: sim.Now);
            step++;
        }
        Console.WriteLine();
        Console.WriteLine("Reached step limit without dormancy â€” rates may need re-tuning.");
    }

    private static void PrintRow(int step, Simulation sim, TileCoord at)
    {
        var cfg = sim.World.BiomeDegradationConfig;
        var ext = (Extractor)sim.World.Structures[at];
        var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(sim.World, at, sim.Now, cfg);
        var fert = Sim.Core.Biomes.BiomeDegradation.FertilityAt(sim.World, at, sim.Now, cfg);
        Console.WriteLine($"{step,4} | {sim.Now,7} | {ext.TickArmed,9} | {ext.Buffer,6} | {biome,-14} | {fert}");
    }
}
