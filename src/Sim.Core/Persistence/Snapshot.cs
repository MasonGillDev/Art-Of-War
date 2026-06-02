using System.Security.Cryptography;

namespace Sim.Core.Persistence;

// Canonical serialization of sim state.
//
// Hash(sim)          → SHA-256 over canonical bytes. Equality test for "did
//                      two runs end in the same state?"
// Serialize(sim)     → the bytes themselves. Used to round-trip state in
//                      tests (and, later, by the persistence milestone).
// Restore(bytes,seed)→ rebuilds a Simulation from those bytes.
//
// "Canonical" means: every collection iterated in a deterministic order
// (tiles in y-then-x, units by id, structures by (y,x), holdings by Resource
// enum value). Anything that touches a Dictionary's natural iteration order is
// a bug here.
//
// CORRECTNESS SCOPE (READ THIS):
// This captures *static* world state. It does NOT capture the event queue
// — pending arrivals, build completions, production ticks, haul events.
// That means Restore is correct on FROZEN worlds (no work in flight) and
// silently incorrect on worlds with motion in them, which is essentially
// every live moment of a persistent async RTS. Fix is intent-tail replay
// in the persistence milestone. See docs/persistence-model.md, section
// "The in-flight correctness gap" — that's the load-bearing item this
// type is one half of.
//
// No format-version byte yet — the persistence milestone adds one when restore
// needs to survive across released builds. Until then a format change = all
// in-memory snapshots invalidated, which is fine.
public static class Snapshot
{
    private const uint Magic = 0xA0FA0FA0; // "Art of War"

    public static string Hash(Simulation sim)
    {
        var bytes = Serialize(sim);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static byte[] Serialize(Simulation sim)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Magic);
            WriteClocks(bw, sim);
            WriteGrid(bw, sim.World.Grid);
            WritePlayers(bw, sim.World);
            WriteUnits(bw, sim.World);
            WriteStructures(bw, sim.World);
            WriteRoads(bw, sim.World, sim.Now);
            WriteExplored(bw, sim.World);
        }
        return ms.ToArray();
    }

    public static Simulation Restore(byte[] bytes, ulong seed)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8);

        var magic = br.ReadUInt32();
        if (magic != Magic) throw new InvalidDataException("Snapshot magic mismatch.");

        var (now, rngState, nextSeq) = ReadClocks(br);
        var grid = ReadGrid(br);
        var world = new GameWorld(grid);
        ReadPlayers(br, world);
        ReadUnits(br, world);
        ReadStructures(br, world);
        ReadRoads(br, world);
        ReadExplored(br, world);

        var sim = new Simulation(world, seed);
        sim.Rng.SetState(rngState);
        sim.RestoreClocks(now, nextSeq);
        return sim;
    }

    // ----- clocks --------------------------------------------------------

    private static void WriteClocks(BinaryWriter bw, Simulation sim)
    {
        bw.Write(sim.Now);
        bw.Write(sim.Rng.State);
        bw.Write(sim.NextSeq);
    }

    private static (long now, ulong rng, long nextSeq) ReadClocks(BinaryReader br)
    {
        var now = br.ReadInt64();
        var rng = br.ReadUInt64();
        var nextSeq = br.ReadInt64();
        return (now, rng, nextSeq);
    }

    // ----- grid ----------------------------------------------------------

    private static void WriteGrid(BinaryWriter bw, TileGrid grid)
    {
        bw.Write(grid.Width);
        bw.Write(grid.Height);
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
                bw.Write((byte)grid.BiomeAt(new TileCoord(x, y)));
    }

    private static TileGrid ReadGrid(BinaryReader br)
    {
        var w = br.ReadInt32();
        var h = br.ReadInt32();
        var grid = new TileGrid(w, h, Biome.None);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                grid.SetBiome(new TileCoord(x, y), (Biome)br.ReadByte());
        return grid;
    }

    // ----- players (id-sorted) ------------------------------------------

    private static void WritePlayers(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.Players.Count);
        foreach (var (id, _) in world.Players)  // SortedDictionary → id order
            bw.Write(id);
    }

    private static void ReadPlayers(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id = br.ReadInt32();
            world.Players[id] = new Player(id);
        }
    }

    // ----- units (id-sorted) --------------------------------------------

    private static void WriteUnits(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.Units.Count);
        foreach (var (id, u) in world.Units) // SortedDictionary → id order
        {
            bw.Write(id);
            bw.Write(u.Position.X);
            bw.Write(u.Position.Y);
            bw.Write((byte)u.Role);
            bw.Write(u.CargoCapacity);
            bw.Write((byte)u.Activity);
            if (u.Assignment is TileCoord a)
            {
                bw.Write((byte)1);
                bw.Write(a.X);
                bw.Write(a.Y);
            }
            else
            {
                bw.Write((byte)0);
            }
            bw.Write((byte)u.CargoResource);
            bw.Write(u.CargoAmount);
            bw.Write(u.AssignmentEpoch);
            bw.Write(u.OwnerId);
        }
    }

    private static void ReadUnits(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id = br.ReadInt32();
            var pos = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var role = (UnitRole)br.ReadByte();
            var capacity = br.ReadInt32();
            var activity = (Activity)br.ReadByte();
            TileCoord? assignment = br.ReadByte() == 1
                ? new TileCoord(br.ReadInt32(), br.ReadInt32())
                : null;
            var cargoR = (Resource)br.ReadByte();
            var cargoA = br.ReadInt32();
            var epoch = br.ReadByte();
            var ownerId = br.ReadInt32();

            var u = new Unit(id, pos) { Role = role, CargoCapacity = capacity, OwnerId = ownerId };
            u.CargoResource = cargoR;
            u.CargoAmount = cargoA;
            if (activity != Activity.Idle)
            {
                // Idle is the default; only call TrySet if we actually move off it.
                if (!u.TrySetActivity(activity, assignment))
                    throw new InvalidDataException(
                        $"Restore: illegal activity transition Idle → {activity} for unit {id}.");
            }
            // Restore the epoch AFTER the activity transition above (which would
            // bump it) so the snapshot's epoch wins.
            u.RestoreAssignmentEpoch(epoch);
            world.AddUnit(u);
        }
    }

    // ----- structures (by y,x then kind dispatch) ------------------------

    private static IEnumerable<Structure> CanonicalStructures(GameWorld world) =>
        world.Structures.Values.OrderBy(s => s.At.Y).ThenBy(s => s.At.X);

    private static void WriteStructures(BinaryWriter bw, GameWorld world)
    {
        var list = CanonicalStructures(world).ToList();
        bw.Write(list.Count);
        foreach (var s in list)
        {
            bw.Write(s.At.X);
            bw.Write(s.At.Y);
            bw.Write((byte)s.Kind);
            bw.Write(s.OwnerId);
            switch (s)
            {
                case StorageStructure ss: WriteStorage(bw, ss); break;
                case Extractor e:         WriteExtractor(bw, e); break;
                case ConstructionSite c:  WriteConstruction(bw, c); break;
                case Tower:               /* no fields */ break;
                default:
                    throw new InvalidOperationException($"No serializer for {s.GetType().Name}");
            }
        }
    }

    private static void ReadStructures(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var at = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var kind = (StructureKind)br.ReadByte();
            var ownerId = br.ReadInt32();
            Structure s = kind switch
            {
                StructureKind.Castle           => ReadStorage(br, new Castle(at) { OwnerId = ownerId }),
                StructureKind.Stockpile        => ReadStorage(br, new Stockpile(at) { OwnerId = ownerId }),
                StructureKind.LumberCamp
                  or StructureKind.Quarry
                  or StructureKind.Mine
                  or StructureKind.Farm        => ReadExtractor(br, new Extractor(kind, at) { OwnerId = ownerId }),
                StructureKind.ConstructionSite => ReadConstruction(br, at, ownerId),
                StructureKind.Tower            => new Tower(at) { OwnerId = ownerId },
                _ => throw new InvalidDataException($"Unknown structure kind: {kind}"),
            };
            world.AddStructure(s);
        }
    }

    private static void WriteStorage(BinaryWriter bw, StorageStructure s)
    {
        bw.Write(s.Capacity);
        bw.Write(s.Holdings.Count);
        foreach (var (r, n) in s.Holdings) // SortedDictionary → Resource enum order
        {
            bw.Write((byte)r);
            bw.Write(n);
        }
    }

    private static T ReadStorage<T>(BinaryReader br, T s) where T : StorageStructure
    {
        var capacity = br.ReadInt32();
        if (capacity != s.Capacity)
            throw new InvalidDataException(
                $"{s.Kind} capacity drift: snapshot={capacity}, catalog={s.Capacity}. " +
                "Catalog values must remain stable or the snapshot needs migration.");
        var n = br.ReadInt32();
        for (var i = 0; i < n; i++)
        {
            var r = (Resource)br.ReadByte();
            var amount = br.ReadInt32();
            s.Holdings[r] = amount;
        }
        return s;
    }

    private static void WriteExtractor(BinaryWriter bw, Extractor e)
    {
        bw.Write(e.Workers.Count);
        foreach (var w in e.Workers) bw.Write(w); // SortedSet → ascending
        bw.Write(e.Buffer);
        bw.Write(e.LastProductionTick);
        bw.Write(e.TickArmed);
    }

    private static Extractor ReadExtractor(BinaryReader br, Extractor e)
    {
        var n = br.ReadInt32();
        for (var i = 0; i < n; i++) e.Workers.Add(br.ReadInt32());
        e.Buffer = br.ReadInt32();
        e.LastProductionTick = br.ReadInt64();
        e.TickArmed = br.ReadBoolean();
        return e;
    }

    private static void WriteConstruction(BinaryWriter bw, ConstructionSite c)
    {
        bw.Write((byte)c.TargetKind);
        bw.Write(c.Required.Count);
        foreach (var (r, n) in c.Required) { bw.Write((byte)r); bw.Write(n); }
        bw.Write(c.Delivered.Count);
        foreach (var (r, n) in c.Delivered) { bw.Write((byte)r); bw.Write(n); }
        bw.Write(c.BuildDurationTicks);
        bw.Write(c.RequiredBuilderCount);
        bw.Write(c.ProgressTicks);
        bw.Write(c.BuildPaused);
        WriteNullableLong(bw, c.LastActiveAtTick);
        WriteNullableLong(bw, c.ScheduledCompletion);
    }

    private static ConstructionSite ReadConstruction(BinaryReader br, TileCoord at, int ownerId)
    {
        var targetKind = (StructureKind)br.ReadByte();
        var c = new ConstructionSite(at, targetKind) { OwnerId = ownerId };
        c.Required.Clear();
        var req = br.ReadInt32();
        for (var i = 0; i < req; i++)
        {
            var r = (Resource)br.ReadByte();
            var n = br.ReadInt32();
            c.Required[r] = n;
        }
        var del = br.ReadInt32();
        for (var i = 0; i < del; i++)
        {
            var r = (Resource)br.ReadByte();
            var n = br.ReadInt32();
            c.Delivered[r] = n;
        }
        var buildDur = br.ReadInt32();
        var reqBuilders = br.ReadInt32();
        if (buildDur != c.BuildDurationTicks || reqBuilders != c.RequiredBuilderCount)
            throw new InvalidDataException(
                $"ConstructionSite spec drift for {targetKind}: snapshot " +
                $"(dur={buildDur}, builders={reqBuilders}) vs catalog " +
                $"(dur={c.BuildDurationTicks}, builders={c.RequiredBuilderCount}).");
        c.ProgressTicks = br.ReadInt64();
        c.BuildPaused = br.ReadBoolean();
        c.LastActiveAtTick = ReadNullableLong(br);
        c.ScheduledCompletion = ReadNullableLong(br);
        return c;
    }

    private static void WriteNullableLong(BinaryWriter bw, long? value)
    {
        if (value is long v) { bw.Write((byte)1); bw.Write(v); }
        else { bw.Write((byte)0); }
    }

    private static long? ReadNullableLong(BinaryReader br) =>
        br.ReadByte() == 1 ? br.ReadInt64() : null;

    // ----- explored (per player, tiles in (y, x) order) -----------------

    private static void WriteExplored(BinaryWriter bw, GameWorld world)
    {
        // Player count + per-player (id, tile count + tiles).
        // Sorted by player id for canonical order, then tiles by (y, x).
        var byPlayer = world.Explored
            .OrderBy(kv => kv.Key)
            .ToList();
        bw.Write(byPlayer.Count);
        foreach (var (playerId, tiles) in byPlayer)
        {
            bw.Write(playerId);
            var sortedTiles = tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
            bw.Write(sortedTiles.Count);
            foreach (var t in sortedTiles)
            {
                bw.Write(t.X);
                bw.Write(t.Y);
            }
        }
    }

    private static void ReadExplored(BinaryReader br, GameWorld world)
    {
        var playerCount = br.ReadInt32();
        for (var i = 0; i < playerCount; i++)
        {
            var playerId = br.ReadInt32();
            var tileCount = br.ReadInt32();
            var set = new HashSet<TileCoord>(capacity: tileCount);
            for (var j = 0; j < tileCount; j++)
            {
                var x = br.ReadInt32();
                var y = br.ReadInt32();
                set.Add(new TileCoord(x, y));
            }
            world.Explored[playerId] = set;
        }
    }

    // ----- roads (sparse, by y,x) ---------------------------------------

    private static void WriteRoads(BinaryWriter bw, GameWorld world, long now)
    {
        // Filter by *effective* condition at `now`, not stored Condition.
        // A road tile that's fully decayed but never been re-touched by a
        // traversal still has stored Condition > 0 — pure-read ConditionAt
        // returns 0 for it. Including it in the snapshot would bloat the
        // serialized output with entries that pure-reads treat as absent.
        // Determinism is preserved: ConditionAt is a pure read.
        var list = world.Roads
            .Where(kv => Road.ConditionAt(world, kv.Key, now) > 0)
            .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .ToList();
        bw.Write(list.Count);
        foreach (var kv in list)
        {
            bw.Write(kv.Key.X);
            bw.Write(kv.Key.Y);
            bw.Write(kv.Value.Condition);
            bw.Write(kv.Value.LastDecayTick);
        }
    }

    private static void ReadRoads(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var x = br.ReadInt32();
            var y = br.ReadInt32();
            var condition = br.ReadInt32();
            var lastDecayTick = br.ReadInt64();
            world.Roads[new TileCoord(x, y)] = new RoadState(condition, lastDecayTick);
        }
    }
}
