using Sim.Core.Bandits;

namespace Sim.Core.Progression;

// M37 — the omen rules (docs/progression.md): raise an omen, make it happen at
// its due tick, and resolve it when it is over.
//
// SINGLE MUTATION POINT for GameWorld.Omens / NextOmenId and every Omen field
// (Snapshot restore aside). Called from:
//   * Threat / Arrival / Rumour.Apply   RaiseRaid / RaiseArrival / RaiseRumour
//   * OmenDueEvent                      Arrive (or slip)
//   * DespawnBanditPartyIntent          OnRaiderEscaping (a raider got away)
//   * Population.OnUnitRemoved          OnUnitRemoved (a raider is gone)
//   * CacheLooting                      OnCacheGone (a rumoured ruin emptied)
//
// Deterministic without drawing the world RNG for placement: bearings and
// tiles come from fixed scans with a hash tie-break. (Newcomers roll their
// lifespans like anyone born, inside the sim's own event order.)
public static class Omens
{
    // Newcomers arrive as working adults.
    private const int NewcomerAgeYears = 20;

    // ---- raising ------------------------------------------------------------

    internal static void RaiseRaid(Simulation sim, Player owner, int sourceMilestoneId, int size,
                                   long warningTicks, Resource chestResource, int chestAmount)
    {
        var world = sim.World;
        // No seat, nothing to march on: the threat has no meaning.
        if (Sim.Core.Food.FoodConsumption.FindCastleFor(world, owner.Id) is not { } castle) return;
        var id = world.NextOmenId++;
        Schedule(sim, new Omen
        {
            OmenId = id,
            OwnerId = owner.Id,
            Kind = OmenKind.Raid,
            SourceMilestoneId = sourceMilestoneId,
            Target = castle.At,
            From = PickBearing(world, owner.Id, castle.At, id, wildest: true),
            Size = Math.Clamp(size, 1, BanditConstants.MaxPartySize),
            ChestResource = chestResource,
            ChestAmount = Math.Max(0, chestAmount),
        }, warningTicks);
    }

    internal static void RaiseArrival(Simulation sim, Player owner, int sourceMilestoneId, int size, long warningTicks)
    {
        var world = sim.World;
        if (size <= 0 || Sim.Core.Food.FoodConsumption.FindCastleFor(world, owner.Id) is not { } castle) return;
        var id = world.NextOmenId++;
        Schedule(sim, new Omen
        {
            OmenId = id,
            OwnerId = owner.Id,
            Kind = OmenKind.Refugees,
            SourceMilestoneId = sourceMilestoneId,
            Target = castle.At,
            // Newcomers walk in from the settled side, along the player's own
            // country, not out of the bandit wilds.
            From = PickBearing(world, owner.Id, castle.At, id, wildest: false),
            Size = size,
        }, warningTicks);
    }

    // A rumour needs no countdown: the ruin stands in the fog from the moment
    // it is heard of, and the omen lasts until it is emptied.
    internal static void RaiseRumour(Simulation sim, Player owner, int sourceMilestoneId,
                                     IReadOnlyList<(Resource Resource, int Amount)> loot)
    {
        var world = sim.World;
        if (Sim.Core.Food.FoodConsumption.FindCastleFor(world, owner.Id) is not { } castle) return;
        var id = world.NextOmenId;
        var from = PickBearing(world, owner.Id, castle.At, id, wildest: true);
        world.Explored.TryGetValue(owner.Id, out var explored);
        var at = Search(world, castle.At, from, id, t =>
            (explored is null || !explored.Contains(t))
            && !world.Structures.ContainsKey(t)
            && world.Grid.BiomeAt(t) is not (Biome.Water or Biome.None));
        if (at is not { } ruinAt) return;   // the whole bearing is known ground: no rumour

        world.NextOmenId++;
        var cache = new Cache(ruinAt) { OwnerId = Sim.Core.Caches.CacheConstants.OwnerId };
        foreach (var (r, n) in loot) if (n > 0) cache.Deposit(r, n);
        world.AddStructure(cache);
        var radius = Math.Max(1, world.ProgressionConfig.RumourAreaRadius);
        world.Omens[id] = new Omen
        {
            OmenId = id,
            OwnerId = owner.Id,
            Kind = OmenKind.Rumour,
            SourceMilestoneId = sourceMilestoneId,
            Target = ruinAt,
            From = from,
            AreaCenter = AreaAround(sim, ruinAt, radius),
            AreaRadius = radius,
            State = OmenState.Arrived,
        };
    }

    // The centre of a search circle of `radius` that holds `at`: `at` pushed
    // off by an offset drawn from the disc of radius radius - 1, so the ruin is
    // always inside, a tile clear of the rim, and never at the centre. The
    // centre may fall off the map; the circle is still right.
    //
    // THE SIM'S RNG, NOT A HASH (docs/scouting-secrets.md): the centre goes on
    // the wire, and a public hash of (omen id, tile) could be inverted by a
    // modded client to recover the tile. The RNG state never leaves the server.
    private static TileCoord AreaAround(Simulation sim, TileCoord at, int radius)
    {
        var reach = Math.Max(0, radius - 1);
        var offsets = new List<(int dx, int dy)>();
        for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
                if (dx * dx + dy * dy <= reach * reach && (dx, dy) != (0, 0)) offsets.Add((dx, dy));
        if (offsets.Count == 0) return at;
        var (ox, oy) = offsets[sim.Rng.NextInt(offsets.Count)];
        return new TileCoord(at.X + ox, at.Y + oy);
    }

    // M39 — a bandit camp in the owner's wildest direction, on ground they have
    // never explored, dark and past the bandit distance floor; rumoured with a
    // search circle. No ground for it: no camp and no rumour.
    internal static void RaiseCamp(Simulation sim, Player owner, int sourceMilestoneId)
    {
        var world = sim.World;
        if (Sim.Core.Food.FoodConsumption.FindCastleFor(world, owner.Id) is not { } castle) return;
        var cc = world.CampConfig;
        var id = world.NextOmenId;
        var from = PickBearing(world, owner.Id, castle.At, id, wildest: true);
        world.Explored.TryGetValue(owner.Id, out var explored);
        var at = Search(world, castle.At, from, id, t =>
            (explored is null || !explored.Contains(t))
            && !world.Structures.ContainsKey(t)
            && Sim.Core.Biomes.BiomeDegradation.BiomeAt(world, t, sim.Now, world.BiomeDegradationConfig) is not (Biome.Water or Biome.None)
            && BanditRules.ChebyshevToNearestPlayerPresence(world, t) >= BanditConstants.MinSpawnDistance
            && !BanditRules.IsSeenByAnyPlayer(world, t),
            cc.MinDistance, cc.MaxDistance);
        if (at is not { } campAt) return;

        world.NextOmenId++;
        var camp = Sim.Core.Bandits.Camps.Build(sim, campAt, owner.Id, sourceMilestoneId);
        var radius = Math.Max(1, world.ProgressionConfig.RumourAreaRadius);
        world.Omens[id] = new Omen
        {
            OmenId = id,
            OwnerId = owner.Id,
            Kind = OmenKind.Camp,
            SourceMilestoneId = sourceMilestoneId,
            Target = campAt,
            From = from,
            Size = world.CampConfig.GarrisonStart,
            AreaCenter = AreaAround(sim, campAt, radius),
            AreaRadius = radius,
            // Display only (no event): when the first raid may ride.
            DueTick = camp.LastRaidTick + cc.RaidPeriodTicks,
            State = OmenState.Arrived,
        };
    }

    // M39 — a camp was razed: its rumour is over.
    internal static void OnCampRazed(Simulation sim, TileCoord at)
    {
        foreach (var o in sim.World.Omens.Values)
            if (o.Kind == OmenKind.Camp && o.State == OmenState.Arrived && o.Target == at)
                Resolve(sim, o, OmenState.Fulfilled);
    }

    private static void Schedule(Simulation sim, Omen omen, long warningTicks)
    {
        // Every omen is telegraphed (docs/progression.md, design rule 1): a day
        // is the floor whatever the config says.
        omen.DueTick = sim.Now + Math.Max(Time.Day, warningTicks);
        sim.World.Omens[omen.OmenId] = omen;
        omen.DueSeq = sim.Schedule(omen.DueTick, new OmenDueEvent(omen.OmenId));
    }

    // ---- arriving -----------------------------------------------------------

    internal static void Arrive(Simulation sim, Omen omen)
    {
        var world = sim.World;
        if (!world.Players.TryGetValue(omen.OwnerId, out var owner) || owner.Defeated)
        {
            Resolve(sim, omen, OmenState.Fizzled);
            return;
        }
        switch (omen.Kind)
        {
            case OmenKind.Raid: ArriveRaid(sim, omen); break;
            case OmenKind.Refugees: ArriveRefugees(sim, omen); break;
            default: Resolve(sim, omen, OmenState.Fizzled); break;
        }
    }

    private static void ArriveRaid(Simulation sim, Omen omen)
    {
        var world = sim.World;
        var cfg = world.ProgressionConfig;
        var dark = omen.Slips < cfg.OmenMaxSlips;
        if (FindLanding(world, omen, sim.Now, requireDark: dark) is not { } at)
        {
            if (dark)
            {
                // Every tile in the bearing is lit or crowded: come a little later.
                omen.Slips++;
                omen.DueTick = sim.Now + Math.Max(1, cfg.OmenSlipTicks);
                omen.DueSeq = sim.Schedule(omen.DueTick, new OmenDueEvent(omen.OmenId));
            }
            else
            {
                // Even lit, no walkable ground far enough out in that
                // direction (a peninsula seat). The threat fizzles.
                Resolve(sim, omen, OmenState.Fizzled);
            }
            return;
        }

        omen.PartyIds.AddRange(SpawnBanditPartyIntent.Materialize(sim, at, omen.Size));
        omen.State = OmenState.Arrived;   // the anchor is dead from here: State fences it
    }

    // A gift never fizzles: with nowhere out in the bearing to walk in from,
    // the newcomers are simply at the seat.
    private static void ArriveRefugees(Simulation sim, Omen omen)
    {
        var world = sim.World;
        Resolve(sim, omen, OmenState.Fulfilled);
        var seat = Sim.Core.Food.FoodConsumption.FindCastleFor(world, omen.OwnerId)?.At ?? omen.Target;
        var at = FindLanding(world, omen, sim.Now, requireDark: false) ?? seat;
        var born = sim.Now - NewcomerAgeYears * world.PopulationConfig.TicksPerYear;
        for (var i = 0; i < omen.Size; i++)
        {
            var id = world.NextUnitId++;
            var unit = Sim.Core.Population.Population.OnUnitAdded(sim, new Unit(id, at)
            {
                OwnerId = omen.OwnerId,
                BornTick = born,
            });
            Sim.Core.Population.Population.ScheduleLifespan(sim, unit);
            if (unit.Position != seat)
                MoveIntent.BeginMove(sim, unit, seat);
        }
    }

    // ---- resolving ----------------------------------------------------------

    // A raider is despawning (fleeing off the map). Before its cargo is
    // cleared: it got away, and did it get away with anything?
    internal static void OnRaiderEscaping(GameWorld world, Unit unit)
    {
        if (RaidOf(world, unit.Id) is not { } omen) return;
        omen.Escaped = true;
        if (unit.Cargo.Total > 0) omen.Plundered = true;
    }

    // Every removal path (combat, despawn) converges on Population.OnUnitRemoved.
    // The last raider gone resolves the raid.
    internal static void OnUnitRemoved(Simulation sim, Unit unit)
    {
        if (unit.OwnerId != BanditConstants.OwnerId) return;
        if (RaidOf(sim.World, unit.Id) is not { } omen) return;
        omen.PartyIds.Remove(unit.Id);
        if (omen.PartyIds.Count > 0) return;

        Resolve(sim, omen, omen.Plundered ? OmenState.Lost : OmenState.Repelled);
        // Every raider killed: the war chest drops where the last one fell.
        if (!omen.Escaped && omen.ChestAmount > 0 && omen.ChestResource != Resource.None)
            CargoTransfer.DropToGround(sim.World, unit.Position, omen.ChestResource, omen.ChestAmount);
        var key = omen.Plundered
            ? ProgressKey.Lost(omen.SourceMilestoneId)
            : ProgressKey.Repelled(omen.SourceMilestoneId);
        Progression.Bump(sim, omen.OwnerId, key);
    }

    // A cache was emptied: a rumour about it is over.
    internal static void OnCacheGone(GameWorld world, TileCoord at, long now)
    {
        foreach (var o in world.Omens.Values)
            if (o.Kind == OmenKind.Rumour && o.State == OmenState.Arrived && o.Target == at)
            {
                o.State = OmenState.Fulfilled;
                o.ResolvedTick = now;
            }
    }

    private static void Resolve(Simulation sim, Omen omen, OmenState outcome)
    {
        omen.State = outcome;
        omen.ResolvedTick = sim.Now;
    }

    public static Omen? RaidOf(GameWorld world, int unitId)
    {
        foreach (var o in world.Omens.Values)
            if (o.Kind == OmenKind.Raid && o.State == OmenState.Arrived && o.PartyIds.Contains(unitId)) return o;
        return null;
    }

    // ---- where from ---------------------------------------------------------

    // Counts, per octant around the seat inside the arrival ring, the walkable
    // ground the owner has never explored. The wildest octant is where raiders
    // and ruins are; the tamest is where newcomers walk in from. Ties break on
    // a hash of the omen, so two omens need not share a road.
    internal static Bearing PickBearing(GameWorld world, int ownerId, TileCoord center, int omenId, bool wildest)
    {
        var cfg = world.ProgressionConfig;
        world.Explored.TryGetValue(ownerId, out var explored);
        var wild = new int[8];
        var land = new int[8];
        var grid = world.Grid;
        var r = cfg.OmenSpawnMaxDistance;
        for (var y = Math.Max(0, center.Y - r); y <= Math.Min(grid.Height - 1, center.Y + r); y++)
            for (var x = Math.Max(0, center.X - r); x <= Math.Min(grid.Width - 1, center.X + r); x++)
            {
                var dx = x - center.X;
                var dy = y - center.Y;
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < cfg.OmenSpawnMinDistance) continue;
                var t = new TileCoord(x, y);
                if (grid.BiomeAt(t) is Biome.Water or Biome.None) continue;
                var s = (int)SectorOf(dx, dy);
                land[s]++;
                if (explored is null || !explored.Contains(t)) wild[s]++;
            }

        var best = -1;
        long bestScore = long.MinValue;
        var bestTie = uint.MaxValue;
        for (var b = 0; b < 8; b++)
        {
            // Tamest = most known ground; an octant with no land at all can
            // never be where anyone walks in from.
            long score = wildest ? wild[b] : (land[b] == 0 ? long.MinValue + 1 : land[b] - wild[b]);
            var tie = Mix(omenId, b, 0);
            if (score > bestScore || (score == bestScore && tie < bestTie))
            {
                best = b;
                bestScore = score;
                bestTie = tie;
            }
        }
        return (Bearing)best;
    }

    // Where a raid or a band of newcomers lands: walkable, past the bandit
    // distance floor from anyone, and (for raiders, until waived) dark.
    private static TileCoord? FindLanding(GameWorld world, Omen omen, long now, bool requireDark) =>
        Search(world, omen.Target, omen.From, omen.OmenId, t =>
        {
            var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(world, t, now, world.BiomeDegradationConfig);
            if (biome is Biome.Water or Biome.None) return false;
            if (BanditRules.ChebyshevToNearestPlayerPresence(world, t) < BanditConstants.MinSpawnDistance) return false;
            return !requireDark || !BanditRules.IsSeenByAnyPlayer(world, t);
        });

    // Nearest ring first (between OmenSpawnMinDistance and
    // OmenSpawnMaxDistance of `center`), inside the bearing's octant; within a
    // ring, a hash order. The first tile that passes wins.
    private static TileCoord? Search(GameWorld world, TileCoord center, Bearing from, int omenId, Func<TileCoord, bool> ok) =>
        Search(world, center, from, omenId, ok, world.ProgressionConfig.OmenSpawnMinDistance, world.ProgressionConfig.OmenSpawnMaxDistance);

    internal static TileCoord? Search(GameWorld world, TileCoord center, Bearing from, int omenId, Func<TileCoord, bool> ok,
                                      int minDistance, int maxDistance)
    {
        var grid = world.Grid;
        var ring = new List<TileCoord>();
        for (var d = Math.Max(1, minDistance); d <= maxDistance; d++)
        {
            ring.Clear();
            for (var dy = -d; dy <= d; dy++)
                for (var dx = -d; dx <= d; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d) continue;
                    if (SectorOf(dx, dy) != from) continue;
                    var t = new TileCoord(center.X + dx, center.Y + dy);
                    if (t.X < 0 || t.Y < 0 || t.X >= grid.Width || t.Y >= grid.Height) continue;
                    ring.Add(t);
                }
            ring.Sort((a, b) => Mix(omenId, a.X, a.Y).CompareTo(Mix(omenId, b.X, b.Y)));
            foreach (var t in ring)
                if (ok(t)) return t;
        }
        return null;
    }

    // Octant of an offset, integer-only (12/29 ≈ tan 22.5°).
    public static Bearing SectorOf(int dx, int dy)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        if (ay * 29 <= ax * 12) return dx >= 0 ? Bearing.East : Bearing.West;
        if (ax * 29 <= ay * 12) return dy > 0 ? Bearing.North : Bearing.South;
        return dx > 0
            ? (dy > 0 ? Bearing.NorthEast : Bearing.SouthEast)
            : (dy > 0 ? Bearing.NorthWest : Bearing.SouthWest);
    }

    private static uint Mix(int a, int b, int c)
    {
        unchecked
        {
            var h = 2166136261u;
            h = (h ^ (uint)a) * 16777619u;
            h = (h ^ (uint)b) * 16777619u;
            h = (h ^ (uint)c) * 16777619u;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return h;
        }
    }
}
