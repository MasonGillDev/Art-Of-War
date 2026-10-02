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
    // Pure-read observation of the route arcs. ConditionAt computes any
    // pending decay without writing.
    var routeArcs = sim.World.Roads.Keys
        .OrderBy(e => e.A.Y).ThenBy(e => e.A.X).ThenBy(e => (byte)e.Direction)
        .ToList();
    if (routeArcs.Count == 0) { log("  (no road arcs)"); return; }
    foreach (var e in routeArcs)
    {
        var condition = Road.ConditionAt(sim.World, e, sim.Now);
        log($"  ({e.A.X},{e.A.Y})-({e.B.X},{e.B.Y}): condition={condition}");
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
var canalDemo = args.Length > 0 && args[0] == "--canal";
var cachesDemo = args.Length > 0 && args[0] == "--caches";
var automationDemo = args.Length > 0 && args[0] == "--automation";
var scoutingDemo = args.Length > 0 && args[0] == "--scouting";
var siegeDemo = args.Length > 0 && args[0] == "--siege";
var refiningDemo = args.Length > 0 && args[0] == "--refining";
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
else if (canalDemo)
{
    CanalDemo.Run();
}
else if (cachesDemo)
{
    CacheDemo.Run();
}
else if (automationDemo)
{
    AutomationDemo.Run();
}
else if (scoutingDemo)
{
    ScoutingDemo.Run();
}
else if (siegeDemo)
{
    SiegeDemo.Run();
}
else if (refiningDemo)
{
    RefiningDemo.Run();
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

// M20 — scouting reports demo. Dispatch a scout from a Lodge on a sortie past
// a foreign camp; it observes a fog-honest log along the way, returns home,
// and the server-side claims compiler turns the log into an honest report.
// Ends with the determinism checks (twin run + snapshot round-trip): the log
// is canonical sim state, the prose is a presentation-only VIEW of it.
static class ScoutingDemo
{
    static Sim.Core.Engine.Simulation Build()
    {
        var grid = new TileGrid(40, 40, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var castle = world.AddStructure(new Castle(new TileCoord(5, 20)) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 200); // keep the scout fed through the ride
        world.AddStructure(new Lodge(new TileCoord(6, 20)) { OwnerId = 0 });
        world.AddUnit(new Unit(1, new TileCoord(5, 20)) { Role = UnitRole.Scout });

        // Out east: House Ashford (owner 1) raising a dwelling, nine soldiers
        // idle beside it — a camp the scout will glimpse from its waypoints.
        world.AddStructure(new ConstructionSite(new TileCoord(30, 24), StructureKind.House) { OwnerId = 1 })
             .ProgressTicks = 720; // ~40% of the House's 1800-tick build
        for (var i = 0; i < 9; i++)
            world.AddUnit(new Unit(100 + i, new TileCoord(30, 24)) { OwnerId = 1, Role = UnitRole.Soldier });

        return new Sim.Core.Engine.Simulation(world, seed: 0x5C07);
    }

    static Sim.Core.Engine.Simulation RunOnce()
    {
        var sim = Build();
        sim.SubmitIntent(0, new Sim.Core.Scouting.DispatchScoutIntent(
            scoutUnitId: 1,
            waypoints: new List<TileCoord> { new(28, 22), new(28, 26) },
            returnRule: Sim.Core.Scouting.ScoutReturnRule.WaypointsExhausted));
        sim.Run();
        return sim;
    }

    public static void Run()
    {
        var sim = RunOnce();
        var mission = sim.World.ScoutMissions[1];

        Console.WriteLine("--- Scouting Demo (M20) ---");
        Console.WriteLine($"Scout dispatched from (5,20) past two waypoints; recall rule: WaypointsExhausted.");
        Console.WriteLine($"Final mission state: {mission.State}; observation legs: {mission.Legs.Count}; " +
                          $"scout home at ({sim.World.Units[1].Position.X},{sim.World.Units[1].Position.Y}).");
        Console.WriteLine();

        // Phase 4: narrate via Claude when ANTHROPIC_API_KEY is set; otherwise
        // the service falls back to the raw claims sheet. Either way the report
        // is honest — the prose is a view of the same canonical claims.
        var opts = Sim.Server.Scouting.ScoutNarrationOptions.FromEnvironment();
        Sim.Server.Scouting.IReportNarrator? narrator =
            opts.Enabled ? new Sim.Server.Scouting.ClaudeReportNarrator(opts) : null;
        var service = new Sim.Server.Scouting.ScoutReportNarrationService(narrator);
        var narrated = service.NarrateAsync(sim.World, mission, scoutName: "Maddox").GetAwaiter().GetResult();

        Console.WriteLine(opts.Enabled
            ? $"MADDOX'S REPORT — narrated by {opts.Model} (status: {narrated.Status}):"
            : "MADDOX'S REPORT — raw claims (put your key in anthropic-key.txt, or set ANTHROPIC_API_KEY, to narrate via Claude):");
        Console.WriteLine();
        Console.WriteLine(narrated.Prose);
        Console.WriteLine();
        if (narrated.Status == Sim.Server.Scouting.ReportStatus.Narrated)
        {
            Console.WriteLine("--- the canonical claims the prose is a view of (sketch-map source) ---");
            foreach (var c in narrated.Report.Claims)
                Console.WriteLine($"  [{c.Kind}] {c.Text}");
            Console.WriteLine();
        }

        // The log is deterministic sim state; the prose is a pure view of it.
        var twin = RunOnce();
        if (Snapshot.Hash(sim) != Snapshot.Hash(twin))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE: twin scouting runs diverged.");
            Environment.Exit(1);
        }
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0x5C07);
        if (Snapshot.Hash(sim) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE: restored scouting snapshot diverged.");
            Environment.Exit(1);
        }
        Console.WriteLine("OK: twin run identical; observation log round-trips through snapshot.");
    }
}

// Automation-substrate demo. A Maintain supply line keeps the castle stocked
// from a pre-buffered lumber camp while a Routine circuit laps two stops -
// the SubstrateDriver (the same brain GameHost runs) evaluating fog-filtered
// predicates and submitting ordinary intents. Ends with the substrate's
// headline check: a driverless replay of the intent log lands on the
// identical hash, which is what makes the driver a trusted advisor rather
// than a second source of truth.
static class AutomationDemo
{
    public static void Run()
    {
        Simulation Build()
        {
            var grid = new TileGrid(24, 24, Biome.Grassland);
            grid.SetBiome(new TileCoord(10, 5), Biome.Forest);
            var world = new GameWorld(grid);
            world.Players[0] = new Player(0);
            var castle = world.AddStructure(new Castle(new TileCoord(5, 5)) { OwnerId = 0 });
            castle.Deposit(Resource.Food, 50);
            var camp = world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(10, 5)) { OwnerId = 0 });
            camp.Buffer = 60;
            camp.TickArmed = false; // fixed stock â€” no workers in this demo
            world.AddUnit(new Unit(1, new TileCoord(6, 5)) { Role = UnitRole.Hauler });
            world.AddUnit(new Unit(2, new TileCoord(6, 8)) { Role = UnitRole.Scout });
            return new Simulation(world, seed: 0xAB7);
        }

        var sim = Build();
        Console.WriteLine("--- Automation Demo (substrate) ---");
        Console.WriteLine("Supply line: hauler 1 keeps the castle at >= 40 wood from the camp's stock.");
        Console.WriteLine("Circuit:     scout 2 laps (12,8) -> (6,8) forever.");
        Console.WriteLine();

        // A MAINTAIN thermostat: while the castle reads below 40 wood, send
        // the named hauler on a camp -> castle trip. The trigger carries the
        // threshold; Target is the same number for the reader's benefit.
        var supply = new Sim.Core.Automation.Order
        {
            SubjectKind = Sim.Core.Automation.SubjectKind.Structure,
            SubjectTile = new TileCoord(5, 5),
            Program = Sim.Core.Automation.ProgramKind.Maintain,
            Recipe = Sim.Core.Automation.RecipeKind.Haul,
            Resource = Resource.Wood,
            SourceTile = new TileCoord(10, 5),
            Target = 40,
            CrewMode = Sim.Core.Automation.CrewMode.Named,
            Trigger = Sim.Core.Automation.Trigger.When(
                Sim.Core.Automation.Predicate.StockBelow(new TileCoord(5, 5), Resource.Wood, 40)),
        };
        supply.NamedCrew.Add(1);
        sim.SubmitIntent(0, new Sim.Core.Automation.SetOrderIntent(supply) { PlayerId = 0 });

        // A ROUTINE circuit: two stops, no load/unload, no departure gate -
        // a patrol lap. Routines are Named-crew only (the route IS the
        // crew's identity), and carry the one durable cursor in the whole
        // substrate: "which stop is this body walking to" is not a fact the
        // map can answer when a tile appears twice in the circuit.
        var patrol = new Sim.Core.Automation.Order
        {
            SubjectKind = Sim.Core.Automation.SubjectKind.Structure,
            SubjectTile = new TileCoord(5, 5),
            Program = Sim.Core.Automation.ProgramKind.Routine,
            CrewMode = Sim.Core.Automation.CrewMode.Named,
            Steps =
            {
                new Sim.Core.Automation.RoutineStep { Tile = new TileCoord(12, 8) },
                new Sim.Core.Automation.RoutineStep { Tile = new TileCoord(6, 8) },
            },
        };
        patrol.NamedCrew.Add(2);
        sim.SubmitIntent(0, new Sim.Core.Automation.SetOrderIntent(patrol) { PlayerId = 0 });

        var journal = new Sim.Server.Automation.OrderJournal();
        var driver = new Sim.Server.Automation.SubstrateDriver(
            new Sim.Server.Automation.AutomationConfig { ThinkPeriodTicks = 30 }, journal);

        var castleLive = (Castle)sim.World.Structures[new TileCoord(5, 5)];
        long reportEvery = 5_000;
        long nextReport = reportEvery;
        // RUN then THINK - the same order GameHost uses. Thinking first
        // front-loads the driver's intents ahead of the same-tick scheduled
        // events, which hands them different Seq numbers than a chronological
        // replay assigns, and the headline check below fails.
        for (long t = 0; t <= 40_000; t += 30)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            if (t >= nextReport)
            {
                var camp2 = (Extractor)sim.World.Structures[new TileCoord(10, 5)];
                var supplyLive = sim.World.Orders[1];
                var last = journal.Last(supplyLive.OrderId);
                Console.WriteLine(
                    $"t {t,6}: castle wood={castleLive.AmountOf(Resource.Wood),3}  camp buffer={camp2.Buffer,3}  " +
                    $"supply[{(supplyLive.Enabled ? "on " : "off")} {last?.Outcome.ToString() ?? "-"}]  " +
                    $"scout 2 at {sim.World.Units[2].Position.X},{sim.World.Units[2].Position.Y} " +
                    $"(stop {sim.World.Orders[2].CurrentStep})");
                nextReport += reportEvery;
            }
        }
        sim.Run(until: 40_000);

        Console.WriteLine();
        Console.WriteLine($"Final castle wood:   {castleLive.AmountOf(Resource.Wood)} (target 40)");
        Console.WriteLine($"Intents in log:      {sim.IntentLog.Count} " +
            $"(status moves: {sim.IntentLog.Count(e => e.Intent is Sim.Core.Automation.OrderStatusIntent)})");

        // THE HEADLINE, live: replay the log into a fresh world with NO
        // driver and land on the identical hash. Two rules this loop must
        // respect, both learned the hard way:
        //   1. Replay the RESOLVED log, including REJECTED intents - a
        //      rejection still consumes a Seq number, and Seq is hashed.
        //   2. Group by tick and order by Seq, so same-tick intents replay
        //      in the sequence the live run gave them.
        // Round-tripping each intent through IntentJson also proves the
        // durable registry can carry every substrate intent the driver emits.
        var replay = Build();
        foreach (var batch in sim.ResolvedLog.OfType<Sim.Core.Intents.IntentEvent>()
                     .OrderBy(e => e.Seq).GroupBy(e => e.At))
        {
            replay.Run(until: batch.Key);
            foreach (var ev in batch)
            {
                var (typeName, payload) = Sim.Persistence.IntentJson.Serialize(ev.Intent);
                replay.SubmitIntent(batch.Key,
                    Sim.Persistence.IntentJson.Deserialize(typeName, payload));
            }
        }
        replay.Run(until: sim.Now);

        var liveHash = Snapshot.Hash(sim);
        var replayHash = Snapshot.Hash(replay);
        Console.WriteLine($"Live hash:           {liveHash}");
        Console.WriteLine($"Driverless replay:   {replayHash}");
        if (liveHash != replayHash)
        {
            Console.Error.WriteLine("HEADLINE FAILURE: driverless replay diverged from the live run.");
            Environment.Exit(1);
        }
        Console.WriteLine("OK: driverless replay reproduced the live world exactly.");
    }
}

// M46 — Group lifecycle demo (docs/m46-groups-spec.md). An army of two companies — six
// scattered soldiers and a farmer at work — is created, mustered (the farmer leaves his
// farm and keeps its slot), marched in formation, and dismissed (the farmer goes back to
// work). Prints each step, then asserts twin-run hash equality and a snapshot round-trip.
static class GroupDemo
{
    public static void Run()
    {
        var farmAt = new TileCoord(3, 9);
        var musterAt = new TileCoord(12, 6);
        var marchTo = new TileCoord(17, 9);

        Simulation Build()
        {
            var grid = new TileGrid(20, 12, Biome.Grassland);
            var world = new GameWorld(grid);
            world.Players[0] = new Player(0);
            world.AddStructure(new Castle(new TileCoord(6, 6)) { OwnerId = 0 });
            world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
            world.AddUnit(new Unit(1, farmAt) { Role = UnitRole.Farmer });
            for (var id = 2; id <= 7; id++)
                world.AddUnit(new Unit(id, new TileCoord(id, 1 + id % 3)) { Role = UnitRole.Soldier });
            return new Simulation(world, seed: 0xC0DE);
        }

        void Script(Simulation sim, bool talk)
        {
            void Say(string line) { if (talk) Console.WriteLine(line); }
            var world = sim.World;
            sim.SubmitIntent(0, new AssignWorkersIntent(farmAt, new[] { 1 }));
            sim.SubmitIntent(0, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true));
            sim.SubmitIntent(0, new CreateGroupIntent("Guard", new[] { 2, 3, 4, 5, 6, 7 }, parentId: 1));
            sim.SubmitIntent(0, new CreateGroupIntent("Militia", new[] { 1 }, parentId: 1));
            sim.Run(until: 0);
            Say($"Created: Army {{ Guard (6 soldiers), Militia (farmer 1) }}; farmer 1 is {world.Units[1].Activity}");

            sim.SubmitIntent(sim.Now, new MusterGroupIntent(1, musterAt));
            sim.Run(until: sim.Now);
            var farm = (Extractor)world.Structures[farmAt];
            Say($"Muster → {musterAt.X},{musterAt.Y}: farm workers [{string.Join(",", farm.Workers)}], held [{string.Join(",", farm.HeldBy)}]; farmer's saved task {world.Units[1].SavedTask?.Kind}");
            while (world.Groups[2].State != GroupState.Idle || world.Groups[3].State != GroupState.Idle) sim.Run(until: sim.Now + 10);
            var p = GroupMuster.Progress(world, world.Groups[1]);
            Say($"  Formed at tick {sim.Now}: {p.Here} here, {p.OnTheWay} on the way");

            sim.SubmitIntent(sim.Now, new MoveGroupIntent(1, marchTo));
            sim.Run(until: sim.Now);
            while (world.Groups[2].State != GroupState.Idle || world.Groups[3].State != GroupState.Idle) sim.Run(until: sim.Now + 10);
            Say($"March → {marchTo.X},{marchTo.Y}: arrived at tick {sim.Now}; members at " +
                string.Join(" ", Enumerable.Range(1, 7).Select(id => $"{id}:{world.Units[id].Position.X},{world.Units[id].Position.Y}")));

            sim.SubmitIntent(sim.Now, new DismissGroupIntent(1));
            sim.Run(until: sim.Now + 3000);
            Say($"Dismiss: farmer 1 is {world.Units[1].Activity} at {world.Units[1].Position.X},{world.Units[1].Position.Y}; farm workers [{string.Join(",", farm.Workers)}], held [{string.Join(",", farm.HeldBy)}]");
            Say($"  Groups kept: {string.Join(", ", world.Groups.Values.Select(g => $"{g.Name} ({g.State})"))}");
        }

        Console.WriteLine("--- Group Demo (M46) ---");
        var sim = Build();
        Script(sim, talk: true);
        Console.WriteLine($"Final hash: {Snapshot.Hash(sim)}");

        var sim2 = Build();
        Script(sim2, talk: false);
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
            Rivers = MapGenerator.ToRiverOverrides(map),
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
                // A river tile shows as '=' — the river runs along one of its
                // edges (docs/rivers.md); the biome underneath is still land.
                else if (map.Rivers[x, y] != RiverEdge.None) sb.Append('=');
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

// M23 — loot caches smoke. Genesis scatters unowned treasure into the fog,
// only ever on tiles no player has seen. Prints the scatter (location + loot),
// confirms the spawn-in-fog invariant, marches a hauler to the nearest find and
// loots it cargo-capped, then runs the milestone determinism checks (twin
// genesis scatters identically + the looted world round-trips).
static class CacheDemo
{
    static GenesisSpec MakeSpec() => new()
    {
        Width = 24, Height = 24,
        Caches = new Sim.Core.Caches.CacheConfig(Count: 12),
        FactionStarts = new[]
        {
            new FactionStartSpec
            {
                OwnerId = 0,
                CastlePosition = new TileCoord(2, 2),
                CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 500 },
                UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(2, 2), UnitRole.Hauler) },
            },
        },
    };

    static Simulation Build() => new(MakeSpec(), seed: 0xCAC4E);

    public static void Run()
    {
        var sim = Build();
        var caches = sim.World.Structures.Values.OfType<Cache>()
            .OrderBy(c => c.At.Y).ThenBy(c => c.At.X).ToList();

        Console.WriteLine("--- Loot Caches Demo (M23) ---");
        Console.WriteLine($"{caches.Count} caches scattered into the genesis fog (seed 0xCAC4E).");

        var seen = new HashSet<TileCoord>();
        foreach (var set in sim.World.Explored.Values) seen.UnionWith(set);
        var leak = caches.FirstOrDefault(c => seen.Contains(c.At));
        Console.WriteLine(leak is null
            ? "Every cache is on a tile NO player has seen at genesis. OK."
            : $"INVARIANT VIOLATION: cache at {leak.At.X},{leak.At.Y} is already seen!");
        Console.WriteLine();
        Console.WriteLine("Caches (location — loot):");
        foreach (var c in caches)
            Console.WriteLine($"  ({c.At.X,2},{c.At.Y,2}) — " +
                string.Join(", ", c.Holdings.Select(kv => $"{kv.Value} {kv.Key}")));
        Console.WriteLine();

        // March the hauler to the first cache and loot one resource cargo-capped.
        var target = caches[0];
        var resource = target.Holdings.Keys.First();
        var have = target.AmountOf(resource);
        new MoveIntent(1, target.At) { PlayerId = 0 }.Resolve(sim);
        // Bounded run: enough to finish the march (which also REVEALS the cache
        // as the hauler closes in), well before any far-future lifespan event.
        sim.Run(until: 20_000);
        var hauler = sim.World.Units[1];
        new Sim.Core.Caches.LootCacheIntent(1, resource) { PlayerId = 0 }.Resolve(sim);
        Console.WriteLine(
            $"Hauler marched to ({target.At.X},{target.At.Y}) and looted {resource}: " +
            $"carries {hauler.CargoAmount}/{hauler.CargoCapacity} {hauler.CargoResource}.");
        Console.WriteLine(
            sim.World.Structures.TryGetValue(target.At, out var s) && s is Cache left
                ? $"  Cache persists with {left.AmountOf(resource)} {resource} left (re-lootable)."
                : $"  Cache emptied ({have} {resource}) and removed.");
        Console.WriteLine();

        // Determinism: two fresh genesis builds scatter identically.
        if (Snapshot.Hash(Build()) != Snapshot.Hash(Build()))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE: twin cache scatter diverged.");
            Environment.Exit(1);
        }
        // The looted world round-trips through snapshot.
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xCAC4E);
        if (Snapshot.Hash(sim) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE: looted cache world diverged.");
            Environment.Exit(1);
        }
        Console.WriteLine("OK: twin genesis scatters identical caches; looted world round-trips.");
    }
}

// M21 — canals smoke. A latched desert field sits inland, beyond the coastal
// sea's reach. The player digs a canal from the coast to the field: the path
// floods to Water, irrigates the field (M21 latch-lift restores degraded land
// near water), and a boat sails up the new canal to the heart of the kingdom.
// Ends with the milestone determinism checks (twin run + snapshot round-trip).
static class CanalDemo
{
    // Demo-scale degradation config (the production default's hourly pacing
    // would take game-months to show recovery). Same shape as DegradationDemo.
    static readonly Sim.Core.Biomes.BiomeDegradationConfig Cfg = new(
        ForestBaseline: 100, GrasslandBaseline: 50, DesertBaseline: 10,
        HillsBaseline: 30, MountainBaseline: 60, WaterBaseline: 0,
        ForestThreshold: 75, DesertThreshold: 25,
        RecoveryAmount: 1, RecoveryPeriod: 30,
        DegradePeriod: 40, DegradeRadius: 2, WaterRecoveryRadius: 2);

    static readonly TileCoord Field = new(3, 5);
    static readonly List<TileCoord> Path = new() { new(1, 5), new(2, 5) };

    static Simulation Build()
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        for (var y = 0; y < 12; y++) grid.SetBiome(new TileCoord(0, y), Biome.Water); // west coast
        var world = new GameWorld(grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
            new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(), Cfg);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(6, 0)) { OwnerId = 0 });
        // The field, degraded into latched desert (fertility 20 < threshold 25),
        // 3 tiles from the coast so it is NOT near water until the canal arrives.
        world.Fertility[Field] = new Sim.Core.Biomes.Fertility(-30, 0);
        // A boat waiting on the coast.
        world.AddUnit(new Unit(50, new TileCoord(0, 6))
            { Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water, BornTick = 0 });
        // Three builders on the canal's anchor tile (Path[0]).
        for (var i = 1; i <= 3; i++)
            world.AddUnit(new Unit(i, Path[0]) { Role = UnitRole.Builder, OwnerId = 0 });
        return new Simulation(world, seed: 0xCABA1);
    }

    static string FieldState(Simulation sim, long tick) =>
        $"{Sim.Core.Biomes.BiomeDegradation.BiomeAt(sim.World, Field, tick, Cfg)} " +
        $"(fertility {Sim.Core.Biomes.BiomeDegradation.FertilityAt(sim.World, Field, tick, Cfg)})";

    static Simulation RunScenario(Action<string>? log = null)
    {
        var sim = Build();
        var beforeFlood = FieldState(sim, 0);

        // Dig the canal from the coast toward the field.
        new Sim.Core.Canals.PlaceCanalIntent(Path) { PlayerId = 0 }.Resolve(sim);
        var site = (ConstructionSite)sim.World.Structures[Path[0]];
        foreach (var (r, n) in site.Required) site.Deposit(r, n); // hand-deliver stone/wood
        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(Path[0], new[] { 1, 2, 3 }));
        sim.Run(); // build completes; the path floods to Water
        var floodedAt = sim.Now;

        // Sail the boat up the new canal to the inland end — an inland supply line.
        new MoveIntent(50, new TileCoord(2, 5)) { PlayerId = 0 }.Resolve(sim);
        sim.Run();

        // Let the irrigated field recover, then settle.
        sim.Schedule(sim.Now + Cfg.RecoveryPeriod * 30, new NoOpEvent());
        sim.Run();

        if (log != null)
        {
            log("--- Canals Demo (M21) ---");
            log($"A latched desert field sits inland at ({Field.X},{Field.Y}): {beforeFlood}.");
            log("");
            log($"Canal dug {string.Join(" -> ", Path.Select(t => $"({t.X},{t.Y})"))}, flooded at tick {floodedAt}:");
            foreach (var t in Path)
                log($"  tile ({t.X},{t.Y}) is now {sim.World.Grid.BiomeAt(t)}");
            log("");
            var boat = sim.World.Units[50].Position;
            log($"Boat sailed up the canal to ({boat.X},{boat.Y}) — supply lines reach inland.");
            log("");
            log("Field beside the canal, recovering after irrigation:");
            log($"  at flood   (tick {floodedAt}): {FieldState(sim, floodedAt)}");
            log($"  +150 ticks: {FieldState(sim, floodedAt + 150)}");
            log($"  +900 ticks: {FieldState(sim, floodedAt + 900)} — restored to grassland.");
            log("");
            log("The desert latch is no longer permanent NEAR WATER. Where you farm matters,");
            log("and a canal can reclaim land you thought you'd lost forever.");
            log("");
        }
        return sim;
    }

    public static void Run()
    {
        var first = RunScenario(Console.WriteLine);
        var second = RunScenario();
        if (Snapshot.Hash(first) != Snapshot.Hash(second))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE: twin canal runs diverged.");
            Environment.Exit(1);
        }
        var bytes = Snapshot.Serialize(first);
        var restored = Snapshot.Restore(bytes, seed: 0xCABA1);
        if (Snapshot.Hash(first) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE: restored canal snapshot diverged.");
            Environment.Exit(1);
        }
        Console.WriteLine("OK: twin run identical; canal world round-trips through snapshot " +
            $"({bytes.Length} bytes).");
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
        // the Lumberjack staffs it; we drain the buffer so the camp keeps
        // producing and we watch its CLAIMED tiles degrade through
        // Forest → Grassland → claim-exhausted dormancy (M15).
        //
        // Demo-scale degradation config (the production default exhausts a
        // claim after ~1250 hourly periods ≈ 52 game-days — correct for
        // play, useless for a smoke demo).
        var campAt = new TileCoord(4, 4);
        var spec = new GenesisSpec
        {
            Width = 10,
            Height = 10,
            DefaultBiome = Sim.Core.World.Biome.Forest,
            BiomeDegradation = new Sim.Core.Biomes.BiomeDegradationConfig(
                ForestBaseline: 100, GrasslandBaseline: 50, DesertBaseline: 10,
                HillsBaseline: 30, MountainBaseline: 60, WaterBaseline: 0,
                ForestThreshold: 75, DesertThreshold: 25,
                RecoveryAmount: 1, RecoveryPeriod: 30,
                DegradePeriod: 40, DegradeRadius: 2),
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

        Console.WriteLine("M15 degradation smoke: LumberCamp on Forest, single Lumberjack.");
        Console.WriteLine("Each step: run 5 production ticks, manually drain the buffer (simulating");
        Console.WriteLine("instant haul), re-arm. Stops when the camp exhausts its CLAIM (M15).");
        Console.WriteLine();
        Console.WriteLine("step | sim.Now | TickArmed | Buffer | claim biome | claim fertility");
        Console.WriteLine("-----+---------+-----------+--------+-------------+------------------");
        var campRef = (Extractor)sim.World.Structures[campAt];
        PrintRow(0, sim, campAt, campRef.ClaimTiles.Count > 0 ? campRef.ClaimTiles[0] : campAt);

        var cfg = sim.World.BiomeDegradationConfig;
        var step = 1;
        var totalProduced = 0;
        // Bounded loop. Each iteration advances sim by ~5 production
        // periods, drains the buffer, re-arms. ArmIfDormant declines once
        // the claim is exhausted (the M15 headline) and we exit.
        var period = StructureCatalog.Spec(StructureKind.LumberCamp).ProductionPeriodTicks;
        while (sim.Now < 5_000)
        {
            sim.Run(until: sim.Now + period * 5);
            var camp = (Extractor)sim.World.Structures[campAt];
            totalProduced += camp.Buffer;
            camp.Buffer = 0;   // instant haul

            var watch = camp.ClaimTiles.Count > 0 ? camp.ClaimTiles[0] : campAt;
            PrintRow(step, sim, campAt, watch);

            // EXIT CHECK before re-arming: dormant with an empty (drained)
            // buffer + zero in-band claims = claim exhausted, the camp is
            // done. Re-arming below would be declined anyway.
            var inBand = Sim.Core.World.Claims.InBandClaimCount(sim.World, camp, sim.Now);
            if (!camp.TickArmed && inBand == 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Headline: LumberCamp exhausted its claim and went dormant.");
                Console.WriteLine($"  Wood produced (cumulative across drains): {totalProduced}");
                Console.WriteLine($"  Claim tiles now: " + string.Join(", ", camp.ClaimTiles.Select(
                    t => Sim.Core.Biomes.BiomeDegradation.BiomeAt(sim.World, t, sim.Now, cfg))));
                Console.WriteLine($"  Own tile (the building) still: " +
                    Sim.Core.Biomes.BiomeDegradation.BiomeAt(sim.World, campAt, sim.Now, cfg));
                Console.WriteLine($"  Sim ticks elapsed:  {sim.Now}");
                Console.WriteLine();
                Console.WriteLine("No infinite extraction. The player must claim fresh land.");
                return;
            }
            // Otherwise re-arm and continue (M1 buffer-full dormancy is the
            // re-armable kind — that's not the headline).
            sim.SubmitIntent(sim.Now, new AssignWorkersIntent(campAt, new[] { 2 }));
            sim.Run(until: sim.Now);
            step++;
        }
        Console.WriteLine();
        Console.WriteLine("Reached step limit without dormancy — rates may need re-tuning.");
    }

    // `campAt` locates the extractor; `watch` is the tile whose biome /
    // fertility we report (M15: a CLAIMED tile — the camp's own tile never
    // degrades).
    private static void PrintRow(int step, Simulation sim, TileCoord campAt, TileCoord watch)
    {
        var cfg = sim.World.BiomeDegradationConfig;
        var ext = (Extractor)sim.World.Structures[campAt];
        var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(sim.World, watch, sim.Now, cfg);
        var fert = Sim.Core.Biomes.BiomeDegradation.FertilityAt(sim.World, watch, sim.Now, cfg);
        Console.WriteLine($"{step,4} | {sim.Now,7} | {ext.TickArmed,9} | {ext.Buffer,6} | {biome,-11} | {fert}");
    }
}

// M24 — sieges smoke. Two factions at war. The attacker marches a squad onto
// the enemy castle, the round-based siege chips it down, the castle becomes
// rubble, the defender is defeated, and a GameOverEvent names the winner.
// Closes with the milestone determinism checks (twin run + snapshot
// round-trip).
static class SiegeDemo
{
    static Sim.Core.World.GenesisSpec MakeSpec() => new()
    {
        Width = 12, Height = 6,
        Diplomacy = new Sim.Core.Diplomacy.DiplomacyConfig(Delay: 5, ProposalExpiryTicks: 200),
        Combat = new Sim.Core.Combat.CombatConfig(RoundIntervalTicks: 10),
        FactionStarts = new[]
        {
            new Sim.Core.World.FactionStartSpec
            {
                OwnerId = 0,
                CastlePosition = new Sim.Core.World.TileCoord(0, 3),
                UnitSpawns = new[]
                {
                    new Sim.Core.World.UnitSpawn(100, new Sim.Core.World.TileCoord(0, 3), Sim.Core.World.UnitRole.Soldier),
                    new Sim.Core.World.UnitSpawn(101, new Sim.Core.World.TileCoord(0, 3), Sim.Core.World.UnitRole.Soldier),
                    new Sim.Core.World.UnitSpawn(102, new Sim.Core.World.TileCoord(0, 3), Sim.Core.World.UnitRole.Soldier),
                },
            },
            new Sim.Core.World.FactionStartSpec
            {
                OwnerId = 1,
                CastlePosition = new Sim.Core.World.TileCoord(11, 3),
            },
        },
    };

    static Simulation Build()
    {
        var sim = new Simulation(MakeSpec(), seed: 0x51E6E);
        // Telegraph the war via the public intent path; with Delay=5 above the
        // WarBecomesEffectiveEvent fires by tick 5. Then drop the castle's HP
        // so the smoke finishes in seconds, not in-game years. The MECHANICS
        // are identical at 30 HP as at 1000 — only the round count shrinks.
        new Sim.Core.Diplomacy.DeclareWarIntent(0, 1) { PlayerId = 0 }.Resolve(sim);
        sim.Run(until: 5);
        ((Castle)sim.World.Structures[new TileCoord(11, 3)]).Health = 30;
        return sim;
    }

    public static void Run()
    {
        Console.WriteLine("--- Sieges & Conquest Demo (M24) ---");
        Console.WriteLine("Two factions at war; faction 0 marches 3 Soldiers onto faction 1's castle.");
        Console.WriteLine($"Castle starts at {StructureCatalog.Spec(StructureKind.Castle).BaseHealth} HP " +
            "(dialed to 30 here so the demo finishes in seconds).");
        Console.WriteLine();

        var sim = Build();
        var castleTile = new TileCoord(11, 3);

        // March each soldier across the map. MoveIntent.Resolve schedules the
        // arrival; the attackers converge on the castle.
        foreach (var id in new[] { 100, 101, 102 })
            new MoveIntent(id, castleTile) { PlayerId = 0 }.Resolve(sim);

        Console.WriteLine("tick |    castle HP | defenders | attackers | event");
        Console.WriteLine("-----+--------------+-----------+-----------+-------------------------");
        long lastReportTick = -1;
        for (var t = 0L; t <= 5_000; t += 5)
        {
            sim.Run(until: t);
            if (t == lastReportTick) continue;
            lastReportTick = t;

            var occupant = sim.World.Structures.GetValueOrDefault(castleTile);
            var hp = occupant?.Health ?? 0;
            var atk = sim.World.Units.Values.Count(u => u.OwnerId == 0 && u.Position == castleTile);
            var def = sim.World.Units.Values.Count(u => u.OwnerId == 1 && u.Position == castleTile);
            var note = occupant switch
            {
                Castle => "",
                Rubble => "CASTLE RAZED",
                _ => "(no structure)"
            };

            // Only report ticks where something happened.
            if (atk == 0 && def == 0 && occupant is Castle) continue;
            Console.WriteLine($"{t,4} | {hp,4} ({(occupant?.Kind.ToString() ?? "—"),-6}) | {def,9} | {atk,9} | {note}");
            if (sim.World.Players[1].Defeated)
            {
                Console.WriteLine();
                Console.WriteLine($"Player 1 defeated at tick {t}.");
                break;
            }
        }

        // Trace through the resolved log for the named transitions.
        Console.WriteLine();
        Console.WriteLine("Resolved-log highlights:");
        foreach (var e in sim.ResolvedLog)
        {
            if (e is Sim.Core.Sieges.PlayerDefeatedEvent pd)
                Console.WriteLine($"  tick {pd.At,4}: {pd.Describe()}");
            else if (e is Sim.Core.Sieges.GameOverEvent go)
                Console.WriteLine($"  tick {go.At,4}: {go.Describe()}");
        }

        // Determinism: twin run hash equality.
        var twin = Build();
        foreach (var id in new[] { 100, 101, 102 })
            new MoveIntent(id, castleTile) { PlayerId = 0 }.Resolve(twin);
        twin.Run(until: sim.Now);
        if (Snapshot.Hash(sim) != Snapshot.Hash(twin))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE: twin siege diverged.");
            Environment.Exit(1);
        }

        // Round-trip the post-game state through snapshot.
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0x51E6E);
        if (Snapshot.Hash(sim) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE: razed-castle world diverged.");
            Environment.Exit(1);
        }

        Console.WriteLine();
        Console.WriteLine("OK: twin run identical; post-game snapshot round-trips.");
    }
}

// Refining smoke (docs/refining-structures.md): the two-hop chain end to end.
//
//   Mine (Hills) --ore-->  Smelter  --iron-->  Smithy  --craft-->  Sword
//   Stockpile   --wood-->  Smelter (fuel)
//
// Four haulers each run one leg every day; the smelter burns fuel per iron;
// the smithy forges the moment it holds a sword's worth. Prints a day-by-day
// ledger so a human can watch ore turn into a blade, then proves twin-run
// determinism and a post-game snapshot round-trip.
static class RefiningDemo
{
    static Simulation Build()
    {
        var grid = new TileGrid(12, 8, Biome.Grassland);
        grid.SetBiome(new TileCoord(2, 2), Biome.Mountain);   // M44 — ore is a mountain resource
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(0, 0)) { OwnerId = 0 });

        var mine = world.AddStructure(new Extractor(StructureKind.Mine, new TileCoord(2, 2)) { OwnerId = 0 });
        var smelter = world.AddStructure(new Extractor(StructureKind.Smelter, new TileCoord(4, 2)) { OwnerId = 0 });
        var woodpile = world.AddStructure(new Stockpile(new TileCoord(4, 3)) { OwnerId = 0 });
        var smithy = world.AddStructure(new Smithy(new TileCoord(6, 2)) { OwnerId = 0 });
        woodpile.Deposit(Resource.Wood, 200);

        world.AddUnit(new Unit(1, mine.At) { Role = UnitRole.Miner, OwnerId = 0 });
        world.AddUnit(new Unit(2, mine.At) { Role = UnitRole.Miner, OwnerId = 0 });
        world.AddUnit(new Unit(3, smelter.At) { Role = UnitRole.Miner, OwnerId = 0 });
        world.AddUnit(new Unit(10, mine.At) { Role = UnitRole.Hauler, OwnerId = 0 });      // ore leg
        world.AddUnit(new Unit(11, woodpile.At) { Role = UnitRole.Hauler, OwnerId = 0 });  // fuel leg
        world.AddUnit(new Unit(12, smelter.At) { Role = UnitRole.Hauler, OwnerId = 0 });   // iron leg
        world.AddUnit(new Unit(13, smithy.At) { Role = UnitRole.Hauler, OwnerId = 0 });    // smithy wood leg

        var sim = new Simulation(world, seed: 0x1F0A);
        sim.SubmitIntent(0, new AssignWorkersIntent(mine.At, new[] { 1, 2 }) { PlayerId = 0 });
        sim.SubmitIntent(0, new AssignWorkersIntent(smelter.At, new[] { 3 }) { PlayerId = 0 });
        return sim;
    }

    // One round trip per hauler per day, submitted as ordinary intents. A
    // standing supply-line order would do this for real; the demo keeps the
    // wiring visible.
    static void Legs(Simulation sim, long at)
    {
        var mine = new TileCoord(2, 2); var smelter = new TileCoord(4, 2);
        var woodpile = new TileCoord(4, 3); var smithy = new TileCoord(6, 2);
        sim.SubmitIntent(at, new HaulIntent(10, mine, smelter, Resource.Ore) { PlayerId = 0 });
        sim.SubmitIntent(at, new HaulIntent(11, woodpile, smelter, Resource.Wood) { PlayerId = 0 });
        sim.SubmitIntent(at, new HaulIntent(12, smelter, smithy, Resource.Iron) { PlayerId = 0 });
        sim.SubmitIntent(at, new HaulIntent(13, woodpile, smithy, Resource.Wood) { PlayerId = 0 });
        sim.SubmitIntent(at + 1, new Sim.Core.Equipment.CraftEquipmentIntent(smithy, Resource.Sword) { PlayerId = 0 });
    }

    static Simulation Play(int days, Action<string>? log)
    {
        var sim = Build();
        var mine = (Extractor)sim.World.Structures[new TileCoord(2, 2)];
        var smelter = (Extractor)sim.World.Structures[new TileCoord(4, 2)];
        var smithy = (Smithy)sim.World.Structures[new TileCoord(6, 2)];
        log?.Invoke($"{"day",3} | {"mine ore",8} | {"smelter ore/wood",16} | {"iron",4} | {"smithy iron",11} | {"swords",6}");
        for (var d = 1; d <= days; d++)
        {
            Legs(sim, sim.Now);
            sim.Run(until: (long)d * Sim.Core.Time.Day);
            log?.Invoke($"{d,3} | {mine.Buffer,8} | {smelter.InputOf(Resource.Ore),7}/{smelter.InputOf(Resource.Wood),-8} | " +
                        $"{smelter.Buffer,4} | {smithy.AmountOf(Resource.Iron),11} | {smithy.AmountOf(Resource.Sword),6}");
        }
        return sim;
    }

    public static void Run()
    {
        var spec = StructureCatalog.Spec(StructureKind.Smelter);
        var recipe = string.Join(" + ", spec.InputCost.Select(kv => $"{kv.Value} {kv.Key}"));
        var sword = Sim.Core.Equipment.EquipmentCatalog.Spec(Resource.Sword);
        Console.WriteLine("--- Refining Demo (docs/refining-structures.md) ---");
        Console.WriteLine($"Smelter: {recipe} -> 1 {spec.OutputResource} per worker per day (Miner x2), feed store {spec.InputCap}.");
        Console.WriteLine($"Sword at the {sword.CraftedAt}: {string.Join(" + ", sword.CraftCost.Select(kv => $"{kv.Value} {kv.Key}"))}.");
        Console.WriteLine("Mine (2 miners) -> ore leg -> Smelter <- fuel leg <- woodpile; iron leg -> Smithy.");
        Console.WriteLine();

        var first = Play(days: 14, log: Console.WriteLine);
        var smithy = (Smithy)first.World.Structures[new TileCoord(6, 2)];
        var castle = (Castle)first.World.Structures[new TileCoord(0, 0)];
        Console.WriteLine();
        if (smithy.AmountOf(Resource.Sword) == 0)
        {
            Console.Error.WriteLine("SMOKE FAILURE: no sword forged in 14 days.");
            Environment.Exit(1);
        }
        Console.WriteLine($"Forged {smithy.AmountOf(Resource.Sword)} sword(s) from ore that never touched the Smithy as ore.");
        Console.WriteLine($"Castle ore: {castle.AmountOf(Resource.Ore)} (nothing bypassed the smelter).");

        var second = Play(days: 14, log: null);
        if (Snapshot.Hash(first) != Snapshot.Hash(second))
        {
            Console.Error.WriteLine("DETERMINISM FAILURE: twin refining runs diverged.");
            Environment.Exit(1);
        }
        var restored = Snapshot.Restore(Snapshot.Serialize(first), seed: 0x1F0A);
        if (Snapshot.Hash(first) != Snapshot.Hash(restored))
        {
            Console.Error.WriteLine("ROUND-TRIP FAILURE: refining snapshot did not restore identically.");
            Environment.Exit(1);
        }
        Console.WriteLine("OK: twin run identical; snapshot with a mid-chain smelter round-trips.");
    }
}
