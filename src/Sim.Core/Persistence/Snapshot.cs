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

    // Format version byte. Bumped whenever the serialized layout changes
    // incompatibly. Restore refuses mismatched versions; the operator path
    // is snapshot-on-deploy under the producing code's version, then deploy
    // and restore under the new code (see docs/persistence-model.md).
    //
    //   1 — M4 in-flight anchors (Unit path + haul + structure Seqs).
    //   2 — M5 groups (Unit.GroupId + GameWorld.Groups).
    //   3 — M6 diplomacy (DiplomacyConfig + Relationships + Proposals).
    //   4 — M7 combat (Unit.Health + Buffs; GameWorld.CombatStates +
    //       GroundResources + CombatConfig).
    //   5 — M8 population (Unit.BornTick + DeathTick + DeathSeq;
    //       GameWorld.PopulationConfig + NextUnitId).
    //   6 — M9 biome degradation (GameWorld.BiomeDegradationConfig +
    //       sparse Fertility dict — pure derived state, no new scheduled events).
    //   7 — M13 food consumption (Player.PopulationCount). Phases B–E
    //       extend this further (Castle anchors, famine state, event
    //       anchors); the version stays 7 across the milestone since
    //       no shipped snapshot ever sees the intermediate phases.
    public const int FormatVersion = 7;

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
            bw.Write(FormatVersion);
            WriteClocks(bw, sim);
            WriteGrid(bw, sim.World.Grid);
            WritePlayers(bw, sim.World);
            WriteUnits(bw, sim.World);
            WriteStructures(bw, sim.World);
            WriteRoads(bw, sim.World, sim.Now);
            WriteExplored(bw, sim.World);
            WriteGroups(bw, sim.World);
            WriteDiplomacy(bw, sim.World);
            WriteCombat(bw, sim.World);
            WriteGroundResources(bw, sim.World);
            WritePopulation(bw, sim.World);
            WriteBiomeDegradation(bw, sim.World);
            WriteRememberedBiome(bw, sim.World);
        }
        return ms.ToArray();
    }

    public static Simulation Restore(byte[] bytes, ulong seed)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8);

        var magic = br.ReadUInt32();
        if (magic != Magic) throw new InvalidDataException("Snapshot magic mismatch.");
        var version = br.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException(
                $"Snapshot format version {version} not supported by this build " +
                $"(current = {FormatVersion}). Run snapshot-on-deploy under the " +
                $"producing code version; see docs/persistence-model.md.");

        var (now, rngState, nextSeq) = ReadClocks(br);
        var grid = ReadGrid(br);
        // World is constructed with a placeholder DiplomacyConfig; the real
        // config (from the snapshot) is restored in ReadDiplomacy below.
        var world = new GameWorld(grid);
        ReadPlayers(br, world);
        ReadUnits(br, world);
        ReadStructures(br, world);
        ReadRoads(br, world);
        ReadExplored(br, world);
        ReadGroups(br, world);
        ReadDiplomacy(br, world);
        ReadCombat(br, world);
        ReadGroundResources(br, world);
        ReadPopulation(br, world);
        ReadBiomeDegradation(br, world);
        ReadRememberedBiome(br, world);

        var sim = new Simulation(world, seed);
        sim.Rng.SetState(rngState);
        sim.RestoreClocks(now, nextSeq);
        // M4 Phase B: reconstruct the in-flight event queue from per-entity
        // next-event anchors. See Persistence/RegenerateQueue.cs.
        RegenerateQueue.From(sim);
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
        // M13: Player.PopulationCount is NOT serialised — it's
        // re-derived as Snapshot.ReadUnits calls world.AddUnit for each
        // restored unit, which increments the owner's count.
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
            // M4: in-flight movement anchor.
            WritePathRemaining(bw, u.PathRemaining);
            WriteNullableTileCoord(bw, u.PathFinalDest);
            WriteNullableLong(bw, u.NextArrivalTick);
            WriteNullableLong(bw, u.NextArrivalSeq);
            // M4: in-flight haul anchor.
            WriteHaulPlan(bw, u.HaulPlan);
            // M5: group membership tag.
            WriteNullableInt(bw, u.GroupId);
            // M7: combat state.
            bw.Write(u.Health);
            WriteBuffs(bw, u.Buffs);
            // M8: population state.
            bw.Write(u.BornTick);
            WriteNullableLong(bw, u.DeathTick);
            WriteNullableLong(bw, u.DeathSeq);
        }
    }

    private static void WriteBuffs(BinaryWriter bw, IReadOnlyList<Sim.Core.Combat.Buff> buffs)
    {
        bw.Write(buffs.Count);
        foreach (var b in buffs)
        {
            bw.Write(b.Kind);
            bw.Write(b.PowerModifier);
            bw.Write(b.HealthModifier);
            WriteNullableLong(bw, b.ExpiresAt);
        }
    }

    private static List<Sim.Core.Combat.Buff> ReadBuffs(BinaryReader br)
    {
        var n = br.ReadInt32();
        var list = new List<Sim.Core.Combat.Buff>(capacity: n);
        for (var i = 0; i < n; i++)
        {
            var kind = br.ReadString();
            var pm = br.ReadInt32();
            var hm = br.ReadInt32();
            var exp = ReadNullableLong(br);
            list.Add(new Sim.Core.Combat.Buff(kind, pm, hm, exp));
        }
        return list;
    }

    private static void WriteNullableInt(BinaryWriter bw, int? value)
    {
        if (value is int v) { bw.Write((byte)1); bw.Write(v); }
        else { bw.Write((byte)0); }
    }

    private static int? ReadNullableInt(BinaryReader br) =>
        br.ReadByte() == 1 ? br.ReadInt32() : null;

    private static void WritePathRemaining(BinaryWriter bw, List<TileCoord>? path)
    {
        if (path is null) { bw.Write(-1); return; }
        bw.Write(path.Count);
        foreach (var t in path) { bw.Write(t.X); bw.Write(t.Y); }
    }

    private static List<TileCoord>? ReadPathRemaining(BinaryReader br)
    {
        var n = br.ReadInt32();
        if (n < 0) return null;
        var list = new List<TileCoord>(capacity: n);
        for (var i = 0; i < n; i++) list.Add(new TileCoord(br.ReadInt32(), br.ReadInt32()));
        return list;
    }

    private static void WriteNullableTileCoord(BinaryWriter bw, TileCoord? coord)
    {
        if (coord is { } c) { bw.Write((byte)1); bw.Write(c.X); bw.Write(c.Y); }
        else { bw.Write((byte)0); }
    }

    private static TileCoord? ReadNullableTileCoord(BinaryReader br) =>
        br.ReadByte() == 1 ? new TileCoord(br.ReadInt32(), br.ReadInt32()) : null;

    private static void WriteHaulPlan(BinaryWriter bw, HaulPlan? plan)
    {
        if (plan is null) { bw.Write((byte)0); return; }
        bw.Write((byte)1);
        bw.Write(plan.SourceTile.X); bw.Write(plan.SourceTile.Y);
        bw.Write(plan.DestTile.X);   bw.Write(plan.DestTile.Y);
        bw.Write((byte)plan.Resource);
        bw.Write((byte)plan.Phase);
    }

    private static HaulPlan? ReadHaulPlan(BinaryReader br)
    {
        if (br.ReadByte() == 0) return null;
        var src   = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var dest  = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var res   = (Resource)br.ReadByte();
        var phase = (HaulPhase)br.ReadByte();
        return new HaulPlan(src, dest, res, phase);
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
            var pathRem = ReadPathRemaining(br);
            var pathDest = ReadNullableTileCoord(br);
            var nextArrAt = ReadNullableLong(br);
            var nextArrSeq = ReadNullableLong(br);
            var haulPlan = ReadHaulPlan(br);
            var groupId = ReadNullableInt(br);
            var health = br.ReadInt32();
            var buffs = ReadBuffs(br);
            var bornTick = br.ReadInt64();
            var deathTick = ReadNullableLong(br);
            var deathSeq = ReadNullableLong(br);

            var u = new Unit(id, pos) { Role = role, CargoCapacity = capacity, OwnerId = ownerId, BornTick = bornTick };
            u.CargoResource = cargoR;
            u.CargoAmount = cargoA;

            u.PathRemaining = pathRem;
            u.PathFinalDest = pathDest;
            u.NextArrivalTick = nextArrAt;
            u.NextArrivalSeq  = nextArrSeq;
            u.HaulPlan = haulPlan;
            u.GroupId  = groupId;
            u.Health   = health;
            foreach (var b in buffs) u.Buffs.Add(b);
            u.DeathTick = deathTick;
            u.DeathSeq  = deathSeq;
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
                // House must come before StorageStructure: it IS a
                // StorageStructure but carries extra breeding state.
                case House h:             WriteStorage(bw, h); WriteHouseOccupation(bw, h); break;
                // M13 — Castle has the food-consumption anchor. Castle
                // must come before StorageStructure for the same reason.
                case Castle castle:       WriteStorage(bw, castle); WriteCastleAnchors(bw, castle); break;
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
                StructureKind.Castle           => ReadCastleWithAnchors(br, at, ownerId),
                StructureKind.Stockpile        => ReadStorage(br, new Stockpile(at) { OwnerId = ownerId }),
                StructureKind.LumberCamp
                  or StructureKind.Quarry
                  or StructureKind.Mine
                  or StructureKind.Farm        => ReadExtractor(br, new Extractor(kind, at) { OwnerId = ownerId }),
                StructureKind.ConstructionSite => ReadConstruction(br, at, ownerId),
                StructureKind.Tower            => new Tower(at) { OwnerId = ownerId },
                StructureKind.House            => ReadHouseWithOccupation(br, at, ownerId),
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
        // M4: queued ProductionTickEvent anchor.
        WriteNullableLong(bw, e.NextProductionTickSeq);
    }

    private static Extractor ReadExtractor(BinaryReader br, Extractor e)
    {
        var n = br.ReadInt32();
        for (var i = 0; i < n; i++) e.Workers.Add(br.ReadInt32());
        e.Buffer = br.ReadInt32();
        e.LastProductionTick = br.ReadInt64();
        e.TickArmed = br.ReadBoolean();
        e.NextProductionTickSeq = ReadNullableLong(br);
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
        // M4: queued BuildCompleteEvent anchor.
        WriteNullableLong(bw, c.BuildCompleteSeq);
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
        c.BuildCompleteSeq = ReadNullableLong(br);
        return c;
    }

    // ----- house (M8) ----------------------------------------------------

    // M13 — castle food consumption anchor + (Phase C–D) famine /
    // starvation event anchors. Written after ReadStorage's payload.
    private static void WriteCastleAnchors(BinaryWriter bw, Castle c)
    {
        bw.Write(c.LastFoodConsumedTick);
        WriteNullableLong(bw, c.FamineStartTick);
        WriteNullableLong(bw, c.NextFamineCheckTick);
        WriteNullableLong(bw, c.NextFamineCheckSeq);
        WriteNullableLong(bw, c.NextStarvationDeathTick);
        WriteNullableLong(bw, c.NextStarvationDeathSeq);
    }

    private static Castle ReadCastleWithAnchors(BinaryReader br, TileCoord at, int ownerId)
    {
        var c = new Castle(at) { OwnerId = ownerId };
        ReadStorage(br, c);
        c.LastFoodConsumedTick = br.ReadInt64();
        c.FamineStartTick = ReadNullableLong(br);
        c.NextFamineCheckTick = ReadNullableLong(br);
        c.NextFamineCheckSeq = ReadNullableLong(br);
        c.NextStarvationDeathTick = ReadNullableLong(br);
        c.NextStarvationDeathSeq = ReadNullableLong(br);
        return c;
    }

    private static void WriteHouseOccupation(BinaryWriter bw, House h)
    {
        if (h.Occupation is null) { bw.Write((byte)0); return; }
        bw.Write((byte)1);
        bw.Write(h.Occupation.ParentAId);
        bw.Write(h.Occupation.ParentBId);
        bw.Write(h.Occupation.BirthTick);
        bw.Write(h.Occupation.BirthSeq);
    }

    private static House ReadHouseWithOccupation(BinaryReader br, TileCoord at, int ownerId)
    {
        var h = new House(at) { OwnerId = ownerId };
        ReadStorage(br, h);
        if (br.ReadByte() == 0) return h;
        h.Occupation = new BreedingOccupation
        {
            ParentAId = br.ReadInt32(),
            ParentBId = br.ReadInt32(),
            BirthTick = br.ReadInt64(),
            BirthSeq  = br.ReadInt64(),
        };
        return h;
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

    // ----- diplomacy (M6) -----------------------------------------------

    private static void WriteDiplomacy(BinaryWriter bw, GameWorld world)
    {
        var d = world.Diplomacy;
        // Config first so it can be restored verbatim.
        bw.Write(d.Config.Delay);
        bw.Write(d.Config.ProposalExpiryTicks);

        // Relationships in canonical pair-key order.
        bw.Write(d.Relationships.Count);
        foreach (var (pair, rel) in d.Relationships)
        {
            bw.Write(pair.Lo);
            bw.Write(pair.Hi);
            bw.Write((byte)rel.State);
            WriteNullableLong(bw, rel.PendingEffectiveTick);
            WriteNullableLong(bw, rel.PendingSeq);
        }

        // Proposals in id order.
        bw.Write(d.Proposals.Count);
        foreach (var (_, p) in d.Proposals)
        {
            bw.Write(p.Id);
            bw.Write(p.ProposerId);
            bw.Write(p.TargetId);
            bw.Write((byte)p.DesiredState);
            bw.Write(p.ExpiryTick);
        }
        bw.Write(d.NextProposalId);
    }

    private static void ReadDiplomacy(BinaryReader br, GameWorld world)
    {
        var delay = br.ReadInt64();
        var expiry = br.ReadInt64();
        world.Diplomacy.RestoreConfig(new Sim.Core.Diplomacy.DiplomacyConfig(delay, expiry));

        var relCount = br.ReadInt32();
        for (var i = 0; i < relCount; i++)
        {
            var lo = br.ReadInt32();
            var hi = br.ReadInt32();
            var pair = new Sim.Core.Diplomacy.FactionPair(lo, hi);
            var state = (Sim.Core.Diplomacy.RelationshipState)br.ReadByte();
            var pendingTick = ReadNullableLong(br);
            var pendingSeq  = ReadNullableLong(br);
            var rel = world.Diplomacy.GetOrCreate(pair);
            rel.State = state;
            rel.PendingEffectiveTick = pendingTick;
            rel.PendingSeq = pendingSeq;
        }

        var propCount = br.ReadInt32();
        for (var i = 0; i < propCount; i++)
        {
            var id = br.ReadInt32();
            var proposer = br.ReadInt32();
            var target = br.ReadInt32();
            var desired = (Sim.Core.Diplomacy.RelationshipState)br.ReadByte();
            var expiryTick = br.ReadInt64();
            world.Diplomacy.AddProposal(new Sim.Core.Diplomacy.Proposal
            {
                Id = id,
                ProposerId = proposer,
                TargetId = target,
                DesiredState = desired,
                ExpiryTick = expiryTick,
            });
        }
        world.Diplomacy.RestoreNextProposalId(br.ReadInt32());
    }

    // ----- combat (M7) --------------------------------------------------

    private static void WriteCombat(BinaryWriter bw, GameWorld world)
    {
        // Config first.
        bw.Write(world.CombatConfig.RoundIntervalTicks);

        // CombatStates in canonical (y, x) order — same shape as Roads.
        var states = world.CombatStates
            .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .ToList();
        bw.Write(states.Count);
        foreach (var kv in states)
        {
            bw.Write(kv.Key.X);
            bw.Write(kv.Key.Y);
            bw.Write(kv.Value.NextRoundTick);
            bw.Write(kv.Value.NextRoundSeq);
            bw.Write(kv.Value.RoundNumber);
        }
    }

    private static void ReadCombat(BinaryReader br, GameWorld world)
    {
        var roundInterval = br.ReadInt64();
        world.RestoreCombatConfig(new Sim.Core.Combat.CombatConfig(roundInterval));

        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var x = br.ReadInt32();
            var y = br.ReadInt32();
            var tile = new TileCoord(x, y);
            var tick = br.ReadInt64();
            var seq = br.ReadInt64();
            var round = br.ReadByte();
            var state = new Sim.Core.Combat.CombatState(tile)
            {
                NextRoundTick = tick,
                NextRoundSeq = seq,
                RoundNumber = round,
            };
            world.CombatStates[tile] = state;
        }
    }

    // ----- ground resources (M7 capture economy) -----------------------

    private static void WriteGroundResources(BinaryWriter bw, GameWorld world)
    {
        var tiles = world.GroundResources
            .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .ToList();
        bw.Write(tiles.Count);
        foreach (var (tile, pile) in tiles)
        {
            bw.Write(tile.X);
            bw.Write(tile.Y);
            bw.Write(pile.Count);
            foreach (var (r, n) in pile) // SortedDictionary → Resource enum order
            {
                bw.Write((byte)r);
                bw.Write(n);
            }
        }
    }

    private static void ReadGroundResources(BinaryReader br, GameWorld world)
    {
        var tileCount = br.ReadInt32();
        for (var i = 0; i < tileCount; i++)
        {
            var x = br.ReadInt32();
            var y = br.ReadInt32();
            var tile = new TileCoord(x, y);
            var pileCount = br.ReadInt32();
            var pile = new SortedDictionary<Resource, int>();
            for (var j = 0; j < pileCount; j++)
            {
                var r = (Resource)br.ReadByte();
                var n = br.ReadInt32();
                pile[r] = n;
            }
            world.GroundResources[tile] = pile;
        }
    }

    // ----- population (M8) ----------------------------------------------

    private static void WritePopulation(BinaryWriter bw, GameWorld world)
    {
        var c = world.PopulationConfig;
        bw.Write(c.TicksPerYear);
        bw.Write(c.MinTrainAge);
        bw.Write(c.MinFertileAge);
        bw.Write(c.MaxFertileAge);
        bw.Write(c.GestationTicks);
        bw.Write(c.BirthFoodCost);
        bw.Write(c.LifespanMinYears);
        bw.Write(c.LifespanMaxYears);
        bw.Write(world.NextUnitId);
    }

    private static void ReadPopulation(BinaryReader br, GameWorld world)
    {
        var ticksPerYear = br.ReadInt64();
        var minTrain = br.ReadInt32();
        var minFert = br.ReadInt32();
        var maxFert = br.ReadInt32();
        var gestation = br.ReadInt64();
        var birthFood = br.ReadInt32();
        var lifeMin = br.ReadInt32();
        var lifeMax = br.ReadInt32();
        var nextUnitId = br.ReadInt32();
        world.RestorePopulationConfig(new Sim.Core.Population.PopulationConfig(
            ticksPerYear, minTrain, minFert, maxFert, gestation, birthFood, lifeMin, lifeMax));
        world.NextUnitId = nextUnitId;
    }

    // ----- biome degradation (M9) ---------------------------------------

    private static void WriteBiomeDegradation(BinaryWriter bw, GameWorld world)
    {
        // Config first — twelve fields, fixed order matches the record-struct
        // positional layout.
        var c = world.BiomeDegradationConfig;
        bw.Write(c.ForestBaseline);
        bw.Write(c.GrasslandBaseline);
        bw.Write(c.DesertBaseline);
        bw.Write(c.HillsBaseline);
        bw.Write(c.MountainBaseline);
        bw.Write(c.WaterBaseline);
        bw.Write(c.ForestThreshold);
        bw.Write(c.DesertThreshold);
        bw.Write(c.RecoveryAmount);
        bw.Write(c.RecoveryPeriod);
        bw.Write(c.DegradePeriod);
        bw.Write(c.DegradeRadius);

        // Sparse fertility dict in canonical (y, x) order. Entries with
        // Deviation == 0 should not exist (CatchUp removes them); guard
        // anyway so a hand-constructed test world doesn't break canonical
        // hashing.
        var list = world.Fertility
            .Where(kv => kv.Value.Deviation != 0)
            .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .ToList();
        bw.Write(list.Count);
        foreach (var kv in list)
        {
            bw.Write(kv.Key.X);
            bw.Write(kv.Key.Y);
            bw.Write(kv.Value.Deviation);
            bw.Write(kv.Value.LastUpdateTick);
        }
    }

    // ----- remembered biome (M9, per-player per-tile) -------------------

    private static void WriteRememberedBiome(BinaryWriter bw, GameWorld world)
    {
        // Canonical: sort by player id; per player, tiles in (y, x) order.
        var byPlayer = world.RememberedBiome
            .OrderBy(kv => kv.Key)
            .ToList();
        bw.Write(byPlayer.Count);
        foreach (var (playerId, perTile) in byPlayer)
        {
            bw.Write(playerId);
            var tiles = perTile
                .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
                .ToList();
            bw.Write(tiles.Count);
            foreach (var (tile, biome) in tiles)
            {
                bw.Write(tile.X);
                bw.Write(tile.Y);
                bw.Write((byte)biome);
            }
        }
    }

    private static void ReadRememberedBiome(BinaryReader br, GameWorld world)
    {
        var playerCount = br.ReadInt32();
        for (var i = 0; i < playerCount; i++)
        {
            var playerId = br.ReadInt32();
            var tileCount = br.ReadInt32();
            var perTile = new Dictionary<TileCoord, Biome>(capacity: tileCount);
            for (var j = 0; j < tileCount; j++)
            {
                var x = br.ReadInt32();
                var y = br.ReadInt32();
                var biome = (Biome)br.ReadByte();
                perTile[new TileCoord(x, y)] = biome;
            }
            world.RememberedBiome[playerId] = perTile;
        }
    }

    private static void ReadBiomeDegradation(BinaryReader br, GameWorld world)
    {
        var forestBase = br.ReadInt32();
        var grassBase = br.ReadInt32();
        var desertBase = br.ReadInt32();
        var hillsBase = br.ReadInt32();
        var mountainBase = br.ReadInt32();
        var waterBase = br.ReadInt32();
        var forestThresh = br.ReadInt32();
        var desertThresh = br.ReadInt32();
        var recoveryAmount = br.ReadInt32();
        var recoveryPeriod = br.ReadInt64();
        var degradePeriod = br.ReadInt64();
        var degradeRadius = br.ReadInt32();
        world.RestoreBiomeDegradationConfig(new Sim.Core.Biomes.BiomeDegradationConfig(
            forestBase, grassBase, desertBase, hillsBase, mountainBase, waterBase,
            forestThresh, desertThresh, recoveryAmount, recoveryPeriod, degradePeriod, degradeRadius));

        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var x = br.ReadInt32();
            var y = br.ReadInt32();
            var dev = br.ReadInt32();
            var lastUpdate = br.ReadInt64();
            world.Fertility[new TileCoord(x, y)] = new Sim.Core.Biomes.Fertility(dev, lastUpdate);
        }
    }

    // ----- groups (id-sorted) -------------------------------------------

    private static void WriteGroups(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.Groups.Count);
        foreach (var (id, g) in world.Groups)
        {
            bw.Write(id);
            bw.Write(g.OwnerId);
            bw.Write((byte)g.State);
            bw.Write(g.Position.X);
            bw.Write(g.Position.Y);
            WriteNullableTileCoord(bw, g.RendezvousTile);
            bw.Write(g.PendingArrivals);

            // Members in ascending order (SortedSet → sorted iteration).
            bw.Write(g.Members.Count);
            foreach (var memberId in g.Members) bw.Write(memberId);

            // M4-style in-flight anchor.
            WritePathRemaining(bw, g.PathRemaining);
            WriteNullableTileCoord(bw, g.PathFinalDest);
            WriteNullableLong(bw, g.NextArrivalTick);
            WriteNullableLong(bw, g.NextArrivalSeq);
            bw.Write(g.MovementEpoch);
        }
    }

    private static void ReadGroups(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id      = br.ReadInt32();
            var ownerId = br.ReadInt32();
            var state   = (GroupState)br.ReadByte();
            var pos     = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var rendez  = ReadNullableTileCoord(br);
            var pending = br.ReadInt32();

            var memCount = br.ReadInt32();
            var memberIds = new int[memCount];
            for (var m = 0; m < memCount; m++) memberIds[m] = br.ReadInt32();

            var pathRem  = ReadPathRemaining(br);
            var pathDest = ReadNullableTileCoord(br);
            var arrTick  = ReadNullableLong(br);
            var arrSeq   = ReadNullableLong(br);
            var epoch    = br.ReadByte();

            var g = new Group(id) { OwnerId = ownerId };
            foreach (var m in memberIds) g.Members.Add(m);
            g.Position = pos;
            g.State = state;
            g.RendezvousTile = rendez;
            g.PendingArrivals = pending;
            g.PathRemaining = pathRem;
            g.PathFinalDest = pathDest;
            g.NextArrivalTick = arrTick;
            g.NextArrivalSeq  = arrSeq;
            g.RestoreMovementEpoch(epoch);

            world.Groups[id] = g;
        }
    }
}
