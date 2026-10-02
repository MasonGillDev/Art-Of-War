using Sim.Core.Bandits;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
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
//
// M37 — an omen's raiders (docs/progression.md) are ordinary bandits the
// sim put on the map. The census recognises them from the DURABLE omen
// record (Omens.RaidOf), so a restarted driver still knows where they are
// headed, and gives the party a Target: the owner's seat. With nothing in
// sight they march on it instead of wandering; once there, or once they
// see something worth taking, they raid like anyone else.
//
// M39 — bandit CAMPS (docs/bandit-camps.md). Camp-bound bandits are not kept
// in _parties at all: every think re-derives them from the sim, which owns who
// is out (BanditCamp.Raiders) and who stands guard (everyone else on the camp's
// tile). The garrison stays put. Raiders steal what they can see; carrying
// anything, they go HOME and unload into the hoard (instead of despawning);
// with nothing seen and nothing carried they march on the camp's target seat.
// A razed camp's raiders are no longer on any list, so the census adopts them
// as ordinary raiders.
public sealed class BanditDriver
{
    private enum Mode { Ambush, Raid, Flee }

    private sealed class Party
    {
        public List<int> UnitIds = new();
        public Mode Mode = Mode.Raid;
        public TileCoord? OrderedDest;
        // M37 — an omen raid's objective; null for ordinary parties.
        public TileCoord? Target;
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
    // Two-act pacing — landing bandits whose assault is over (they reached the
    // castle or carry loot): the census adopts them as ordinary raiders.
    // Ephemeral: a restarted driver re-derives it from the same two facts.
    private readonly HashSet<int> _released = new();
    // M43 (docs/fix-combat-m43.md) — the brain leaves a fight to the board, and never sends
    // the same order that didn't take twice. All ephemeral, like the parties: a restarted
    // driver re-derives what it needs from the world.
    //   _doctrines — the doctrine last set per unit (set once, again only if it changes);
    //   _lastMove  — the last march each unit was sent on, to spot an order that didn't take;
    //   _blocked   — destinations found unreachable or full, skipped until the cooldown ends.
    private readonly Dictionary<int, DoctrineBehaviour> _doctrines = new();
    private readonly Dictionary<int, (TileCoord Dest, long Tick)> _lastMove = new();
    private readonly Dictionary<TileCoord, long> _blocked = new();

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
        Forget(world, now);
        MaybeSpawn(sim, now, world);
        foreach (var party in _parties)
            Act(sim, now, world, party);
        foreach (var camp in world.Structures.Values.OfType<BanditCamp>()
                     .OrderBy(c => c.At.Y).ThenBy(c => c.At.X).ToList())
        {
            ActCampGarrison(sim, now, world, camp);
            ActCampRaid(sim, now, world, camp);
        }
        foreach (var (_, host) in world.LandingHosts)   // target id order
            ActLanding(sim, now, world, host);
    }

    // ---- M43: leave the fight to the board ---------------------------------

    private void Forget(GameWorld world, long now)
    {
        foreach (var id in _doctrines.Keys.Where(id => !world.Units.ContainsKey(id)).ToList()) _doctrines.Remove(id);
        foreach (var id in _lastMove.Keys.Where(id => !world.Units.ContainsKey(id)).ToList()) _lastMove.Remove(id);
        foreach (var t in _blocked.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _blocked.Remove(t);
    }

    // A unit the brain may order: free by the one rule every system shares
    // (UnitAvailability: standing still, idle, not in a fight). A unit on a board is never
    // "walking" (the board owns its steps), so without the fight test every fighting bandit
    // was re-ordered each think, and the order overrode its doctrine.
    private static bool Free(GameWorld world, Unit u) => UnitAvailability.IsFree(world, u);

    // Put `behaviour` on a bandit that is on a board: once, and again only if it changes. The
    // withdraw threshold is a head count of its own side left on the board (BattleDoctrine), so
    // it is a share of the party: fewer than that many of us left, and it leaves.
    private void SetDoctrine(Simulation sim, long now, Unit u, DoctrineBehaviour behaviour, int partySize)
    {
        if (u.Board is null) return;
        if (_doctrines.TryGetValue(u.Id, out var set) && set == behaviour) return;
        var below = behaviour == DoctrineBehaviour.Withdraw ? 0
            : Math.Clamp((int)Math.Ceiling(partySize * _cfg.WithdrawBelowPercent / 100.0), 0, Subtile.Count);
        sim.SubmitIntent(now, new SetBattleDoctrineIntent(u.Id, (byte)behaviour, below) { PlayerId = BanditConstants.OwnerId });
        _doctrines[u.Id] = behaviour;
    }

    // Every march goes through here. An order that is sent a second time for the same place
    // means the first didn't take (the unit is still idle, not walking, not there): it had no
    // path, or no room to stop. Check both before sending it again; if it can't work, block the
    // place for a cooldown. `substitute`: go to the nearest tile with room instead (a seat, a
    // staging point); false for a prize, which the caller then picks another of.
    private void Go(Simulation sim, long now, GameWorld world, Unit u, TileCoord dest, bool substitute = true)
    {
        if (u.Board is not null || u.Position == dest) return;
        if (_lastMove.TryGetValue(u.Id, out var last) && last.Dest == dest && now > last.Tick
            && (!Walk.CanReach(world, u, dest) || !TileCapacity.HasRoom(world, dest, u.OwnerId)))
        {
            _blocked[dest] = now + _cfg.BlockedCooldownTicks;
            if (!substitute) return;
            var near = TileCapacity.RoomNear(world, dest, u.OwnerId, u.Traversal, except: dest);
            if (near == dest || near == u.Position) return;
            dest = near;
        }
        _lastMove[u.Id] = (dest, now);
        sim.SubmitIntent(now, new MoveIntent(u.Id, dest) { PlayerId = BanditConstants.OwnerId });
    }

    // Join a fight already open on `tile` only at fair odds: the party's power against the power
    // of everyone fighting there that isn't one of ours. A party with a member already on the
    // tile is in it and isn't asked.
    private bool WorthJoining(GameWorld world, List<Unit> units, TileCoord tile, long now)
    {
        if (!world.Battlefields.ContainsKey(tile) || units.Any(u => u.Position == tile)) return true;
        long ours = 0, theirs = 0;
        foreach (var u in units) ours += CombatRules.EffectivePower(world, u, now);
        foreach (var o in world.Units.Values)
            if (o.Position == tile && o.Board is not null && o.OwnerId != BanditConstants.OwnerId)
                theirs += CombatRules.EffectivePower(world, o, now);
        return ours * 100 >= theirs * _cfg.JoinPowerRatioPercent;
    }

    // A fleeing party's way to a dark tile mustn't cross a fight (a cheap test on the straight
    // line of tiles: the pathfinder plans the real way, and taking an avoid-set into it would
    // make it a global rule; docs/fix-combat-m43.md).
    private static bool LineCrossesBoard(GameWorld world, TileCoord from, TileCoord to)
    {
        if (world.Battlefields.Count == 0) return false;
        var n = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        for (var i = 1; i < n; i++)
        {
            var t = new TileCoord(from.X + (to.X - from.X) * i / n, from.Y + (to.Y - from.Y) * i / n);
            if (world.Battlefields.ContainsKey(t)) return true;
        }
        return false;
    }

    // ---- census: prune the dead, adopt the unknown ----------------------

    private void Census(GameWorld world)
    {
        var live = new HashSet<int>();
        foreach (var u in world.Units.Values)
            if (u.OwnerId == BanditConstants.OwnerId) live.Add(u.Id);

        // M39 — camp-bound bandits (raiders on a camp's list, and anyone
        // standing on a camp) are the sim's to account for; the driver
        // re-derives them each think and never keeps them in a party.
        var campBound = CampBound(world);
        // Two-act pacing — so are landing bands still marching or holding for
        // the assault (ActLanding); released ones fall through to the census.
        campBound.UnionWith(HostBound(world));
        foreach (var p in _parties) p.UnitIds.RemoveAll(id => !live.Contains(id) || campBound.Contains(id));
        _parties.RemoveAll(p => p.UnitIds.Count == 0);
        live.ExceptWith(campBound);

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
            // M37 — raiders an omen brought: raid, with the seat as objective.
            var omen = Sim.Core.Progression.Omens.RaidOf(world, ids[0]);
            _parties.Add(new Party
            {
                UnitIds = ids,
                Mode = omen is null ? mode : Mode.Raid,
                Target = omen?.Target,
            });
        }
    }

    // Every bandit a camp accounts for: its raiders and its garrison.
    private static HashSet<int> CampBound(GameWorld world)
    {
        var bound = new HashSet<int>();
        var camps = world.Structures.Values.OfType<BanditCamp>().ToList();
        if (camps.Count == 0) return bound;
        var campTiles = new HashSet<TileCoord>(camps.Select(c => c.At));
        foreach (var c in camps) bound.UnionWith(c.Raiders);
        foreach (var u in world.Units.Values)
            if (u.OwnerId == BanditConstants.OwnerId && campTiles.Contains(u.Position)) bound.Add(u.Id);
        return bound;
    }

    // Every landing bandit still under its band's orders.
    private HashSet<int> HostBound(GameWorld world)
    {
        var bound = new HashSet<int>();
        foreach (var host in world.LandingHosts.Values)
            foreach (var front in host.Fronts)
                foreach (var id in front.UnitIds)
                    if (!_released.Contains(id)) bound.Add(id);
        return bound;
    }

    // ---- the landing: form up, then strike together ----------------------
    //
    // docs/two-act-pacing.md. Each band marches from where it came out of the fog
    // to its gathering point, in sight of the target's castle, and holds there.
    // At the host's assault tick every band moves at once: to its approach tile,
    // then onto the castle, so each enters through its own side (on the
    // battlefield grid, its own edge row). Distractions are ignored until then:
    // a band that meets defenders on the way fights them where it stands (combat
    // starts on co-location) and resumes its march when it can.
    private void ActLanding(Simulation sim, long now, GameWorld world, Sim.Core.Landing.LandingHost host)
    {
        // The objective is the castle as it stood at the landing. If it has
        // fallen, or its kingdom is out, there is nothing to take by assault:
        // the bands turn into ordinary raiders.
        var castle = Sim.Core.Food.FoodConsumption.FindCastleFor(world, host.TargetOwnerId);
        var seatStands = castle is not null && castle.At == host.Seat
            && world.Players.TryGetValue(host.TargetOwnerId, out var target) && !target.Defeated;
        var assault = now >= host.AssaultTick;

        foreach (var front in host.Fronts)
            foreach (var id in front.UnitIds)
            {
                if (_released.Contains(id) || !world.Units.TryGetValue(id, out var u)) continue;
                // At the castle, or carrying loot: this one's assault is over. From
                // here it raids like any bandit (steal, then flee with it).
                if (!seatStands || u.Position == host.Seat || u.Cargo.Total > 0)
                {
                    _released.Add(id);
                    continue;
                }
                SetDoctrine(sim, now, u, DoctrineBehaviour.Advance, front.UnitIds.Count);
                if (!Free(world, u)) continue;   // marching, or fighting: the board has it
                var to = !assault ? front.Staging
                    : u.Position == front.Approach ? host.Seat
                    : front.Approach;
                if (u.Position != to) Go(sim, now, world, u, to);
            }
    }

    // ---- camps: the raid rides, steals, comes home ----------------------

    private void ActCampRaid(Simulation sim, long now, GameWorld world, BanditCamp camp)
    {
        // Raiders already back (home, empty, the raid having left) stand guard
        // until the camp's next tick takes them off the list.
        var units = camp.Raiders.Where(world.Units.ContainsKey).Select(id => world.Units[id])
            .Where(u => !Camps.IsBack(camp, u)).ToList();
        if (units.Count == 0) return;
        var home = camp.At;

        // Home with loot: into the hoard.
        foreach (var u in units)
            if (u.Position == home && Free(world, u) && u.Cargo.Total > 0)
                sim.SubmitIntent(now, new UnloadCargoIntent(u.Id) { PlayerId = BanditConstants.OwnerId });

        foreach (var u in units) SetDoctrine(sim, now, u, DoctrineBehaviour.Advance, units.Count);
        var sated = units.All(u => u.CargoAmount >= u.CargoCapacity);
        TileCoord? hold = null;
        var target = sated ? null : FindTarget(world, units, now, out hold);
        if (target is { } dest)
        {
            foreach (var u in units)
            {
                if (u.Position == dest && !IsMoving(u))
                {
                    if (Free(world, u)   // free: not in the fight on this tile, if there is one
                        && u.CargoAmount < u.CargoCapacity
                        && !world.CombatStates.ContainsKey(dest)
                        && StealableResource(world, dest, u) is { } r)
                        sim.SubmitIntent(now, new LoadCargoIntent(u.Id, r) { PlayerId = BanditConstants.OwnerId });
                }
                else if (Free(world, u))
                    Go(sim, now, world, u, dest, substitute: false);
            }
            return;
        }
        // A fight at a prize, at odds the raid won't take: hold and look again next think.
        if (hold is not null && units.All(u => u.Cargo.Total == 0)) return;

        // Carrying anything with nothing left in sight, or the seat reached and
        // bare: home. Otherwise, march on the target's seat.
        var seat = camp.TargetOwnerId >= 0
            ? Sim.Core.Food.FoodConsumption.FindCastleFor(world, camp.TargetOwnerId)?.At
            : null;
        var goHome = units.Any(u => u.Cargo.Total > 0) || seat is null || units.Any(u => u.Position == seat);
        var to = goHome ? home : seat!.Value;
        foreach (var u in units)
            if (Free(world, u) && u.Position != to) Go(sim, now, world, u, to);
    }

    // A camp's garrison holds its ground: whoever stands on the camp and isn't out raiding.
    private void ActCampGarrison(Simulation sim, long now, GameWorld world, BanditCamp camp)
    {
        var guards = world.Units.Values
            .Where(u => u.OwnerId == BanditConstants.OwnerId && u.Position == camp.At && !camp.Raiders.Contains(u.Id))
            .OrderBy(u => u.Id).ToList();
        foreach (var u in guards) SetDoctrine(sim, now, u, DoctrineBehaviour.Hold, guards.Count);
    }

    // ---- spawning: prosperity attracts wolves ---------------------------

    private void MaybeSpawn(Simulation sim, long now, GameWorld world)
    {
        // Grace period — no wolves until colonies have had time to settle
        // and raise a garrison (see BanditConfig.SpawnGraceTicks).
        if (now < _cfg.SpawnGraceTicks) return;

        // Real factions' structures only (owner >= 0). Before 2026-09-24 this
        // counted every structure not the bandits' own: caches (-2), idols
        // (-2), rubble (-3), and bandit camps, which pinned the target at the
        // cap from day 7 on every map (docs/secrets-and-progression-proposal.md).
        var playerStructures = world.Structures.Values
            .Count(s => s.OwnerId >= 0);
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
            if (FindTarget(world, units, now, out _) is null) return;   // keep lurking
            party.Mode = Mode.Raid;                                      // sprung!
        }

        // M43: on a board a bandit follows its doctrine, not the brain's marches: raiders
        // advance, a fleeing party withdraws.
        foreach (var u in units)
            SetDoctrine(sim, now, u, party.Mode == Mode.Flee ? DoctrineBehaviour.Withdraw : DoctrineBehaviour.Advance, units.Count);

        if (party.Mode == Mode.Raid)
        {
            var sated = units.All(u => u.CargoAmount >= u.CargoCapacity);
            TileCoord? hold = null;
            var target = sated ? null : FindTarget(world, units, now, out hold);
            if (target is { } dest)
            {
                foreach (var u in units)
                {
                    if (u.Position == dest && !IsMoving(u))
                    {
                        // Standing on the prize: steal if there's anything
                        // stealable and no fight raging on the tile.
                        if (Free(world, u)   // free: not in the fight on this tile, if there is one
                            && u.CargoAmount < u.CargoCapacity
                            && !world.CombatStates.ContainsKey(dest)
                            && StealableResource(world, dest, u) is { } r)
                            sim.SubmitIntent(now, new LoadCargoIntent(u.Id, r)
                                { PlayerId = BanditConstants.OwnerId });
                    }
                    else if (Free(world, u) && u.Position != dest)
                    {
                        // Not there and not on the way (fresh order, or a
                        // stalled/interrupted march) — (re)issue the move, unless the
                        // last one showed this place can't be reached or has no room.
                        Go(sim, now, world, u, dest, substitute: false);
                    }
                }
                party.OrderedDest = dest;
                return;
            }
            // A fight at a prize, at odds the raid won't take: hold and look again next think
            // (still carrying loot, it flees below instead).
            if (hold is not null && units.All(u => u.Cargo.Total == 0)) return;
            if (units.Any(u => u.CargoAmount > 0))
            {
                // Cargo full, or carrying something with nothing left in
                // sight: the job's done — go home. Falls through to Flee.
                party.Mode = Mode.Flee;
                party.OrderedDest = null;
            }
            else if (party.Target is { } seat && units.All(u => u.Position != seat))
            {
                // M37 — an omen raid with nothing in sight yet: march on.
                foreach (var u in units)
                    if (Free(world, u)) Go(sim, now, world, u, seat);
                party.OrderedDest = seat;
                return;
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
            if (dest is null || BanditRules.IsSeenByAnyPlayer(world, dest.Value)
                || (_blocked.TryGetValue(dest.Value, out var until) && until > now))
            {
                dest = PickDarkTile(world, now, units[0].Position);
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
                if (Free(world, u) && u.Position != dest.Value) Go(sim, now, world, u, dest.Value, substitute: false);
        }
    }

    // Nearest interesting tile any party member can SEE (Euclidean disc,
    // the same math as View.VisibleTiles): stealable structures first,
    // then player units (attack-move — combat triggers on co-location).
    //
    // M43: a place the last march showed unreachable or full is skipped for its cooldown, and a
    // fight already open on a prize is joined only at fair odds (WorthJoining): `holdAt` is
    // the prize that was passed over for its odds, so the caller waits instead of wandering off.
    private TileCoord? FindTarget(GameWorld world, List<Unit> units, long now, out TileCoord? holdAt)
    {
        TileCoord? best = null;
        TileCoord? held = null;
        var bestDist = int.MaxValue;
        var bestIsStealable = false;

        void Consider(TileCoord at, bool stealable)
        {
            if (!units.Any(u => WithinSight(u, at))) return;
            if (_blocked.TryGetValue(at, out var until) && until > now) return;
            var d = units.Min(u => Chebyshev(u.Position, at));
            // Stealable beats fightable at any distance; within a class,
            // nearest wins.
            var better = (stealable && !bestIsStealable)
                || (stealable == bestIsStealable && d < bestDist);
            if (!better) return;
            if (!WorthJoining(world, units, at, now)) { held ??= at; return; }
            // M43: a prize the party can't walk to (an island, a walled-in tile) is scenery,
            // or the party re-orders itself every think and never arrives.
            if (!Walk.CanReach(world, units[0], at)) return;
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
        holdAt = held;
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
        if (units.Any(u => IsMoving(u) || u.Activity != Activity.Idle || u.Board is not null)) return;   // leg in progress, or a fight
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
            foreach (var u in units) Go(sim, now, world, u, t);
            party.OrderedDest = t;
            return;
        }
    }

    private TileCoord? PickDarkTile(GameWorld world, long now, TileCoord? from = null)
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
            if (_blocked.TryGetValue(tile, out var until) && until > now) continue;
            if (from is { } origin && LineCrossesBoard(world, origin, tile)) continue;
            return tile;
        }
        return null;
    }

    // Movement is anchored on the unit (M4 pattern), NOT reflected in
    // Activity — a marching unit reads Activity.Idle. This is the
    // "am I walking" check every move/load decision gates on.
    private static bool IsMoving(Unit u) =>
        u.IsWalking;

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
