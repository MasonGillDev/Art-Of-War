using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

// Phase-A smoke test: build a richer world via Genesis (castle, biomes,
// roled units), issue a single move intent, run twice with the same setup
// and seed, assert the snapshots match.
//
// The M0 "one unit walks the forest band" loop still drives the determinism
// check; everything else (castle, holdings, roles) is just there to exercise
// the new state surface end-to-end.

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
            new UnitSpawn(Id: 2, new TileCoord(0, 0), UnitRole.Lumberjack),
            new UnitSpawn(Id: 3, new TileCoord(0, 0), UnitRole.Hauler, CargoCapacity: 5),
        },
    };
}

static Simulation BuildScenario()
{
    var world = Genesis.Build(MakeSpec());
    var sim = new Simulation(world, seed: 0xC0FFEE);
    sim.SubmitIntent(at: 0, new MoveIntent(unitId: 1, new TileCoord(9, 9)));
    return sim;
}

static void Print(string label, Simulation sim)
{
    Console.WriteLine($"--- {label} ---");
    var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
    Console.WriteLine($"Castle holdings:");
    foreach (var (r, n) in castle.Holdings)
        Console.WriteLine($"  {r}: {n}");
    Console.WriteLine($"Units (id: role, position):");
    foreach (var (id, u) in sim.World.Units)
        Console.WriteLine($"  {id}: {u.Role}, {u.Position.X},{u.Position.Y}");
    Console.WriteLine($"Intents submitted:     {sim.IntentLog.Count}");

    sim.Run();

    Console.WriteLine($"Events resolved:       {sim.ResolvedLog.Count}");
    Console.WriteLine($"Final sim tick:        {sim.Now}");
    var mover = sim.World.Units[1];
    Console.WriteLine($"Unit 1 final position: {mover.Position.X},{mover.Position.Y}");
    Console.WriteLine($"Snapshot hash:         {Snapshot.Hash(sim)}");
    Console.WriteLine();
}

var first = BuildScenario();
Print("Run 1", first);

var second = BuildScenario();
Print("Run 2 (replay with identical intent log)", second);

if (Snapshot.Hash(first) != Snapshot.Hash(second))
{
    Console.Error.WriteLine("DETERMINISM FAILURE: snapshot hashes diverged.");
    Environment.Exit(1);
}

// Also exercise the new Serialize/Restore path on the live final state.
var bytes = Snapshot.Serialize(first);
var restored = Snapshot.Restore(bytes, seed: 0xC0FFEE);
if (Snapshot.Hash(first) != Snapshot.Hash(restored))
{
    Console.Error.WriteLine("ROUND-TRIP FAILURE: restored snapshot does not match original.");
    Environment.Exit(1);
}

Console.WriteLine("OK: identical intent log produced identical final state.");
Console.WriteLine($"OK: serialized snapshot ({bytes.Length} bytes) restored to identical state.");
