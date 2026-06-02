using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

// Phase-D smoke: M0 walk + Phase-C build + Phase-D production.
//
// Genesis seeds: Builder (unit 2) and Lumberjack (unit 4) both on the Forest
// tile we'll build the LumberCamp on. The scenario:
//   1. Place a LumberCamp construction site at (3,3).
//   2. Pre-deposit build materials (Phase E will haul this for real).
//   3. Assign builder, build completes.
//   4. Assign the Lumberjack as worker — production arms.
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
        CastlePosition = new TileCoord(0, 0),
        StartingHoldings = new SortedDictionary<Resource, int>
        {
            [Resource.Wood] = 40,
            [Resource.Stone] = 20,
            [Resource.Food] = 10,
        },
        Units = new[]
        {
            new UnitSpawn(Id: 1, new TileCoord(0, 0), UnitRole.Builder),
            new UnitSpawn(Id: 2, new TileCoord(3, 3), UnitRole.Builder),    // builds the camp
            new UnitSpawn(Id: 3, new TileCoord(0, 0), UnitRole.Hauler, CargoCapacity: 5),
            new UnitSpawn(Id: 4, new TileCoord(3, 3), UnitRole.Lumberjack), // staffs the camp post-build
        },
    };
}

static Simulation RunScenario()
{
    var siteTile = new TileCoord(3, 3);
    var world = Genesis.Build(MakeSpec());
    var sim = new Simulation(world, seed: 0xC0FFEE);

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
    // Run until the build is done — sim.Now will land at BuildDurationTicks.
    sim.Run(until: spec.BuildDurationTicks);

    // Phase D layer: assign the Lumberjack to the now-built camp.
    sim.SubmitIntent(at: sim.Now, new AssignWorkersIntent(siteTile, new[] { 4 }));
    // Let production run long enough to fill the buffer and go dormant.
    sim.Run(until: sim.Now + spec.ProductionPeriodTicks * 25);

    return sim;
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

var first = RunScenario();
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
