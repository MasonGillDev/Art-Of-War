using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

// Phase-C smoke: in addition to the M0 move loop, place a LumberCamp
// construction site on a Forest tile, pre-deposit its build cost
// (the haul that would do this lives in Phase E), assign a builder, and
// let the build run to completion. Verify twin runs match and the result
// round-trips through Serialize/Restore.

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
            new UnitSpawn(Id: 2, new TileCoord(3, 3), UnitRole.Builder),  // already on the Forest tile we'll build on
            new UnitSpawn(Id: 3, new TileCoord(0, 0), UnitRole.Hauler, CargoCapacity: 5),
        },
    };
}

static Simulation BuildScenario()
{
    var world = Genesis.Build(MakeSpec());
    var sim = new Simulation(world, seed: 0xC0FFEE);

    // M0 loop: unit 1 walks across the grid.
    sim.SubmitIntent(at: 0, new MoveIntent(unitId: 1, new TileCoord(9, 9)));

    // Phase C: build a LumberCamp at (3,3) using unit 2.
    var siteTile = new TileCoord(3, 3);
    sim.SubmitIntent(at: 0, new PlaceSiteIntent(siteTile, StructureKind.LumberCamp));

    // Materials would normally arrive by haul (Phase E). For the smoke we
    // pre-deposit by running the place intent first then directly seeding the
    // site. This bypasses physicality on purpose for the demo.
    sim.Run(until: 0);  // resolves the place intent
    var site = (ConstructionSite)sim.World.Structures[siteTile];
    var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
    foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);

    sim.SubmitIntent(at: sim.Now, new AssignBuildersIntent(siteTile, new[] { 2 }));
    return sim;
}

static void Print(string label, Simulation sim)
{
    Console.WriteLine($"--- {label} ---");
    var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
    Console.WriteLine($"Castle holdings:");
    foreach (var (r, n) in castle.Holdings) Console.WriteLine($"  {r}: {n}");
    Console.WriteLine($"Units (id: role, position, activity):");
    foreach (var (id, u) in sim.World.Units)
        Console.WriteLine($"  {id}: {u.Role}, {u.Position.X},{u.Position.Y}, {u.Activity}");
    Console.WriteLine($"Intents submitted:     {sim.IntentLog.Count}");

    sim.Run();

    Console.WriteLine($"Events resolved:       {sim.ResolvedLog.Count}");
    Console.WriteLine($"Final sim tick:        {sim.Now}");

    var site = new TileCoord(3, 3);
    if (sim.World.Structures.TryGetValue(site, out var built))
        Console.WriteLine($"Structure @ 3,3:       {built.Kind}");

    var mover = sim.World.Units[1];
    Console.WriteLine($"Unit 1 final position: {mover.Position.X},{mover.Position.Y}");
    var builder = sim.World.Units[2];
    Console.WriteLine($"Unit 2 (builder):      activity={builder.Activity}");

    var rejects = sim.ResolvedLog.Count(e => e.Outcome.IsRejected);
    Console.WriteLine($"Rejected events:       {rejects}");
    Console.WriteLine($"Snapshot hash:         {Snapshot.Hash(sim)}");
    Console.WriteLine();
}

var first = BuildScenario();
Print("Run 1", first);

var second = BuildScenario();
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

Console.WriteLine("OK: identical intent log produced identical final state.");
Console.WriteLine($"OK: serialized snapshot ({bytes.Length} bytes) restored to identical state.");
