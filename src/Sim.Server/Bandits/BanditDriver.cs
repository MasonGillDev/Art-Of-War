using Sim.Core.Bandits;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Server.Bandits;

// M16 — the bandit BRAIN, and architecturally the dry run of the player
// automation layer: a server-side driver that READS world state (pure
// reads only) and acts exclusively by SUBMITTING ORDINARY INTENTS as the
// bandit faction. The sim stays dumb; the durable intent log stays the
// single source of truth (crash recovery replays the driver's decisions
// without the driver); twin-run determinism is proven by
// BanditDriverTests.ReplayFromIntentLog_HashesMatch.
//
// THREADING: Think() must run on the sim-owning thread (GameHost calls
// it inside the clock-loop lock, right after Run). It never blocks, never
// sleeps, and self-gates to one evaluation per ThinkPeriodTicks.
//
// STATE: party tracking (which units travel together, what they're
// doing) is EPHEMERAL by design. A restarted server forgets what every
// party was chasing; the census re-adopts live bandit units by tile
// cluster as fresh raiders. Acceptable, even flavorful — the world
// state itself (units, cargo, positions) was never here.
//
// The FSM, per party:
//   Ambush — sit still in the fog until a target enters the party's own
//            sight (bandits get fog too), then turn Raider.
//   Raid   — march at the nearest stealable structure the party can see
//            (extractor buffer first, then any stocked storage — yes,
//            castles), LoadCargo when standing on it; with nothing in
//            sight, wander a leg and look again; cargo full → Flee.
//   Flee   — run for a dark tile far from player presence and despawn
//            there, loot and all. While anyone can SEE a unit, the
//            despawn rejects (validated sim-side) — pursuit keeps the
//            loot in the world.
public sealed class BanditDriver
{
    private enum Mode { Ambush, Raid, Flee }

    private sealed class Party
    {
        public List<int> UnitIds = new();
        public Mode Mode = Mode.Raid;
        public TileCoord? OrderedDest;
    }

    private readonly BanditConfig _cfg;
    private readonly Random _rng;
    private readonly List<Party> _parties = new();
    // Spawn site → intended mode for the party that will materialize
    // there (the spawn intent resolves between thinks; the census adopts
    // the new units by tile and picks their assignment up here).
    private readonly Dictionary<TileCoord, Mode> _pendingModes = new();
    private long _lastThink = long.MinValue;
    private int _spawnOrdinal;

    public BanditDriver(BanditConfig cfg)
    {
        _cfg = cfg;
        _rng = new Random(unchecked((int)cfg.Seed));
    }

    public void Think(Simulation sim, long now)
    {
        if (!_cfg.Enabled) return;
        if (_lastThink != long.MinValue && now - _lastThink < _cfg.ThinkPeriodTicks) return;
        _lastThink = now;

        var world = sim.World;
        Census(world);
        MaybeSpawn(sim, now, world);
        foreach (var party in _parties)
            Act(sim, now, world, party);
    }

    // ---- census: prune the dead, adopt the unknown ----------------------

    private void Census(GameWorld world)
    {
        var live = new HashSet<int>();
        foreach (var u in world.Units.Values)
            if (u.OwnerId == BanditConstants.OwnerId) live.Add(u.Id);

        foreach (var p in _parties) p.UnitIds.RemoveAll(id => !live.Contains(id));
        _parties.RemoveAll(p => p.UnitIds.Count == 0);

        var tracked = new HashSet<int>(_parties.SelectMany(p => p.UnitIds));
        // Orphans (fresh spawns, or everything after a server restart)
        // cluster by tile — co-located strangers travel together. Sorted
        // iteration keeps adoption order reproducible.
        var orphansByTile = new SortedDictionary<(int Y, int X), List<int>>();
        foreach (var id in live.Where(id => !tracked.Contains(id)).OrderBy(i => i))
        {
            var pos = world.Units[id].Position;
            var key = (pos.Y, pos.X);
            if (!orphansByTile.TryGetValue(key, out var list))
                orphansByTile[key] = list = new List<int>();
            list.Add(id);
        }
        foreach (var ((y, x), ids) in orphansByTile)
        {
            var tile = new TileCoord(x, y);
            var mode = _pendingModes.Remove(tile, out var m) ? m : Mode.Raid;
            _parties.Add(new Party { UnitIds = ids, Mode = mode });
        }
    }

    // ---- spawning: prosperity attracts wolves ---------------------------

    private void MaybeSpawn(Simulation sim, long now, GameWorld world)
    {
        var playerStructures = world.Structures.Values
            .Count(s => s.OwnerId != BanditConstants.OwnerId);
        var target = Math.Min(_cfg.MaxLiveParties, playerStructures / _cfg.StructuresPerParty);
        if (_parties.Count >= target) return;

        // One spawn attempt per think — pressure ramps, never bursts.
        for (var attempt = 0; attempt < _cfg.SpawnAttemptsPerThink; attempt++)
        {
            var tile = new TileCoord(_rng.Next(world.Grid.Width), _rng.Next(world.Grid.Height));
            var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                world, tile, now, world.BiomeDegradationConfig);
            if (biome is Biome.Water or Biome.None) continue;
            if (BanditRules.IsSeenByAnyPlayer(world, tile)) continue;
            if (BanditRules.ChebyshevToNearestPlayerPresence(world, tile)
                < BanditConstants.MinSpawnDistance) continue;

            var size = _rng.Next(_cfg.PartySizeMin, _cfg.PartySizeMax + 1);
            size = Math.Clamp(size, 1, BanditConstants.MaxPartySize);
            sim.SubmitIntent(now, new SpawnBanditPartyIntent(tile, size)
                { PlayerId = BanditConstants.OwnerId });
            _spawnOrdinal++;
            _pendingModes[tile] = _cfg.AmbusherEvery > 0 && _spawnOrdinal % _cfg.AmbusherEvery == 0
                ? Mode.Ambush
                : Mode.Raid;
            return;
        }
    }

    // ---- the party FSM ---------------------------------------------------

    private void Act(Simulation sim, long now, GameWorld world, Party party)
    {
        var units = party.UnitIds.Select(id => world.Units[id]).ToList();

        if (party.Mode == Mode.Ambush)
        {
            if (FindTarget(world, units) is null) return;   // keep lurking
            party.Mode = Mode.Raid;                          // sprung!
        }

        if (party.Mode == Mode.Raid)
        {
            var sated = units.All(u => u.CargoAmount >= u.CargoCapacity);
            var target = sated ? null : FindTarget(world, units);
            if (target is { } dest)
            {
                foreach (var u in units)
                {
                    if (u.Position == dest && !IsMoving(u))
                    {
                        // Standing on the prize: steal if there's anything
                        // stealable and no fight raging on the tile.
                        if (u.Activity == Activity.Idle
                            && u.CargoAmount < u.CargoCapacity
                            && !world.CombatStates.ContainsKey(dest)
                            && StealableResource(world, dest, u) is { } r)
                            sim.SubmitIntent(now, new LoadCargoIntent(u.Id, r)
                                { PlayerId = BanditConstants.OwnerId });
                    }
                    else if (!IsMoving(u) && u.Activity == Activity.Idle && u.Position != dest)
                    {
                        // Not there and not on the way (fresh order, or a
                        // stalled/interrupted march) — (re)issue the move.
                        sim.SubmitIntent(now, new MoveIntent(u.Id, dest)
                            { PlayerId = BanditConstants.OwnerId });
                    }
                }
                party.OrderedDest = dest;
                return;
            }
            if (units.Any(u => u.CargoAmount > 0))
            {
                // Cargo full, or carrying something with nothing left in
                // sight: the job's done — go home. Falls through to Flee.
                party.Mode = Mode.Flee;
                party.OrderedDest = null;
            }
            else
            {
                Wander(sim, now, world, party, units);
                return;
            }
        }

        if (party.Mode == Mode.Flee)
        {
            var dest = party.OrderedDest;
            // (Re)pick an exit if none ordered or the old one got lit up.
            if (dest is null || BanditRules.IsSeenByAnyPlayer(world, dest.Value))
            {
                dest = PickDarkTile(world, now);
                if (dest is null) return;   // the world is lit — keep fighting, keep dying
                party.OrderedDest = dest;
            }
            if (units.All(u => u.Position == dest.Value && !IsMoving(u)))
            {
                // Home free — unless someone followed us. The intent
                // validates darkness sim-side; a rejection just means we
                // try again next think, deeper if needed.
                sim.SubmitIntent(now, new DespawnBanditPartyIntent(
                        party.UnitIds.OrderBy(i => i).ToArray())
                    { PlayerId = BanditConstants.OwnerId });
                party.OrderedDest = null;   // if it fenced, repick next think
                return;
            }
            foreach (var u in units)
                if (!IsMoving(u) && u.Activity == Activity.Idle && u.Position != dest.Value)
                    sim.SubmitIntent(now, new MoveIntent(u.Id, dest.Value)
                        { PlayerId = BanditConstants.OwnerId });
        }
    }

    // Nearest interesting tile any party member can SEE (Euclidean disc,
    // the same math as View.VisibleTiles): stealable structures first,
    // then player units (attack-move — combat triggers on co-location).
    private static TileCoord? FindTarget(GameWorld world, List<Unit> units)
    {
        TileCoord? best = null;
        var bestDist = int.MaxValue;
        var bestIsStealable = false;

        void Consider(TileCoord at, bool stealable)
        {
            if (!units.Any(u => WithinSight(u, at))) return;
            var d = units.Min(u => Chebyshev(u.Position, at));
            // Stealable beats fightable at any distance; within a class,
            // nearest wins.
            var better = (stealable && !bestIsStealable)
                || (stealable == bestIsStealable && d < bestDist);
            if (!better) return;
            best = at;
            bestDist = d;
            bestIsStealable = stealable;
        }

        foreach (var s in world.Structures.Values)
        {
            if (s.OwnerId == BanditConstants.OwnerId) continue;
            // Stockless buildings are NOT targets: bandits can't damage
            // structures (no sieges in M16), so an empty camp is just
            // scenery — without this skip a party "raids" it forever.
            if (!HasStock(s)) continue;
            Consider(s.At, stealable: true);
        }
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId == BanditConstants.OwnerId || u.IsEmbarked) continue;
            Consider(u.Position, stealable: false);
        }
        return best;
    }

    private static bool HasStock(Structure s) => s switch
    {
        Extractor ex => ex.Buffer > 0,
        StorageStructure ss => ss.Holdings.Count > 0,
        _ => false,
    };

    // What to grab from the tile the unit stands on: an extractor's
    // output, else the largest holding of a storage structure, else the
    // largest ground-pile resource.
    private static Resource? StealableResource(GameWorld world, TileCoord tile, Unit u)
    {
        if (world.Structures.TryGetValue(tile, out var s))
        {
            switch (s)
            {
                case Extractor ex when ex.Buffer > 0
                        && (u.CargoAmount == 0 || u.CargoResource == ex.Spec.OutputResource):
                    return ex.Spec.OutputResource;
                case StorageStructure ss:
                    var pick = ss.Holdings
                        .Where(kv => kv.Value > 0
                            && (u.CargoAmount == 0 || u.CargoResource == kv.Key))
                        .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                        .Select(kv => (Resource?)kv.Key).FirstOrDefault();
                    if (pick is not null) return pick;
                    break;
            }
        }
        if (world.GroundResources.TryGetValue(tile, out var pile))
            return pile.Where(kv => kv.Value > 0
                    && (u.CargoAmount == 0 || u.CargoResource == kv.Key))
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                .Select(kv => (Resource?)kv.Key).FirstOrDefault();
        return null;
    }

    // Nothing in sight: pick a wander leg and look again when we get there.
    private void Wander(Simulation sim, long now, GameWorld world, Party party, List<Unit> units)
    {
        var lead = units[0];
        if (units.Any(u => IsMoving(u) || u.Activity != Activity.Idle)) return;   // leg in progress
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var dx = _rng.Next(-_cfg.WanderRadius, _cfg.WanderRadius + 1);
            var dy = _rng.Next(-_cfg.WanderRadius, _cfg.WanderRadius + 1);
            var t = new TileCoord(
                Math.Clamp(lead.Position.X + dx, 0, world.Grid.Width - 1),
                Math.Clamp(lead.Position.Y + dy, 0, world.Grid.Height - 1));
            if (t == lead.Position) continue;
            var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                world, t, now, world.BiomeDegradationConfig);
            if (biome is Biome.Water or Biome.None) continue;
            foreach (var u in units)
                sim.SubmitIntent(now, new MoveIntent(u.Id, t)
                    { PlayerId = BanditConstants.OwnerId });
            party.OrderedDest = t;
            return;
        }
    }

    private TileCoord? PickDarkTile(GameWorld world, long now)
    {
        for (var attempt = 0; attempt < _cfg.SpawnAttemptsPerThink; attempt++)
        {
            var tile = new TileCoord(_rng.Next(world.Grid.Width), _rng.Next(world.Grid.Height));
            var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                world, tile, now, world.BiomeDegradationConfig);
            if (biome is Biome.Water or Biome.None) continue;
            if (BanditRules.IsSeenByAnyPlayer(world, tile)) continue;
            if (BanditRules.ChebyshevToNearestPlayerPresence(world, tile)
                < BanditConstants.MinSpawnDistance) continue;
            return tile;
        }
        return null;
    }

    // Movement is anchored on the unit (M4 pattern), NOT reflected in
    // Activity — a marching unit reads Activity.Idle. This is the
    // "am I between hops" check every move/load decision gates on.
    private static bool IsMoving(Unit u) =>
        u.NextArrivalTick is not null || (u.PathRemaining?.Count ?? 0) > 0;

    private static bool WithinSight(Unit u, TileCoord at)
    {
        var r = Sight.RadiusFor(u.Role);
        var dx = u.Position.X - at.X;
        var dy = u.Position.Y - at.Y;
        return dx * dx + dy * dy <= r * r;
    }

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
