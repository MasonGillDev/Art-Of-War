using Sim.Core.Bandits;
using Sim.Core.Progression;

namespace Sim.Core.Landing;

// The landing (docs/two-act-pacing.md): when it is, the prelude's truce, and the
// war bands it raises.
//
// PURE READS: HasLanded, TruceHolds, IsHostBound — nothing written, callable by
// intents, views, brains and the host at any time.
//
// SINGLE MUTATION POINT for GameWorld.LandingHosts and GameWorld.LandingSeq
// (Snapshot restore aside): ScheduleAtGenesis sets the anchor, LandingEvent clears
// it and calls Land, and OnUnitRemoved keeps each band's roster to the living.
//
// In a one-act world (LandingConfig.Tick == 0) there is no prelude and no
// landing: HasLanded and TruceHolds are both false, no event is scheduled, and no
// Seq is drawn.
public static class LandingRules
{
    // Has the landing happened by `now`? The landing tick itself counts as
    // landed: the second act begins ON day X.
    public static bool HasLanded(GameWorld world, long now) =>
        world.LandingConfig.Enabled && now >= world.LandingConfig.Tick;

    // The prelude's peace: until the landing, no kingdom may declare war on
    // another. Everyone spends the prelude learning and preparing for the same
    // battle, so nobody can be rushed out of the game before it starts.
    public static bool TruceHolds(GameWorld world, long now) =>
        world.LandingConfig.Enabled && now < world.LandingConfig.Tick;

    // Does a landing band still count this unit as one of its own?
    public static bool IsHostBound(GameWorld world, int unitId)
    {
        foreach (var host in world.LandingHosts.Values)
            foreach (var front in host.Fronts)
                if (front.UnitIds.Contains(unitId)) return true;
        return false;
    }

    // Called once, from the Simulation spec-ctor after the genesis scatters.
    internal static void ScheduleAtGenesis(Simulation sim)
    {
        var cfg = sim.World.LandingConfig;
        if (!cfg.Enabled) return;
        sim.World.LandingSeq = sim.Schedule(cfg.Tick, new LandingEvent());
    }

    // ---- day X ----------------------------------------------------------------

    // The four sides a band can come from: a unit step, and the octant its
    // landing is searched in. y grows north (Omens.SectorOf's convention).
    private static readonly (int Dx, int Dy, Bearing Bearing)[] Compass =
    {
        (0, 1, Bearing.North),
        (1, 0, Bearing.East),
        (0, -1, Bearing.South),
        (-1, 0, Bearing.West),
    };

    // One host for every living kingdom with a castle, in owner-id order. The
    // same host for everyone: the landing is a shared test, not a handicap.
    internal static void Land(Simulation sim)
    {
        var world = sim.World;
        foreach (var (ownerId, player) in world.Players)   // SortedDictionary: id order
        {
            if (ownerId < 0 || player.Defeated) continue;
            if (Sim.Core.Food.FoodConsumption.FindCastleFor(world, ownerId) is not { } castle) continue;
            var host = RaiseHost(sim, ownerId, castle.At);
            if (host.Fronts.Count > 0) world.LandingHosts[ownerId] = host;
        }
    }

    private static LandingHost RaiseHost(Simulation sim, int ownerId, TileCoord seat)
    {
        var world = sim.World;
        var cfg = world.LandingConfig;
        var host = new LandingHost
        {
            TargetOwnerId = ownerId,
            Seat = seat,
            AssaultTick = sim.Now + Math.Max(0, cfg.AssaultDelayTicks),
        };

        // Which sides: the compass points whose approach is open ground, tried in
        // an order no player can predict but every replay repeats (a hash of the
        // kingdom and the landing, not an Rng draw — the landing must not shift
        // any later roll). A side with no fog far enough out (the map's edge, the
        // sea) is skipped for the next, so a kingdom gets its full count of bands
        // whenever it has that many open sides.
        var sides = Enumerable.Range(0, Compass.Length)
            .Where(i => OpenGround(world, sim.Now, Step(seat, Compass[i], 1)))
            .OrderBy(i => Mix(ownerId, i, (int)(sim.Now & int.MaxValue)))
            .ThenBy(i => i)
            .ToList();
        var wanted = Math.Clamp(cfg.Fronts, 0, Compass.Length);

        var size = Math.Clamp(cfg.BandSize, 1, BanditConstants.MaxPartySize);
        foreach (var i in sides)
        {
            if (host.Fronts.Count >= wanted) break;
            var side = Compass[i];
            if (FindLanding(world, sim.Now, seat, side.Bearing, ownerId * Compass.Length + i) is not { } at)
                continue;   // no fog on that side far enough out: try the next side
            var approach = Step(seat, side, 1);
            var front = new LandingFront
            {
                Landed = at,
                Approach = approach,
                Staging = StagingFor(world, sim.Now, seat, side, cfg.StagingDistance) ?? approach,
            };
            front.UnitIds.AddRange(SpawnBanditPartyIntent.Materialize(sim, at, size));
            host.Fronts.Add(front);
        }
        return host;
    }

    // Where a band comes out of the fog: dark to EVERY player, and at least the
    // bandit distance floor from every kingdom's units and buildings, so it never
    // appears beside someone else's land. Searched in the side's octant first,
    // then the two octants either side of it. Darkness is never waived: with no
    // such tile, the band does not come.
    private static TileCoord? FindLanding(GameWorld world, long now, TileCoord seat, Bearing bearing, int hashSeed)
    {
        var cfg = world.LandingConfig;
        bool Ok(TileCoord t) =>
            OpenGround(world, now, t)
            && !world.Structures.ContainsKey(t)
            && NearestKingdom(world, t) >= BanditConstants.MinSpawnDistance
            && !BanditRules.IsSeenByAnyPlayer(world, t);

        foreach (var b in new[] { bearing, (Bearing)(((int)bearing + 1) % 8), (Bearing)(((int)bearing + 7) % 8) })
            if (Omens.Search(world, seat, b, hashSeed, Ok, cfg.MinDistance, cfg.MaxDistance) is { } at)
                return at;
        return null;
    }

    // The gathering point: StagingDistance out along the band's side, or the
    // nearest open tile short of it.
    private static TileCoord? StagingFor(GameWorld world, long now, TileCoord seat,
                                          (int Dx, int Dy, Bearing Bearing) side, int distance)
    {
        for (var d = Math.Max(1, distance); d >= 1; d--)
        {
            var t = Step(seat, side, d);
            if (OpenGround(world, now, t)) return t;
        }
        return null;
    }

    // Walkable land in bounds that a bandit may enter (no wall or gate).
    private static bool OpenGround(GameWorld world, long now, TileCoord t)
    {
        if (t.X < 0 || t.Y < 0 || t.X >= world.Grid.Width || t.Y >= world.Grid.Height) return false;
        var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(world, t, now, world.BiomeDegradationConfig);
        if (biome is Biome.Water or Biome.None) return false;
        return !Sim.Core.Fortifications.Fortification.BlocksMover(world, t, BanditConstants.OwnerId);
    }

    // Chebyshev distance to the nearest unit or building of any KINGDOM (owner
    // >= 0). Caches, idols, rubble and bandits are not kingdoms, so unlike
    // BanditRules.ChebyshevToNearestPlayerPresence they do not push the landing
    // away.
    private static int NearestKingdom(GameWorld world, TileCoord tile)
    {
        var best = int.MaxValue;
        foreach (var u in world.Units.Values)
            if (u.OwnerId >= 0) best = Math.Min(best, Chebyshev(u.Position, tile));
        foreach (var s in world.Structures.Values)
            if (s.OwnerId >= 0) best = Math.Min(best, Chebyshev(s.At, tile));
        return best;
    }

    // ---- bookkeeping ----------------------------------------------------------

    // Every removal path converges on Population.OnUnitRemoved: a dead bandit
    // leaves its band.
    internal static void OnUnitRemoved(Simulation sim, Unit unit)
    {
        if (unit.OwnerId != BanditConstants.OwnerId) return;
        foreach (var host in sim.World.LandingHosts.Values)
            foreach (var front in host.Fronts)
                if (front.UnitIds.Remove(unit.Id)) return;
    }

    private static TileCoord Step(TileCoord from, (int Dx, int Dy, Bearing Bearing) side, int n) =>
        new(from.X + side.Dx * n, from.Y + side.Dy * n);

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // A small integer hash (deterministic, no Rng draw).
    private static uint Mix(int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b * 0x85EBCA77u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)c * 0xC2B2AE3Du;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            return h;
        }
    }
}
