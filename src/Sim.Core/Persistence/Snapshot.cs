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
    //   8 — M12 boats (Unit.Traversal). Phases B–F extend further
    //       (Dock structure + Slip, Unit.PassengerCap / Passengers /
    //       EmbarkedOn); the version stays 8 across the milestone.
    //
    // The military milestone (Barracks / Soldier / Archer / equipment)
    // did NOT bump the version: all of its enum additions (UnitRole,
    // Resource, StructureKind) are append-only byte values, the
    // structure payload dispatches on the kind byte (Barracks reuses
    // the StorageStructure shape), and Unit.Buffs was already
    // serialized since v4. A v9 snapshot written before the milestone
    // parses identically under post-milestone code.
    //
    //  10 — M15 extraction claims (Extractor.ClaimTiles +
    //       ConstructionSite.ClaimTiles) — the format change the old
    //       Extractor "PHASE-A PLACEHOLDER" comment always predicted.
    //  11 — famine-debt model (Castle.FoodDebt) — the snapshot bump the
    //       2026-06-09 trickle-deposit addendum deferred; paid 2026-06-11
    //       when the debt model shipped (docs/food-consumption.md).
    //  12 — M18 automation (GameWorld.StandingOrders + NextOrderId). Pure
    //       durable data + cursors — no new scheduled events, so
    //       RegenerateQueue is untouched.
    //  13 — M19 per-house food, Phase 1 (Unit.Home + House.ResidentCount).
    //       Both serialized (and therefore hashed) so restore can never
    //       drift the resident ledger from the units' Home fields.
    //  14 — M20 scouting reports, Phase 1 (GameWorld.ScoutMissions + the
    //       observation log). Pure durable data — the log is appended only
    //       by ScoutObservation.Capture (no new scheduled events), so
    //       RegenerateQueue is untouched.
    //  19 — M24 sieges (Structure.Health + Player.Defeated). Health is
    //       persisted right after OwnerId in the structure block, ahead of
    //       every kind-specific payload — so a partially razed Castle
    //       restores at its damaged HP, and BaseHealth-0 kinds (Cache /
    //       Canal) round-trip a stored 0 without auto-init re-filling
    //       them. Player.Defeated round-trips so a player whose castle
    //       was razed before the snapshot stays muted across restore
    //       (the IntentEvent gate is the consumer). Zero new anchors:
    //       PlayerDefeatedEvent / GameOverEvent both schedule at sim.Now
    //       and fire that same tick, so the queue never carries them
    //       across a snapshot.
    // v20 — M27 irrigation: BiomeDegradationConfig gains WaterRecoveryAmount
    //       (boosted recovery within WaterRecoveryRadius of water — the M21
    //       deferred knob). One int in the config block; no new anchors.
    // v21 — M28 boat freight: the Dock becomes a StorageStructure (the quay
    //       warehouse) — its payload gains the standard storage block,
    //       written between the slip and the production anchors. No new
    //       anchors; boat hauls ride the existing HaulPlan machinery.
    // v22 — automation substrate Phase A (docs/automation-substrate.md):
    //       GameWorld.Claims (the claims ledger) + Unit.Protected. Pure
    //       durable data — claims are mutated only by ClaimUnitIntent and
    //       carry no scheduled events, so RegenerateQueue is untouched
    //       (the same "zero new anchors" property M18's orders had).
    //       Claims MUST round-trip: a pull that was in flight when the
    //       process died has to stay committed across restore, or the
    //       driver would re-issue it against a unit already walking.
    // v23 — automation substrate Phase B: GameWorld.Orders (the universal
    //       order record — subject, DNF trigger, program/recipe, crew,
    //       selector, durable status). Written between the outgoing M18
    //       StandingOrders block and the claims ledger; the two models
    //       coexist until the Phase D cutover deletes the former. Still no
    //       new anchors — Maintain is level-triggered off driver thinks,
    //       not scheduled events, so RegenerateQueue stays untouched.
    // v24 — automation substrate Phase C: Selector.MaxAgeYears (the age
    //       filter becomes a WINDOW, which fertility requires). One int in
    //       the order block; the Breed recipe and the population predicates
    //       that ship alongside it are append-only enum values needing no
    //       format change.
    // v25 — automation substrate Phase D: the Routine program (Order.Steps,
    //       the circuit, + Order.CurrentStep, its durable cursor). The
    //       cursor MUST persist: a caravan mid-circuit has to resume where
    //       it was rather than restart at the first stop, and "which stop"
    //       is not derivable from the world.
    // v30 — rivers (docs/rivers.md): a RiverEdge byte per tile written right
    //       after the biome grid. Terrain, set at genesis, never mutated by
    //       the sim — but hashed, because the crossing fee is a function of
    //       it and two worlds that differ only in rivers must not hash equal.
    // v31 — refining (docs/refining-structures.md): the Extractor payload
    //       gains the refiner INPUT STORE (count + (resource, amount) pairs,
    //       enum-ordinal order) after the claim list; empty for ordinary
    //       extractors. Three new kinds dispatch: Smelter → Extractor,
    //       Workshop / Smithy → StorageStructure.
    // v33 — environmental fertility (docs/environmental-fertility.md): the
    //       BiomeDegradationConfig block gains five knobs (water radius /
    //       bonus, dry-edge penalty, forest-depth rings / bonus). The per-
    //       tile baseline itself is DERIVED from the grid, so nothing else
    //       changes shape; the sparse Fertility dict is untouched.
    // v34 — god mode (docs/god-mode.md): the Player block gains a GodMode
    //       bool after KingUnitId. Genesis-set, so a recovered god game keeps
    //       completing placements on the spot.
    // v35 — mixed cargo (docs/hauling-queue-and-routes.md): a unit's cargo
    //       is a count followed by (resource byte, amount) rows in enum
    //       order, replacing the single resource byte + amount; HaulPlan
    //       gains its Amount cap and JobId after Phase; a trailing haul-
    //       queue block (NextHaulJobId, NextHaulStamp, jobs in id order);
    //       Unit.RouteId after Protected; a haul-route block after the jobs.
    // v36 — progression (docs/progression.md): the Player block gains a
    //       ledger after GodMode: a has-ledger bool, then (stat byte, sub
    //       int, count long) rows in key order and the fired milestone ids
    //       in ascending order.
    // v37 — omens (docs/progression.md): a trailing progression block
    //       (ProgressionConfig knobs, NextOmenId, omens in id order with
    //       their chest, state, anchor, slips, plunder/escape flags, resolve
    //       tick and raid party). Resolved omens stay, with their outcome.
    // v38 — scouting secrets (docs/scouting-secrets.md): a trailing block of
    //       charts (players in id order, entries in (y, x) order: tile, hint,
    //       seen tick, state, gone tick), then the IdolConfig knobs,
    //       NextVisionGrantId and the live vision grants; the Idol structure
    //       payload is its grade byte.
    // v39 — bandit camps (docs/bandit-camps.md): the BanditCamp structure
    //       payload (hoard storage, target, source, raiders, departed flag,
    //       last raid / recruit ticks, tick anchor); the CampConfig block after the idol
    //       grants; ProgressionConfig gains SmokePopulation and CampCaptives
    //       after RumourAreaRadius.
    // v40 — two-act pacing (docs/two-act-pacing.md): a trailing landing block —
    //       the LandingConfig knobs (tick first; 0 = a one-act world), the
    //       pending LandingEvent's Seq (has-flag + long), then the hosts in target
    //       id order (target, seat, assault tick, fronts: landed, approach,
    //       staging, living unit ids ascending).
    // v42 — structure footprints (docs/structure-footprints.md): every
    //       structure row gains a Facing byte after Health.
    // v43 — subtile movement (docs/subtile-movement.md, M42): each unit's row in
    //       the battlefield block gains a has-flag and its subtile (x, y) after
    //       LeavingBoard, then a has-flag and the walk-in anchor (tick, Seq), then
    //       (phase 3) a has-flag, the drawn subtile route (count, x/y pairs) and
    //       its own has-flag and anchor (tick, Seq; absent while a battle pauses it).
    //       Phase 4: a unit's battle slot no longer stores a subtile or a sheltered
    //       flag (tile, last note, came-from, order only).
    //       Units with a subtile now always have a row.
    // v44 — one movement (docs/subtile-movement.md, M43); roads are subtile LINKS (the
    //       road block keeps its shape: owner subtile x, y, axis, condition, last decay): a unit's row loses
    //       PathRemaining and the next-arrival anchor (there are no tile hops; the walk
    //       is the subtile route in the battlefield block, with its own anchor), keeping
    //       PathFinalDest (the tile it was ordered to). The walk-in anchor and
    //       LeavingBoard are gone from the battlefield-block rows. A group row loses its
    //       path and arrival anchor (a group moves as its members' walks).
    // v45 — stone and ore (docs/stone-and-ore-land.md, M44): each unit row gains
    //       its survey anchor after the goal (has-flag, target x, y, nullable
    //       complete tick, nullable complete seq); a new trailing section holds
    //       the VeinConfig, the vein tiles, and each faction's known veins and
    //       proven-barren tiles, all (y, x)-sorted.
    // v46 — haul route UX (docs/m45-status.md, M45): a trailing section after the
    //       veins holds, per route in id order, its name and revision, and per
    //       crew (list order) its last serve (has-flag, stop, tick, loaded,
    //       unloaded, notes byte).
    // v47 — rest healing (docs/unit-healing.md): each unit row gains its rest-heal
    //       anchor after the death anchor (nullable tick, nullable seq).
    // v48 — M46 groups as records (docs/m46-groups-spec.md): the groups block opens
    //       with GameWorld.NextGroupId; each group row gains its name, kind (byte),
    //       parent (nullable int) and children (count + ids, ascending) after the
    //       members. GroupState gains Dismissed (4).
    // v49 — M46 Phase C, the group walks: each group row gains its march after the
    //       epoch — the lead path (count + subtile X,Y pairs; -1 = none), the lead
    //       index, the step anchor (nullable tick, nullable seq) and the stragglers
    //       (count + ids, ascending).
    public const int FormatVersion = 49;

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
            WriteOrders(bw, sim.World);
            WriteClaims(bw, sim.World);
            WriteScoutMissions(bw, sim.World);
            WriteRoyalty(bw, sim.World);        // M31 (v28)
            WriteHaulJobs(bw, sim.World);       // M36 (v35)
            WriteOmens(bw, sim.World);          // M37 (v37)
            WriteCharts(bw, sim.World);         // M38 (v38)
            WriteLanding(bw, sim.World);        // two-act pacing (v40)
            WriteBattlefields(bw, sim.World);   // M41 battlefield grid (v41)
            WriteVeins(bw, sim.World);          // M44 stone and ore (v45)
            WriteRouteExtras(bw, sim.World);    // M45 haul route UX (v46)
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
        world.Restoring = true;   // M42 — units and structures come back exactly as saved
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
        ReadOrders(br, world);
        ReadClaims(br, world);
        ReadScoutMissions(br, world);
        ReadRoyalty(br, world);             // M31 (v28)
        ReadHaulJobs(br, world);            // M36 (v35)
        ReadOmens(br, world);               // M37 (v37)
        ReadCharts(br, world);              // M38 (v38)
        ReadLanding(br, world);             // two-act pacing (v40)
        ReadBattlefields(br, world);        // M41 battlefield grid (v41); unit subtiles (v43)
        ReadVeins(br, world);               // M44 stone and ore (v45)
        ReadRouteExtras(br, world);         // M45 haul route UX (v46)
        world.Restoring = false;

        var sim = new Simulation(world, seed);
        sim.Rng.SetState(rngState);
        sim.RestoreClocks(now, nextSeq);
        // M4 Phase B: reconstruct the in-flight event queue from per-entity
        // next-event anchors. See Persistence/RegenerateQueue.cs.
        RegenerateQueue.From(sim);
        return sim;
    }

    // ----- haul route UX (M45, v46) ------------------------------------------
    //
    // Routes themselves are in the M36 haul block; this section adds what M45
    // gave them, keyed by route id so a mismatch fails loudly instead of
    // shifting fields onto the wrong route.

    private static void WriteRouteExtras(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.HaulRoutes.Count);
        foreach (var (_, route) in world.HaulRoutes)   // ascending id
        {
            bw.Write(route.RouteId);
            bw.Write(route.Name);
            bw.Write(route.Revision);
            bw.Write(route.Crews.Count);
            foreach (var crew in route.Crews)           // list order = ascending CrewId
            {
                bw.Write(crew.LastServe.HasValue);
                if (crew.LastServe is not { } last) continue;
                bw.Write(last.Stop);
                bw.Write(last.Tick);
                bw.Write(last.Loaded);
                bw.Write(last.Unloaded);
                bw.Write((byte)last.Notes);
            }
        }
    }

    private static void ReadRouteExtras(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        if (count != world.HaulRoutes.Count)
            throw new InvalidDataException(
                $"route extras list {count} routes, the haul block restored {world.HaulRoutes.Count}");
        for (var i = 0; i < count; i++)
        {
            var id = br.ReadInt32();
            if (!world.HaulRoutes.TryGetValue(id, out var route))
                throw new InvalidDataException($"route extras name route {id}, which the haul block lacks");
            route.Name = br.ReadString();
            route.Revision = br.ReadInt32();
            var crews = br.ReadInt32();
            if (crews != route.Crews.Count)
                throw new InvalidDataException(
                    $"route {id} extras list {crews} crews, the haul block restored {route.Crews.Count}");
            foreach (var crew in route.Crews)
            {
                if (!br.ReadBoolean()) { crew.LastServe = null; continue; }
                crew.LastServe = new Sim.Core.Hauling.ServeReport(
                    br.ReadInt32(), br.ReadInt64(), br.ReadInt32(), br.ReadInt32(),
                    (Sim.Core.Hauling.ServeNote)br.ReadByte());
            }
        }
    }

    // ----- battlefield grid (M41, v41) --------------------------------------
    //
    // The combat model and its morale knob (the rest of CombatConfig is in
    // WriteCombat), every open battlefield's turn anchor, and each unit's
    // battle state. Units are already restored when this runs, so unit rows
    // key by id. LastTurn is presentation only and is not written.

    private static void WriteBattlefields(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.CombatConfig.LineSupport);

        bw.Write(world.Battlefields.Count);
        foreach (var bf in world.Battlefields.Values)   // (y, x) order by construction
        {
            bw.Write(bf.Tile.X);
            bw.Write(bf.Tile.Y);
            bw.Write(bf.OpenedTick);
            bw.Write(bf.TurnNumber);
            bw.Write(bf.NextTurnTick);
            bw.Write(bf.NextTurnSeq);
            bw.Write(bf.Suspended);
        }

        var rows = world.Units.Values
            .Where(u => u.Board is not null || u.Doctrine is not null || u.EnteredFrom is not null || u.Subtile is not null || u.SubtileRoute is not null)
            .ToList();
        bw.Write(rows.Count);
        foreach (var u in rows)
        {
            bw.Write(u.Id);
            WriteNullableTileCoord(bw, u.EnteredFrom);
            bw.Write(u.EnteredTick);
            bw.Write(u.Subtile is not null);   // M42 (v43)
            if (u.Subtile is { } sub) { bw.Write(sub.X); bw.Write(sub.Y); }
            bw.Write(u.SubtileRoute is not null);   // the walk (M42 phase 3, M43) and its anchor
            if (u.SubtileRoute is { } route)
            {
                bw.Write(route.Count);
                foreach (var w in route) { bw.Write(w.X); bw.Write(w.Y); }
                bw.Write(u.SubtileRouteSeq is not null);
                if (u.SubtileRouteSeq is { } routeSeq) { bw.Write(u.SubtileRouteTick!.Value); bw.Write(routeSeq); }
                bw.Write(u.WaitingToEnter is not null);   // waiting at a battlefield's edge (M43)
                if (u.WaitingToEnter is { } waiting) { WriteNullableTileCoord(bw, waiting); bw.Write(u.WaitingSince); }
            }
            bw.Write(u.Doctrine is not null);
            if (u.Doctrine is { } d)
            {
                bw.Write((byte)d.Behaviour);
                bw.Write(d.WithdrawBelow);
            }
            bw.Write(u.Board is not null);
            if (u.Board is { } s)
            {
                bw.Write(s.Tile.X);
                bw.Write(s.Tile.Y);
                bw.Write((byte)s.LastNote);
                bw.Write(s.CameFrom is not null);
                if (s.CameFrom is { } cf) { bw.Write(cf.X); bw.Write(cf.Y); }
                WriteBattleOrder(bw, s.Order);
            }
        }
    }

    private static void WriteBattleOrder(BinaryWriter bw, Sim.Core.Battlefields.BattleOrder? o)
    {
        bw.Write(o is null ? (byte)0 : (byte)o.Kind);
        if (o is null) return;
        bw.Write(o.Destination.X);
        bw.Write(o.Destination.Y);
        bw.Write(o.Waypoints.Count);
        foreach (var w in o.Waypoints) { bw.Write(w.X); bw.Write(w.Y); }
        bw.Write(o.NextWaypoint);
        bw.Write(o.SwapWith);
    }

    private static Sim.Core.Battlefields.BattleOrder? ReadBattleOrder(BinaryReader br)
    {
        var kind = (Sim.Core.Battlefields.BattleOrderKind)br.ReadByte();
        if (kind == 0) return null;
        var dest = new Sim.Core.Battlefields.Subtile(br.ReadInt32(), br.ReadInt32());
        var n = br.ReadInt32();
        var waypoints = new List<Sim.Core.Battlefields.Subtile>(n);
        for (var i = 0; i < n; i++) waypoints.Add(new Sim.Core.Battlefields.Subtile(br.ReadInt32(), br.ReadInt32()));
        var next = br.ReadInt32();
        var swap = br.ReadInt32();
        return Sim.Core.Battlefields.BattleOrder.Restore(kind, dest, waypoints, next, swap);
    }

    private static void ReadBattlefields(BinaryReader br, GameWorld world)
    {
        var lineSupport = br.ReadInt32();
        world.RestoreCombatConfig(world.CombatConfig with { LineSupport = lineSupport });

        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var bf = new Sim.Core.Battlefields.Battlefield(tile, br.ReadInt64())
            {
                TurnNumber = br.ReadInt32(),
                NextTurnTick = br.ReadInt64(),
                NextTurnSeq = br.ReadInt64(),
                Suspended = br.ReadBoolean(),
            };
            world.Battlefields[tile] = bf;
        }

        var rows = br.ReadInt32();
        for (var i = 0; i < rows; i++)
        {
            var u = world.Units[br.ReadInt32()];
            u.EnteredFrom = ReadNullableTileCoord(br);
            u.EnteredTick = br.ReadInt64();
            if (br.ReadBoolean()) u.Subtile = new Sim.Core.Battlefields.Subtile(br.ReadInt32(), br.ReadInt32());   // M42 (v43)
            if (br.ReadBoolean())
            {
                var steps = br.ReadInt32();
                var route = new List<Sim.Core.Battlefields.WorldSubtile>(steps);
                for (var k = 0; k < steps; k++) route.Add(new Sim.Core.Battlefields.WorldSubtile(br.ReadInt32(), br.ReadInt32()));
                u.SubtileRoute = route;
                if (br.ReadBoolean()) { u.SubtileRouteTick = br.ReadInt64(); u.SubtileRouteSeq = br.ReadInt64(); }
                if (br.ReadBoolean()) { u.WaitingToEnter = ReadNullableTileCoord(br); u.WaitingSince = br.ReadInt64(); }
            }
            if (br.ReadBoolean())
                u.Doctrine = new Sim.Core.Battlefields.BattleDoctrine(
                    (Sim.Core.Battlefields.DoctrineBehaviour)br.ReadByte(), br.ReadInt32());
            if (br.ReadBoolean())
            {
                var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
                var slot = new Sim.Core.Battlefields.BoardSlot(tile)
                {
                    LastNote = (Sim.Core.Battlefields.StepNote)br.ReadByte(),
                };
                if (br.ReadBoolean()) slot.CameFrom = new Sim.Core.Battlefields.Subtile(br.ReadInt32(), br.ReadInt32());
                slot.Order = ReadBattleOrder(br);
                u.Board = slot;
            }
        }
    }

    // ----- landing (two-act pacing, v40) -----------------------------------

    private static void WriteLanding(BinaryWriter bw, GameWorld world)
    {
        var c = world.LandingConfig;
        bw.Write(c.Tick);
        bw.Write(c.BandSize);
        bw.Write(c.Fronts);
        bw.Write(c.MinDistance);
        bw.Write(c.MaxDistance);
        bw.Write(c.StagingDistance);
        bw.Write(c.AssaultDelayTicks);

        bw.Write(world.LandingSeq.HasValue);
        if (world.LandingSeq is { } seq) bw.Write(seq);

        bw.Write(world.LandingHosts.Count);
        foreach (var (_, host) in world.LandingHosts)   // target id order
        {
            bw.Write(host.TargetOwnerId);
            WriteTile(bw, host.Seat);
            bw.Write(host.AssaultTick);
            bw.Write(host.Fronts.Count);
            foreach (var f in host.Fronts)                // raised order
            {
                WriteTile(bw, f.Landed);
                WriteTile(bw, f.Approach);
                WriteTile(bw, f.Staging);
                bw.Write(f.UnitIds.Count);
                foreach (var id in f.UnitIds) bw.Write(id);   // ascending (materialized in order)
            }
        }

        static void WriteTile(BinaryWriter w, TileCoord t) { w.Write(t.X); w.Write(t.Y); }
    }

    private static void ReadLanding(BinaryReader br, GameWorld world)
    {
        world.RestoreLandingConfig(new Sim.Core.Landing.LandingConfig(
            Tick: br.ReadInt64(),
            BandSize: br.ReadInt32(),
            Fronts: br.ReadInt32(),
            MinDistance: br.ReadInt32(),
            MaxDistance: br.ReadInt32(),
            StagingDistance: br.ReadInt32(),
            AssaultDelayTicks: br.ReadInt64()));

        world.LandingSeq = br.ReadBoolean() ? br.ReadInt64() : null;

        var hosts = br.ReadInt32();
        for (var i = 0; i < hosts; i++)
        {
            var host = new Sim.Core.Landing.LandingHost
            {
                TargetOwnerId = br.ReadInt32(),
                Seat = ReadTile(br),
                AssaultTick = br.ReadInt64(),
            };
            var fronts = br.ReadInt32();
            for (var j = 0; j < fronts; j++)
            {
                var front = new Sim.Core.Landing.LandingFront
                {
                    Landed = ReadTile(br),
                    Approach = ReadTile(br),
                    Staging = ReadTile(br),
                };
                var n = br.ReadInt32();
                for (var k = 0; k < n; k++) front.UnitIds.Add(br.ReadInt32());
                host.Fronts.Add(front);
            }
            world.LandingHosts[host.TargetOwnerId] = host;
        }

        static TileCoord ReadTile(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32());
    }

    // ----- haul queue + named routes (M36) -------------------------------
    //
    // Counters, then jobs in id order (SortedDictionary). The queue ORDER is
    // not written: it is the stamps, so restoring the jobs restores the line.
    // ----- charts (M38, v38) ----------------------------------------------

    private static void WriteCharts(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.Charts.Count);
        foreach (var (playerId, chart) in world.Charts)   // id order
        {
            bw.Write(playerId);
            bw.Write(chart.Count);
            foreach (var (_, e) in chart)                  // (y, x) order
            {
                bw.Write(e.Tile.X);
                bw.Write(e.Tile.Y);
                bw.Write((byte)e.Hint);
                bw.Write(e.SeenTick);
                bw.Write((byte)e.State);
                bw.Write(e.GoneTick);
            }
        }

        var c = world.IdolConfig;
        bw.Write(c.Count);
        bw.Write(c.GreaterEvery);
        bw.Write(c.LesserTicks);
        bw.Write(c.LesserRadius);
        bw.Write(c.GreaterTicks);
        bw.Write(c.GreaterRadius);
        bw.Write(c.MinDistanceFromCastle);
        bw.Write(c.MinSpacing);
        bw.Write(world.NextVisionGrantId);
        bw.Write(world.VisionGrants.Count);
        foreach (var (_, g) in world.VisionGrants)   // id order
        {
            bw.Write(g.GrantId);
            bw.Write(g.OwnerId);
            bw.Write(g.Center.X);
            bw.Write(g.Center.Y);
            bw.Write(g.Radius);
            bw.Write(g.EndsTick);
            bw.Write(g.EndSeq);
        }

        // M39 (v39) — the camp knobs.
        var cc = world.CampConfig;
        bw.Write(cc.GarrisonStart);
        bw.Write(cc.GarrisonCap);
        bw.Write(cc.RecruitPeriodTicks);
        bw.Write(cc.RaidPeriodTicks);
        bw.Write(cc.RaidSize);
        bw.Write(cc.MinHome);
        bw.Write(cc.FirstRaidDelayTicks);
        bw.Write(cc.HoardCap);
        bw.Write(cc.HoardStartIron);
        bw.Write(cc.HoardStartOre);
        bw.Write(cc.MinDistance);
        bw.Write(cc.MaxDistance);
        bw.Write(cc.TickPeriodTicks);
    }

    private static void ReadCharts(BinaryReader br, GameWorld world)
    {
        var players = br.ReadInt32();
        for (var i = 0; i < players; i++)
        {
            var playerId = br.ReadInt32();
            var chart = new SortedDictionary<TileCoord, Sim.Core.Scouting.ChartEntry>(TileOrder.Instance);
            var n = br.ReadInt32();
            for (var j = 0; j < n; j++)
            {
                var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
                chart[tile] = new Sim.Core.Scouting.ChartEntry
                {
                    Tile = tile,
                    Hint = (Sim.Core.Scouting.SecretHint)br.ReadByte(),
                    SeenTick = br.ReadInt64(),
                    State = (Sim.Core.Scouting.ChartState)br.ReadByte(),
                    GoneTick = br.ReadInt64(),
                };
            }
            world.Charts[playerId] = chart;
        }

        world.RestoreIdolConfig(new Sim.Core.Scouting.IdolConfig(
            Count: br.ReadInt32(),
            GreaterEvery: br.ReadInt32(),
            LesserTicks: br.ReadInt64(),
            LesserRadius: br.ReadInt32(),
            GreaterTicks: br.ReadInt64(),
            GreaterRadius: br.ReadInt32(),
            MinDistanceFromCastle: br.ReadInt32(),
            MinSpacing: br.ReadInt32()));
        world.NextVisionGrantId = br.ReadInt32();
        var grants = br.ReadInt32();
        for (var i = 0; i < grants; i++)
        {
            var g = new Sim.Core.Scouting.VisionGrant
            {
                GrantId = br.ReadInt32(),
                OwnerId = br.ReadInt32(),
                Center = new TileCoord(br.ReadInt32(), br.ReadInt32()),
                Radius = br.ReadInt32(),
                EndsTick = br.ReadInt64(),
            };
            g.EndSeq = br.ReadInt64();
            world.VisionGrants[g.GrantId] = g;
        }

        world.RestoreCampConfig(new Sim.Core.Bandits.CampConfig(
            GarrisonStart: br.ReadInt32(),
            GarrisonCap: br.ReadInt32(),
            RecruitPeriodTicks: br.ReadInt64(),
            RaidPeriodTicks: br.ReadInt64(),
            RaidSize: br.ReadInt32(),
            MinHome: br.ReadInt32(),
            FirstRaidDelayTicks: br.ReadInt64(),
            HoardCap: br.ReadInt32(),
            HoardStartIron: br.ReadInt32(),
            HoardStartOre: br.ReadInt32(),
            MinDistance: br.ReadInt32(),
            MaxDistance: br.ReadInt32(),
            TickPeriodTicks: br.ReadInt64()));
    }

    // ----- progression (M37, v37) -----------------------------------------
    //
    // The config knobs, then the omens. The milestone rows are code and are
    // rebuilt from the config on restore; the ledgers ride in the player rows.

    private static void WriteOmens(BinaryWriter bw, GameWorld world)
    {
        var c = world.ProgressionConfig;
        bw.Write(c.ReprisalTrained);
        bw.Write(c.ReprisalWarningTicks);
        bw.Write(c.ReprisalRaidSize);
        bw.Write((byte)c.ReprisalChestResource);
        bw.Write(c.ReprisalChestAmount);
        bw.Write(c.WordSpreadsRefugees);
        bw.Write(c.WordSpreadsWarningTicks);
        bw.Write(c.FarHorizonsTiles);
        bw.Write(c.FarHorizonsIron);
        bw.Write(c.FarHorizonsSwords);
        bw.Write(c.RumourAreaRadius);
        bw.Write(c.SmokePopulation);
        bw.Write(c.CampCaptives);
        bw.Write(c.GoodHomeHouses);
        bw.Write(c.GoodHomeSettlers);
        bw.Write(c.GoodHomeWarningTicks);
        bw.Write(c.OmenSpawnMinDistance);
        bw.Write(c.OmenSpawnMaxDistance);
        bw.Write(c.OmenSlipTicks);
        bw.Write(c.OmenMaxSlips);

        bw.Write(world.NextOmenId);
        bw.Write(world.Omens.Count);
        foreach (var (_, o) in world.Omens)   // SortedDictionary → id order
        {
            bw.Write(o.OmenId);
            bw.Write(o.OwnerId);
            bw.Write((byte)o.Kind);
            bw.Write(o.SourceMilestoneId);
            bw.Write(o.Target.X);
            bw.Write(o.Target.Y);
            bw.Write((byte)o.From);
            bw.Write(o.Size);
            bw.Write((byte)o.ChestResource);
            bw.Write(o.ChestAmount);
            bw.Write(o.AreaCenter.X);
            bw.Write(o.AreaCenter.Y);
            bw.Write(o.AreaRadius);
            bw.Write((byte)o.State);
            bw.Write(o.DueTick);
            bw.Write(o.DueSeq);
            bw.Write(o.Slips);
            bw.Write(o.Plundered);
            bw.Write(o.Escaped);
            bw.Write(o.ResolvedTick);
            bw.Write(o.PartyIds.Count);
            foreach (var id in o.PartyIds) bw.Write(id);
        }
    }

    private static void ReadOmens(BinaryReader br, GameWorld world)
    {
        world.RestoreProgressionConfig(new Sim.Core.Progression.ProgressionConfig(
            ReprisalTrained: br.ReadInt32(),
            ReprisalWarningTicks: br.ReadInt64(),
            ReprisalRaidSize: br.ReadInt32(),
            ReprisalChestResource: (Resource)br.ReadByte(),
            ReprisalChestAmount: br.ReadInt32(),
            WordSpreadsRefugees: br.ReadInt32(),
            WordSpreadsWarningTicks: br.ReadInt64(),
            FarHorizonsTiles: br.ReadInt32(),
            FarHorizonsIron: br.ReadInt32(),
            FarHorizonsSwords: br.ReadInt32(),
            RumourAreaRadius: br.ReadInt32(),
            SmokePopulation: br.ReadInt32(),
            CampCaptives: br.ReadInt32(),
            GoodHomeHouses: br.ReadInt32(),
            GoodHomeSettlers: br.ReadInt32(),
            GoodHomeWarningTicks: br.ReadInt64(),
            OmenSpawnMinDistance: br.ReadInt32(),
            OmenSpawnMaxDistance: br.ReadInt32(),
            OmenSlipTicks: br.ReadInt64(),
            OmenMaxSlips: br.ReadInt32()));

        world.NextOmenId = br.ReadInt32();
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var omen = new Sim.Core.Progression.Omen
            {
                OmenId = br.ReadInt32(),
                OwnerId = br.ReadInt32(),
                Kind = (Sim.Core.Progression.OmenKind)br.ReadByte(),
                SourceMilestoneId = br.ReadInt32(),
                Target = new TileCoord(br.ReadInt32(), br.ReadInt32()),
                From = (Sim.Core.Progression.Bearing)br.ReadByte(),
                Size = br.ReadInt32(),
                ChestResource = (Resource)br.ReadByte(),
                ChestAmount = br.ReadInt32(),
                AreaCenter = new TileCoord(br.ReadInt32(), br.ReadInt32()),
                AreaRadius = br.ReadInt32(),
            };
            omen.State = (Sim.Core.Progression.OmenState)br.ReadByte();
            omen.DueTick = br.ReadInt64();
            omen.DueSeq = br.ReadInt64();
            omen.Slips = br.ReadInt32();
            omen.Plundered = br.ReadBoolean();
            omen.Escaped = br.ReadBoolean();
            omen.ResolvedTick = br.ReadInt64();
            var party = br.ReadInt32();
            for (var p = 0; p < party; p++) omen.PartyIds.Add(br.ReadInt32());
            world.Omens[omen.OmenId] = omen;
        }
    }

    private static void WriteHaulJobs(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.NextHaulJobId);
        bw.Write(world.NextHaulStamp);
        bw.Write(world.HaulJobs.Count);
        foreach (var (_, j) in world.HaulJobs)
        {
            bw.Write(j.JobId);
            bw.Write(j.OwnerId);
            bw.Write(j.Source.X); bw.Write(j.Source.Y);
            bw.Write(j.Dest.X);   bw.Write(j.Dest.Y);
            bw.Write((byte)j.Resource);
            bw.Write((byte)j.Kind);
            bw.Write(j.Target);
            bw.Write(j.Delivered);
            bw.Write(j.QueueStamp);
            bw.Write(j.QueuedAtTick);
        }

        // Routes: id order; stops, rules and crews in their list order (the
        // rules' order is the order they apply in; crews are ascending id).
        bw.Write(world.NextHaulRouteId);
        bw.Write(world.HaulRoutes.Count);
        foreach (var (_, route) in world.HaulRoutes)
        {
            bw.Write(route.RouteId);
            bw.Write(route.OwnerId);
            bw.Write(route.NextCrewId);
            bw.Write(route.Stops.Count);
            foreach (var stop in route.Stops)
            {
                bw.Write(stop.Tile.X); bw.Write(stop.Tile.Y);
                bw.Write(stop.Rules.Count);
                foreach (var rule in stop.Rules)
                {
                    bw.Write((byte)rule.Resource);
                    bw.Write((byte)rule.Op);
                    bw.Write(rule.Percent);
                }
            }
            bw.Write(route.Crews.Count);
            foreach (var crew in route.Crews)
            {
                bw.Write(crew.CrewId);
                bw.Write(crew.CurrentStop);
                bw.Write(crew.Members.Count);
                foreach (var id in crew.Members) bw.Write(id);
            }
        }
    }

    private static void ReadHaulJobs(BinaryReader br, GameWorld world)
    {
        world.NextHaulJobId = br.ReadInt32();
        world.NextHaulStamp = br.ReadInt64();
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var job = new Sim.Core.Hauling.HaulJob
            {
                JobId = br.ReadInt32(),
                OwnerId = br.ReadInt32(),
                Source = new TileCoord(br.ReadInt32(), br.ReadInt32()),
                Dest = new TileCoord(br.ReadInt32(), br.ReadInt32()),
                Resource = (Resource)br.ReadByte(),
                Kind = (Sim.Core.Hauling.HaulJobKind)br.ReadByte(),
                Target = br.ReadInt32(),
                Delivered = br.ReadInt32(),
                QueueStamp = br.ReadInt64(),
                QueuedAtTick = br.ReadInt64(),
            };
            world.HaulJobs.Add(job.JobId, job);
        }

        // Routes follow the jobs (same block, same version).
        world.NextHaulRouteId = br.ReadInt32();
        var routes = br.ReadInt32();
        for (var i = 0; i < routes; i++)
        {
            var route = new Sim.Core.Hauling.HaulRoute
            {
                RouteId = br.ReadInt32(),
                OwnerId = br.ReadInt32(),
                NextCrewId = br.ReadInt32(),
            };
            var stops = br.ReadInt32();
            for (var s = 0; s < stops; s++)
            {
                var stop = new Sim.Core.Hauling.RouteStop { Tile = new TileCoord(br.ReadInt32(), br.ReadInt32()) };
                var rules = br.ReadInt32();
                for (var r = 0; r < rules; r++)
                    stop.Rules.Add(new Sim.Core.Hauling.StopRule(
                        (Resource)br.ReadByte(), (Sim.Core.Hauling.StopRuleOp)br.ReadByte(), br.ReadInt32()));
                route.Stops.Add(stop);
            }
            var crews = br.ReadInt32();
            for (var c = 0; c < crews; c++)
            {
                var crew = new Sim.Core.Hauling.RouteCrew { CrewId = br.ReadInt32(), CurrentStop = br.ReadInt32() };
                var members = br.ReadInt32();
                for (var m = 0; m < members; m++) crew.Members.Add(br.ReadInt32());
                route.Crews.Add(crew);
            }
            world.HaulRoutes.Add(route.RouteId, route);
        }
    }

    // ----- royalty (M31) -------------------------------------------------
    //
    // Config only. The crown rides in the player rows and parentage rides on
    // the units, because both are per-entity facts; everything royal that is
    // NOT stored -- who is royal, who is the heir, who is a minor -- is
    // derived on read and so has nothing to serialize.

    private static void WriteRoyalty(BinaryWriter bw, GameWorld world)
    {
        var c = world.RoyaltyConfig;
        bw.Write(c.AuraRadius);
        bw.Write(c.AuraPowerBonus);
        bw.Write(c.MajorityAge);
    }

    private static void ReadRoyalty(BinaryReader br, GameWorld world)
    {
        var radius = br.ReadInt32();
        var bonus = br.ReadInt32();
        var majority = br.ReadInt32();
        world.RestoreRoyaltyConfig(new Sim.Core.Royalty.RoyaltyConfig(radius, bonus, majority));
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
        // v30 — river edge mask, same (y, x) order.
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
                bw.Write((byte)grid.RiverEdgesAt(new TileCoord(x, y)));
    }

    private static TileGrid ReadGrid(BinaryReader br)
    {
        var w = br.ReadInt32();
        var h = br.ReadInt32();
        var grid = new TileGrid(w, h, Biome.None);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                grid.SetBiome(new TileCoord(x, y), (Biome)br.ReadByte());
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                grid.SetRiverEdges(new TileCoord(x, y), (RiverEdge)br.ReadByte());
        return grid;
    }

    // ----- players (id-sorted) ------------------------------------------

    private static void WritePlayers(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.Players.Count);
        foreach (var (id, p) in world.Players)  // SortedDictionary → id order
        {
            bw.Write(id);
            // M24 (v19) — defeated state. Persists so the IntentEvent
            // gate keeps the player muted across restore. PopulationCount
            // is still re-derived from the restored units below.
            bw.Write(p.Defeated);
            // M31 (v28) -- the crown. Null through an interregnum and after
            // the line is extinct; both are legitimate long-lived states, so
            // both must survive a restart.
            WriteNullableInt(bw, p.KingUnitId);
            bw.Write(p.GodMode);   // v34
            WriteProgress(bw, p.Progress);   // v36
        }
    }

    private static void ReadPlayers(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id = br.ReadInt32();
            var defeated = br.ReadBoolean();
            var kingUnitId = ReadNullableInt(br);   // M31 (v28)
            var godMode = br.ReadBoolean();         // v34
            var p = new Player(id);
            if (defeated) p.Defeated = true;
            p.KingUnitId = kingUnitId;
            p.GodMode = godMode;
            p.Progress = ReadProgress(br);          // v36
            world.Players[id] = p;
        }
    }

    // M37 (v36) — one player's progress ledger, or a false byte for none.
    private static void WriteProgress(BinaryWriter bw, Sim.Core.Progression.ProgressLedger? ledger)
    {
        bw.Write(ledger is not null);
        if (ledger is null) return;
        bw.Write(ledger.Counts.Count);
        foreach (var (key, n) in ledger.Counts)   // SortedDictionary → key order
        {
            bw.Write((byte)key.Stat);
            bw.Write(key.Sub);
            bw.Write(n);
        }
        bw.Write(ledger.Fired.Count);
        foreach (var id in ledger.Fired) bw.Write(id);   // SortedSet → ascending
    }

    private static Sim.Core.Progression.ProgressLedger? ReadProgress(BinaryReader br)
    {
        if (!br.ReadBoolean()) return null;
        var ledger = new Sim.Core.Progression.ProgressLedger();
        var counts = br.ReadInt32();
        for (var i = 0; i < counts; i++)
        {
            var stat = (Sim.Core.Progression.ProgressStat)br.ReadByte();
            var sub = br.ReadInt32();
            ledger.Counts[new Sim.Core.Progression.ProgressKey(stat, sub)] = br.ReadInt64();
        }
        var fired = br.ReadInt32();
        for (var i = 0; i < fired; i++) ledger.Fired.Add(br.ReadInt32());
        return ledger;
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
            // M14: CargoCapacity is derived from Role via UnitCargoCatalog.
            // Not serialised — it's recomputed on read from the role byte above.
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
            WriteCargo(bw, u.Cargo);
            bw.Write(u.AssignmentEpoch);
            bw.Write(u.OwnerId);
            // The tile the unit was ordered to (its walk is in the battlefield block).
            WriteNullableTileCoord(bw, u.PathFinalDest);
            // M4: in-flight haul anchor.
            WriteHaulPlan(bw, u.HaulPlan);
            // M29 (v26): in-flight pursuit anchor. A chase running at
            // snapshot time must survive the restart, or the unit wakes up
            // holding a target it will never step toward again.
            WritePursuit(bw, u.Pursuit);
            // M5: group membership tag.
            WriteNullableInt(bw, u.GroupId);
            // M7: combat state.
            bw.Write(u.Health);
            WriteBuffs(bw, u.Buffs);
            // M8: population state.
            bw.Write(u.BornTick);
            WriteNullableLong(bw, u.DeathTick);
            WriteNullableLong(bw, u.DeathSeq);
            // v47: rest-heal anchor (docs/unit-healing.md).
            WriteNullableLong(bw, u.NextRestHealTick);
            WriteNullableLong(bw, u.NextRestHealSeq);
            // M12: movement domain.
            bw.Write((byte)u.Traversal);
            // M12: carrier state.
            bw.Write(u.PassengerCap);
            bw.Write(u.Passengers.Count);
            foreach (var pid in u.Passengers) bw.Write(pid); // SortedSet → ascending
            WriteNullableInt(bw, u.EmbarkedOn);
            // M19 (v13): home — the unit's food demand point.
            WriteNullableTileCoord(bw, u.Home);
            // v22: automation substrate — sacred-from-conscription flag.
            bw.Write(u.Protected);
            WriteNullableInt(bw, u.RouteId);         // M36 (v35)
            // M30 (v27): in-flight GOAL anchor. A unit walking to a job -- or
            // standing in a house waiting for food -- must wake from a restart
            // still doing it, or the player's one decision quietly evaporates
            // across the restart. docs/goal-shaped-intents.md.
            WriteGoal(bw, u.Goal);
            WriteSurvey(bw, u.Survey);               // M44 (v45)
            // M31 (v28): parentage. The dynasty's entire line question is a
            // pure read over these two, so losing them across a restore would
            // orphan every living child of the king.
            WriteNullableInt(bw, u.ParentAId);
            WriteNullableInt(bw, u.ParentBId);
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
            bw.Write(b.CargoModifier);    // M-cart
            bw.Write(b.MoveCostPercent);  // M-cart
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
            var cargo = br.ReadInt32();        // M-cart
            var moveCost = br.ReadInt32();     // M-cart
            list.Add(new Sim.Core.Combat.Buff(kind, pm, hm, exp,
                CargoModifier: cargo, MoveCostPercent: moveCost));
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
        bw.Write(plan.Amount);           // M36 (v35)
        bw.Write(plan.JobId);
    }

    // M36 (v35) — the cargo bag, ascending by Resource (CargoHold keeps a
    // SortedDictionary, so this is canonical with no sort here).
    private static void WriteCargo(BinaryWriter bw, CargoHold cargo)
    {
        bw.Write(cargo.Items.Count);
        foreach (var (r, a) in cargo.Items)
        {
            bw.Write((byte)r);
            bw.Write(a);
        }
    }

    private static List<(Resource Resource, int Amount)> ReadCargo(BinaryReader br)
    {
        var count = br.ReadInt32();
        var rows = new List<(Resource, int)>(count);
        for (var i = 0; i < count; i++) rows.Add(((Resource)br.ReadByte(), br.ReadInt32()));
        return rows;
    }

    private static void WritePursuit(BinaryWriter bw, Pursuit? chase)
    {
        if (chase is null) { bw.Write((byte)0); return; }
        bw.Write((byte)1);
        bw.Write(chase.TargetUnitId);
        bw.Write(chase.LeashTile.X); bw.Write(chase.LeashTile.Y);
        bw.Write(chase.LeashRadius);
    }

    private static void WriteGoal(BinaryWriter bw, GoalPlan? goal)
    {
        if (goal is null) { bw.Write((byte)0); return; }
        bw.Write((byte)1);
        bw.Write((byte)goal.Kind);
        bw.Write(goal.TargetTile.X); bw.Write(goal.TargetTile.Y);
        bw.Write(goal.PartnerUnitId);
        bw.Write(goal.Arg);              // M30 follow-up (v29)
    }

    private static void WriteSurvey(BinaryWriter bw, Sim.Core.Mining.SurveyPlan? survey)
    {
        if (survey is null) { bw.Write((byte)0); return; }
        bw.Write((byte)1);
        bw.Write(survey.Target.X); bw.Write(survey.Target.Y);
        WriteNullableLong(bw, survey.CompleteTick);
        WriteNullableLong(bw, survey.CompleteSeq);
    }

    private static Sim.Core.Mining.SurveyPlan? ReadSurvey(BinaryReader br)
    {
        if (br.ReadByte() == 0) return null;
        var plan = new Sim.Core.Mining.SurveyPlan(new TileCoord(br.ReadInt32(), br.ReadInt32()));
        plan.CompleteTick = ReadNullableLong(br);
        plan.CompleteSeq = ReadNullableLong(br);
        return plan;
    }

    // ----- veins (M44, v45) -------------------------------------------------
    //
    // The config, then the vein tiles, then per-faction known veins and
    // proven-barren tiles. Every set is (y, x)-sorted by construction
    // (TileOrder); factions in id order.

    private static void WriteVeins(BinaryWriter bw, GameWorld world)
    {
        var c = world.VeinConfig;
        bw.Write(c.OneIn);
        bw.Write(c.Seed);
        bw.Write(c.SurveyTicks);
        bw.Write(c.SurveyRadius);
        WriteTileSet(bw, world.Veins);
        WritePlayerTileSets(bw, world.KnownVeins);
        WritePlayerTileSets(bw, world.SurveyedBarren);
    }

    private static void ReadVeins(BinaryReader br, GameWorld world)
    {
        world.RestoreVeinConfig(new Sim.Core.Mining.VeinConfig(
            OneIn: br.ReadInt32(),
            Seed: br.ReadUInt64(),
            SurveyTicks: br.ReadInt64(),
            SurveyRadius: br.ReadInt32()));
        foreach (var t in ReadTileList(br)) world.Veins.Add(t);
        ReadPlayerTileSets(br, world.KnownVeins);
        ReadPlayerTileSets(br, world.SurveyedBarren);
    }

    private static void WriteTileSet(BinaryWriter bw, SortedSet<TileCoord> tiles)
    {
        bw.Write(tiles.Count);
        foreach (var t in tiles) { bw.Write(t.X); bw.Write(t.Y); }
    }

    private static List<TileCoord> ReadTileList(BinaryReader br)
    {
        var n = br.ReadInt32();
        var list = new List<TileCoord>(n);
        for (var i = 0; i < n; i++) list.Add(new TileCoord(br.ReadInt32(), br.ReadInt32()));
        return list;
    }

    private static void WritePlayerTileSets(BinaryWriter bw, SortedDictionary<int, SortedSet<TileCoord>> sets)
    {
        bw.Write(sets.Count);
        foreach (var (playerId, tiles) in sets)
        {
            bw.Write(playerId);
            WriteTileSet(bw, tiles);
        }
    }

    private static void ReadPlayerTileSets(BinaryReader br, SortedDictionary<int, SortedSet<TileCoord>> sets)
    {
        var players = br.ReadInt32();
        for (var i = 0; i < players; i++)
        {
            var playerId = br.ReadInt32();
            var set = new SortedSet<TileCoord>(TileOrder.Instance);
            foreach (var t in ReadTileList(br)) set.Add(t);
            sets[playerId] = set;
        }
    }

    private static GoalPlan? ReadGoal(BinaryReader br)
    {
        if (br.ReadByte() == 0) return null;
        var kind = (GoalKind)br.ReadByte();
        var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var partner = br.ReadInt32();
        var arg = br.ReadInt32();        // M30 follow-up (v29)
        return new GoalPlan(kind, tile, partner, arg);
    }

    private static Pursuit? ReadPursuit(BinaryReader br)
    {
        if (br.ReadByte() == 0) return null;
        var target = br.ReadInt32();
        var leash = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var radius = br.ReadInt32();
        return new Pursuit(target, leash, radius);
    }

    private static HaulPlan? ReadHaulPlan(BinaryReader br)
    {
        if (br.ReadByte() == 0) return null;
        var src   = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var dest  = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var res   = (Resource)br.ReadByte();
        var phase = (HaulPhase)br.ReadByte();
        var amount = br.ReadInt32();     // M36 (v35)
        var jobId = br.ReadInt32();
        return new HaulPlan(src, dest, res, phase, amount, jobId);
    }

    private static void ReadUnits(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id = br.ReadInt32();
            var pos = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var role = (UnitRole)br.ReadByte();
            var activity = (Activity)br.ReadByte();
            TileCoord? assignment = br.ReadByte() == 1
                ? new TileCoord(br.ReadInt32(), br.ReadInt32())
                : null;
            var cargo = ReadCargo(br);          // M36 (v35)
            var epoch = br.ReadByte();
            var ownerId = br.ReadInt32();
            var pathDest = ReadNullableTileCoord(br);
            var haulPlan = ReadHaulPlan(br);
            var pursuit = ReadPursuit(br);          // M29 (v26)
            var groupId = ReadNullableInt(br);
            var health = br.ReadInt32();
            var buffs = ReadBuffs(br);
            var bornTick = br.ReadInt64();
            var deathTick = ReadNullableLong(br);
            var deathSeq = ReadNullableLong(br);
            var restHealTick = ReadNullableLong(br);   // v47
            var restHealSeq = ReadNullableLong(br);
            var traversal = (Traversal)br.ReadByte();
            var passengerCap = br.ReadInt32();
            var passengerCount = br.ReadInt32();
            var passengers = new List<int>(passengerCount);
            for (var p = 0; p < passengerCount; p++) passengers.Add(br.ReadInt32());
            var embarkedOn = ReadNullableInt(br);
            var home = ReadNullableTileCoord(br);   // M19 (v13)
            var isProtected = br.ReadBoolean();     // v22 automation substrate
            var routeId = ReadNullableInt(br);      // M36 (v35)
            var goal = ReadGoal(br);                // M30 (v27)
            var survey = ReadSurvey(br);            // M44 (v45)
            var parentA = ReadNullableInt(br);      // M31 (v28)
            var parentB = ReadNullableInt(br);

            var u = new Unit(id, pos) { Role = role, OwnerId = ownerId, BornTick = bornTick, Traversal = traversal, PassengerCap = passengerCap, ParentAId = parentA, ParentBId = parentB };
            u.Home = home;   // ResidentCount restores from the House payload; no recompute
            u.Protected = isProtected;
            u.RouteId = routeId;
            foreach (var pid in passengers) u.Passengers.Add(pid);
            u.EmbarkedOn = embarkedOn;
            foreach (var (r, a) in cargo) u.Cargo.Add(r, a);

            u.PathFinalDest = pathDest;
            u.HaulPlan = haulPlan;
            u.Pursuit  = pursuit;
            u.Goal     = goal;
            u.Survey   = survey;
            u.GroupId  = groupId;
            u.Health   = health;
            foreach (var b in buffs) u.Buffs.Add(b);
            u.DeathTick = deathTick;
            u.DeathSeq  = deathSeq;
            u.NextRestHealTick = restHealTick;
            u.NextRestHealSeq  = restHealSeq;
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
            // M24 (v19) — siege HP. Written ahead of every kind-specific
            // payload so the common header stays uniform; restored before
            // AddStructure runs so a damaged Castle keeps its damaged HP.
            bw.Write(s.Health);
            // v42 — the structure's facing (docs/structure-footprints.md).
            bw.Write((byte)s.Facing);
            switch (s)
            {
                // House must come before StorageStructure: it IS a
                // StorageStructure but carries breeding state + (M19 v13)
                // its own food-home anchors.
                case House h:             WriteStorage(bw, h); WriteHouseOccupation(bw, h); WriteFoodHomeAnchors(bw, h); break;
                // M13 — Castle has the food-consumption anchor. Castle
                // must come before StorageStructure for the same reason.
                case Castle castle:       WriteStorage(bw, castle); WriteFoodHomeAnchors(bw, castle); break;
                // M23 — Cache is a StorageStructure; its loot rides the same
                // payload. Must precede the StorageStructure case.
                case Cache cache:         WriteStorage(bw, cache); break;
                // M39 — a bandit camp: its hoard, then its own fields.
                case BanditCamp camp:
                    WriteStorage(bw, camp);
                    bw.Write(camp.TargetOwnerId);
                    bw.Write(camp.SourceMilestoneId);
                    bw.Write(camp.Raiders.Count);
                    foreach (var id in camp.Raiders) bw.Write(id);
                    bw.Write(camp.RaidDeparted);
                    bw.Write(camp.LastRaidTick);
                    bw.Write(camp.LastRecruitTick);
                    bw.Write(camp.NextTickAt);
                    bw.Write(camp.NextTickSeq);
                    break;
                // M12/M28 — Dock: slip first (the reader needs it to
                // construct), then the quay-warehouse payload (v21), then
                // the boat-production anchors. Must precede the
                // StorageStructure case — a Dock IS one since M28.
                case Dock d:
                    bw.Write(d.Slip.X); bw.Write(d.Slip.Y);
                    WriteStorage(bw, d);
                    bw.Write(d.ProductionArmed);
                    bw.Write(d.LastProductionTick);
                    WriteNullableLong(bw, d.NextProductionTickSeq);
                    break;
                case StorageStructure ss: WriteStorage(bw, ss); break;
                case Extractor e:         WriteExtractor(bw, e); break;
                case ConstructionSite c:  WriteConstruction(bw, c); break;
                case Tower:               /* no fields */ break;
                case School:              /* no fields */ break;
                case Lodge:               /* no fields */ break;
                case Idol idol:           bw.Write((byte)idol.Grade); break;   // M38
                case Rubble:              /* no fields */ break;
                case Wall:                /* no fields */ break;   // M26
                case Gate:                /* no fields */ break;   // M26
                case Canal:               /* no fields */ break;   // structure footprints
                case Bridge:              /* no fields */ break;
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
            // M24 (v19) — siege HP. Read here, written onto the constructed
            // structure below BEFORE AddStructure runs, so the auto-init
            // there is a no-op for restored structures (matches the
            // Unit.Health restore pattern).
            var health = br.ReadInt32();
            var facing = (Sim.Core.Battlefields.Heading)br.ReadByte();   // v42
            Structure s = kind switch
            {
                StructureKind.Castle           => ReadCastleWithAnchors(br, at, ownerId),
                StructureKind.Stockpile        => ReadStorage(br, new Stockpile(at) { OwnerId = ownerId }),
                StructureKind.LumberCamp
                  or StructureKind.Quarry
                  or StructureKind.Mine
                  or StructureKind.Farm
                  or StructureKind.Smelter     => ReadExtractor(br, new Extractor(kind, at) { OwnerId = ownerId }),
                StructureKind.Workshop         => ReadStorage(br, new Workshop(at) { OwnerId = ownerId }),
                StructureKind.Smithy           => ReadStorage(br, new Smithy(at) { OwnerId = ownerId }),
                StructureKind.ConstructionSite => ReadConstruction(br, at, ownerId),
                StructureKind.Tower            => new Tower(at) { OwnerId = ownerId },
                StructureKind.House            => ReadHouseWithOccupation(br, at, ownerId),
                StructureKind.School           => new School(at) { OwnerId = ownerId },
                StructureKind.Lodge            => new Lodge(at) { OwnerId = ownerId },
                StructureKind.Idol             => new Idol(at, (Sim.Core.Scouting.IdolKind)br.ReadByte()) { OwnerId = ownerId },
                StructureKind.Barracks         => ReadStorage(br, new Barracks(at) { OwnerId = ownerId }),
                StructureKind.Cache            => ReadStorage(br, new Cache(at) { OwnerId = ownerId }),
                StructureKind.BanditCamp       => ReadCamp(br, at, ownerId),
                StructureKind.Rubble           => new Rubble(at) { OwnerId = ownerId },
                StructureKind.Wall             => new Wall(at) { OwnerId = ownerId },   // M26
                StructureKind.Gate             => new Gate(at) { OwnerId = ownerId },   // M26
                StructureKind.Canal            => new Canal(at) { OwnerId = ownerId },
                StructureKind.Bridge           => new Bridge(at) { OwnerId = ownerId },
                StructureKind.Dock             => ReadDock(br, at, ownerId),
                _ => throw new InvalidDataException($"Unknown structure kind: {kind}"),
            };
            s.Health = health;
            s.Facing = facing;
            world.AddStructure(s);
        }
    }

    // M39 (v39) — a bandit camp.
    private static BanditCamp ReadCamp(BinaryReader br, TileCoord at, int ownerId)
    {
        var holdings = new BanditCamp(at) { OwnerId = ownerId };
        ReadStorage(br, holdings);
        var camp = new BanditCamp(at)
        {
            OwnerId = ownerId,
            TargetOwnerId = br.ReadInt32(),
            SourceMilestoneId = br.ReadInt32(),
        };
        foreach (var (r, n) in holdings.Holdings) camp.Deposit(r, n);
        var raiders = br.ReadInt32();
        for (var i = 0; i < raiders; i++) camp.Raiders.Add(br.ReadInt32());
        camp.RaidDeparted = br.ReadBoolean();
        camp.LastRaidTick = br.ReadInt64();
        camp.LastRecruitTick = br.ReadInt64();
        camp.NextTickAt = br.ReadInt64();
        camp.NextTickSeq = br.ReadInt64();
        return camp;
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
        // M15: claimed tiles — list is maintained in canonical (y, x)
        // order by every writer, so we serialize verbatim.
        WriteClaimTiles(bw, e.ClaimTiles);
        // v31: refiner input store. SortedDictionary → enum-ordinal order.
        bw.Write(e.Inputs.Count);
        foreach (var (r, amt) in e.Inputs) { bw.Write((byte)r); bw.Write(amt); }
    }

    private static Extractor ReadExtractor(BinaryReader br, Extractor e)
    {
        var n = br.ReadInt32();
        for (var i = 0; i < n; i++) e.Workers.Add(br.ReadInt32());
        e.Buffer = br.ReadInt32();
        e.LastProductionTick = br.ReadInt64();
        e.TickArmed = br.ReadBoolean();
        e.NextProductionTickSeq = ReadNullableLong(br);
        ReadClaimTiles(br, e.ClaimTiles);
        var inputs = br.ReadInt32();
        for (var i = 0; i < inputs; i++)
        {
            var r = (Resource)br.ReadByte();
            e.Inputs[r] = br.ReadInt32();
        }
        return e;
    }

    // M15 — shared claim-list (de)serialization for both carriers
    // (Extractor + ConstructionSite).
    private static void WriteClaimTiles(BinaryWriter bw, List<TileCoord> claims)
    {
        bw.Write(claims.Count);
        foreach (var t in claims) { bw.Write(t.X); bw.Write(t.Y); }
    }

    private static void ReadClaimTiles(BinaryReader br, List<TileCoord> claims)
    {
        var n = br.ReadInt32();
        for (var i = 0; i < n; i++)
            claims.Add(new TileCoord(br.ReadInt32(), br.ReadInt32()));
    }

    private static void WriteConstruction(BinaryWriter bw, ConstructionSite c)
    {
        bw.Write((byte)c.TargetKind);
        // M21 — canal path (empty for every non-canal target). Written right
        // after TargetKind so ReadConstruction can reconstruct the path-scaled
        // site (its BuildDurationTicks/Required are ×length) BEFORE the
        // build-duration drift check below.
        bw.Write(c.CanalPath.Count);
        foreach (var t in c.CanalPath) { bw.Write(t.X); bw.Write(t.Y); }
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
        // M12: Dock slip carrier (null for non-Dock targets).
        WriteNullableTileCoord(bw, c.DockSlip);
        // M15: pending claim reserved at placement (empty for
        // non-claiming targets).
        WriteClaimTiles(bw, c.ClaimTiles);
        // M30 (v27): the manning sub-goal bound at placement.
        WriteNullableInt(bw, c.WorkerToManId);
    }

    private static ConstructionSite ReadConstruction(BinaryReader br, TileCoord at, int ownerId)
    {
        var targetKind = (StructureKind)br.ReadByte();
        // M21 — canal path read first so the site is reconstructed with its
        // length-scaled BuildDurationTicks (the drift check below compares
        // against the catalog × path length).
        var canalLen = br.ReadInt32();
        List<TileCoord>? canalPath = null;
        if (canalLen > 0)
        {
            canalPath = new List<TileCoord>(canalLen);
            for (var i = 0; i < canalLen; i++)
                canalPath.Add(new TileCoord(br.ReadInt32(), br.ReadInt32()));
        }
        var c = new ConstructionSite(at, targetKind, canalPath) { OwnerId = ownerId };
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
        c.DockSlip = ReadNullableTileCoord(br);
        ReadClaimTiles(br, c.ClaimTiles);
        c.WorkerToManId = ReadNullableInt(br);   // M30 (v27)
        return c;
    }

    // ----- house (M8) ----------------------------------------------------

    // M12 — Dock read: pulls slip + production state.
    private static Dock ReadDock(BinaryReader br, TileCoord at, int ownerId)
    {
        var slip = new TileCoord(br.ReadInt32(), br.ReadInt32());
        var d = new Dock(at, slip) { OwnerId = ownerId };
        ReadStorage(br, d);   // M28 (v21) — the quay-warehouse payload
        d.ProductionArmed = br.ReadBoolean();
        d.LastProductionTick = br.ReadInt64();
        d.NextProductionTickSeq = ReadNullableLong(br);
        return d;
    }

    // M13 — castle food consumption anchor + (Phase C–D) famine /
    // starvation event anchors. Written after ReadStorage's payload.
    // M13 (Castle) / M19 v13 (any IFoodHome) — the food-home anchor
    // block, byte-identical layout for Castle and House.
    private static void WriteFoodHomeAnchors(BinaryWriter bw, Sim.Core.Food.IFoodHome home)
    {
        bw.Write(home.LastFoodConsumedTick);
        WriteNullableLong(bw, home.FamineStartTick);
        bw.Write(home.FoodDebt);   // v11 — famine-debt model
        WriteNullableLong(bw, home.NextFamineCheckTick);
        WriteNullableLong(bw, home.NextFamineCheckSeq);
        WriteNullableLong(bw, home.NextStarvationDeathTick);
        WriteNullableLong(bw, home.NextStarvationDeathSeq);
    }

    private static void ReadFoodHomeAnchors(BinaryReader br, Sim.Core.Food.IFoodHome home)
    {
        home.LastFoodConsumedTick = br.ReadInt64();
        home.FamineStartTick = ReadNullableLong(br);
        home.FoodDebt = br.ReadInt32();   // v11 — famine-debt model
        home.NextFamineCheckTick = ReadNullableLong(br);
        home.NextFamineCheckSeq = ReadNullableLong(br);
        home.NextStarvationDeathTick = ReadNullableLong(br);
        home.NextStarvationDeathSeq = ReadNullableLong(br);
    }

    private static Castle ReadCastleWithAnchors(BinaryReader br, TileCoord at, int ownerId)
    {
        var c = new Castle(at) { OwnerId = ownerId };
        ReadStorage(br, c);
        ReadFoodHomeAnchors(br, c);
        return c;
    }

    private static void WriteHouseOccupation(BinaryWriter bw, House h)
    {
        bw.Write(h.ResidentCount);   // M19 (v13) — before the occupation flag
        // M30 (v27) -- the PRE-conception reservation, written before the
        // occupation so the two halves of a breeding cycle read in the order
        // they occur. Both are never non-null at once.
        if (h.PendingBreed is null) bw.Write((byte)0);
        else
        {
            bw.Write((byte)1);
            bw.Write(h.PendingBreed.ParentAId);
            bw.Write(h.PendingBreed.ParentBId);
            bw.Write(h.PendingBreed.ExpiryTick);
            bw.Write(h.PendingBreed.ExpirySeq);
        }
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
        h.ResidentCount = br.ReadInt32();   // M19 (v13)
        if (br.ReadByte() == 1)              // M30 (v27) -- pending pair
        {
            h.PendingBreed = new PendingBreed
            {
                ParentAId = br.ReadInt32(),
                ParentBId = br.ReadInt32(),
                ExpiryTick = br.ReadInt64(),
                ExpirySeq = br.ReadInt64(),
            };
        }
        if (br.ReadByte() == 1)
        {
            h.Occupation = new BreedingOccupation
            {
                ParentAId = br.ReadInt32(),
                ParentBId = br.ReadInt32(),
                BirthTick = br.ReadInt64(),
                BirthSeq  = br.ReadInt64(),
            };
        }
        ReadFoodHomeAnchors(br, h);   // M19 v13 — the house is a food home
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

    // ----- roads (sparse subtile links, by owner subtile y,x then axis; M43 v44) ---

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
            .OrderBy(kv => kv.Key.A.Y).ThenBy(kv => kv.Key.A.X).ThenBy(kv => (byte)kv.Key.Direction)
            .ToList();
        bw.Write(list.Count);
        foreach (var kv in list)
        {
            bw.Write(kv.Key.A.X);
            bw.Write(kv.Key.A.Y);
            bw.Write((byte)kv.Key.Direction);
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
            var axis = (SubtileLink.Axis)br.ReadByte();
            var condition = br.ReadInt32();
            var lastDecayTick = br.ReadInt64();
            world.Roads[SubtileLink.FromOwner(new Sim.Core.Battlefields.WorldSubtile(x, y), axis)] = new RoadState(condition, lastDecayTick);
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
        // Config first — nineteen fields, fixed order matches the record-struct
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
        bw.Write(c.WaterRecoveryRadius); // M21
        bw.Write(c.WaterRecoveryAmount); // M27 (v20)
        bw.Write(c.WaterFertilityRadius);    // M35 (v33)
        bw.Write(c.WaterFertilityBonus);     // M35 (v33)
        bw.Write(c.DryEdgePenalty);          // M35 (v33)
        bw.Write(c.ForestDepthRings);        // M35 (v33)
        bw.Write(c.ForestDepthBonusPerRing); // M35 (v33)

        // Sparse fertility dict in canonical (y, x) order — serialized
        // FAITHFULLY, including Deviation == 0 entries. Those are the M9
        // transition ANCHORS (deviation 0, lastUpdate = transition tick):
        // dropping them restores a producing extractor's tiles with an
        // implied lastUpdate of 0, over-applying the degrade rate across
        // the whole pre-snapshot history. (Latent M9 bug; caught by M15's
        // MidProduction_ClaimsPartiallyDegraded_SnapshotRoundTrip — the
        // filter was symmetric so round-trip hashes LOOKED identical while
        // the live and restored sims evolved apart.)
        var list = world.Fertility
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
        var waterRecoveryRadius = br.ReadInt32(); // M21
        var waterRecoveryAmount = br.ReadInt32(); // M27 (v20)
        var waterFertilityRadius = br.ReadInt32();    // M35 (v33)
        var waterFertilityBonus = br.ReadInt32();     // M35 (v33)
        var dryEdgePenalty = br.ReadInt32();          // M35 (v33)
        var forestDepthRings = br.ReadInt32();        // M35 (v33)
        var forestDepthBonusPerRing = br.ReadInt32(); // M35 (v33)
        world.RestoreBiomeDegradationConfig(new Sim.Core.Biomes.BiomeDegradationConfig(
            forestBase, grassBase, desertBase, hillsBase, mountainBase, waterBase,
            forestThresh, desertThresh, recoveryAmount, recoveryPeriod, degradePeriod, degradeRadius,
            waterRecoveryRadius, waterRecoveryAmount,
            waterFertilityRadius, waterFertilityBonus, dryEdgePenalty,
            forestDepthRings, forestDepthBonusPerRing));

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

    // ----- substrate orders (v23, id-sorted) -----------------------------

    private static void WriteOrders(BinaryWriter bw, GameWorld world)
    {
        // NextOrderId lives here now � it moved out of the deleted M18
        // standing-order block, which used to own the counter.
        bw.Write(world.NextOrderId);
        bw.Write(world.Orders.Count);
        foreach (var (id, o) in world.Orders) // SortedDictionary → id order
        {
            bw.Write(id);
            bw.Write(o.OwnerId);
            bw.Write(o.Priority);
            // Subject.
            bw.Write((byte)o.SubjectKind);
            bw.Write(o.SubjectTile.X);
            bw.Write(o.SubjectTile.Y);
            bw.Write((byte)o.SubjectRole);
            bw.Write(o.SubjectGroupId);
            // Trigger (DNF: clauses of predicates).
            bw.Write(o.Trigger.Any.Count);
            foreach (var clause in o.Trigger.Any)
            {
                bw.Write(clause.All.Count);
                foreach (var p in clause.All)
                {
                    bw.Write((byte)p.Kind);
                    bw.Write(p.Tile.X);
                    bw.Write(p.Tile.Y);
                    bw.Write((byte)p.Resource);
                    bw.Write((byte)p.Role);
                    bw.Write(p.Threshold);
                    bw.Write(p.CountInFlight);
                }
            }
            // Program + recipe.
            bw.Write((byte)o.Program);
            bw.Write((byte)o.Recipe);
            bw.Write(o.Target);
            bw.Write(o.SourceTile.X);
            bw.Write(o.SourceTile.Y);
            bw.Write((byte)o.Resource);
            // Crew.
            bw.Write((byte)o.CrewMode);
            bw.Write(o.NamedCrew.Count);
            foreach (var u in o.NamedCrew) bw.Write(u); // maintained ascending
            bw.Write((byte)o.Selector.Role);
            bw.Write(o.Selector.AnyRole);
            bw.Write(o.Selector.MinAgeYears);
            bw.Write(o.Selector.MaxAgeYears);   // v24
            bw.Write(o.Selector.RequireDormant);
            bw.Write(o.Selector.Anchor.X);
            bw.Write(o.Selector.Anchor.Y);
            bw.Write(o.Selector.Radius);
            // Flags + status.
            // Routine circuit (v25).
            bw.Write(o.Steps.Count);
            foreach (var st in o.Steps)
            {
                bw.Write(st.Tile.X);
                bw.Write(st.Tile.Y);
                bw.Write((byte)st.Action);
                bw.Write((byte)st.Resource);
                bw.Write(st.DepartWhen.Count);
                foreach (var p in st.DepartWhen)
                {
                    bw.Write((byte)p.Kind);
                    bw.Write(p.Tile.X);
                    bw.Write(p.Tile.Y);
                    bw.Write((byte)p.Resource);
                    bw.Write((byte)p.Role);
                    bw.Write(p.Threshold);
                    bw.Write(p.CountInFlight);
                }
            }
            bw.Write(o.ConscriptOptIn);
            bw.Write(o.EngageRadius);   // v26 — patrol posture
            bw.Write(o.LeashRadius);    // v26
            bw.Write(o.Enabled);
            bw.Write(o.RetryCount);
            bw.Write(o.LastFiredTick);
            bw.Write(o.CurrentStep);   // v25
        }
    }

    private static void ReadOrders(BinaryReader br, GameWorld world)
    {
        world.NextOrderId = br.ReadInt32();
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var id = br.ReadInt32();
            var ownerId = br.ReadInt32();
            var priority = br.ReadInt32();
            var subjectKind = (Sim.Core.Automation.SubjectKind)br.ReadByte();
            var subjectTile = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var subjectRole = (UnitRole)br.ReadByte();
            var subjectGroupId = br.ReadInt32();

            var trigger = new Sim.Core.Automation.Trigger();
            var clauseCount = br.ReadInt32();
            for (var c = 0; c < clauseCount; c++)
            {
                var clause = new Sim.Core.Automation.TriggerClause();
                var predCount = br.ReadInt32();
                for (var p = 0; p < predCount; p++)
                {
                    var kind = (Sim.Core.Automation.PredicateKind)br.ReadByte();
                    var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
                    var res = (Resource)br.ReadByte();
                    var role = (UnitRole)br.ReadByte();
                    var threshold = br.ReadInt64();
                    var countInFlight = br.ReadBoolean();
                    clause.All.Add(new Sim.Core.Automation.Predicate(
                        kind, tile, res, role, threshold, countInFlight));
                }
                trigger.Any.Add(clause);
            }

            var program = (Sim.Core.Automation.ProgramKind)br.ReadByte();
            var recipe = (Sim.Core.Automation.RecipeKind)br.ReadByte();
            var target = br.ReadInt64();
            var sourceTile = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var resource = (Resource)br.ReadByte();

            var crewMode = (Sim.Core.Automation.CrewMode)br.ReadByte();
            var crewCount = br.ReadInt32();
            var crew = new List<int>(crewCount);
            for (var u = 0; u < crewCount; u++) crew.Add(br.ReadInt32());
            var selRole = (UnitRole)br.ReadByte();
            var selAnyRole = br.ReadBoolean();
            var selMinAge = br.ReadInt32();
            var selMaxAge = br.ReadInt32();      // v24
            var selDormant = br.ReadBoolean();
            var selAnchor = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var selRadius = br.ReadInt32();

            // Routine circuit (v25).
            var stepCount = br.ReadInt32();
            var steps = new List<Sim.Core.Automation.RoutineStep>(stepCount);
            for (var s = 0; s < stepCount; s++)
            {
                var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
                var action = (Sim.Core.Automation.RoutineAction)br.ReadByte();
                var stepRes = (Resource)br.ReadByte();
                var depCount = br.ReadInt32();
                var depart = new List<Sim.Core.Automation.Predicate>(depCount);
                for (var dp = 0; dp < depCount; dp++)
                {
                    var kind = (Sim.Core.Automation.PredicateKind)br.ReadByte();
                    var pt = new TileCoord(br.ReadInt32(), br.ReadInt32());
                    var pres = (Resource)br.ReadByte();
                    var prole = (UnitRole)br.ReadByte();
                    var pthr = br.ReadInt64();
                    var pcif = br.ReadBoolean();
                    depart.Add(new Sim.Core.Automation.Predicate(kind, pt, pres, prole, pthr, pcif));
                }
                steps.Add(new Sim.Core.Automation.RoutineStep
                {
                    Tile = tile, Action = action, Resource = stepRes, DepartWhen = depart,
                });
            }

            var conscript = br.ReadBoolean();
            var engageRadius = br.ReadInt32();   // v26
            var leashRadius = br.ReadInt32();    // v26
            var enabled = br.ReadBoolean();
            var retryCount = br.ReadInt32();
            var lastFired = br.ReadInt64();
            var currentStep = br.ReadInt32();   // v25

            var order = new Sim.Core.Automation.Order
            {
                OrderId = id,
                OwnerId = ownerId,
                Priority = priority,
                SubjectKind = subjectKind,
                SubjectTile = subjectTile,
                SubjectRole = subjectRole,
                SubjectGroupId = subjectGroupId,
                Trigger = trigger,
                Program = program,
                Recipe = recipe,
                Target = target,
                SourceTile = sourceTile,
                Resource = resource,
                CrewMode = crewMode,
                Selector = new Sim.Core.Automation.Selector(
                    selRole, selAnyRole, selMinAge, selMaxAge, selDormant, selAnchor, selRadius),
                ConscriptOptIn = conscript,
                EngageRadius = engageRadius,
                LeashRadius = leashRadius,
                Steps = steps,
                Enabled = enabled,
                RetryCount = retryCount,
                LastFiredTick = lastFired,
                CurrentStep = currentStep,
            };
            order.NamedCrew.AddRange(crew);
            world.Orders.Add(id, order);
        }
    }

    // ----- claims ledger (v22, unit-id-sorted) ---------------------------

    private static void WriteClaims(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.Claims.Count);
        foreach (var (unitId, c) in world.Claims) // SortedDictionary → unit-id order
        {
            bw.Write(unitId);
            bw.Write(c.OrderId);
            bw.Write((byte)c.Purpose);
        }
    }

    private static void ReadClaims(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var unitId = br.ReadInt32();
            var orderId = br.ReadInt32();
            var purpose = (Sim.Core.Automation.ClaimPurpose)br.ReadByte();
            world.Claims.Add(unitId, new Sim.Core.Automation.Claim(unitId, orderId, purpose));
        }
    }

    // ----- scout missions (M20; keyed by scout unit id) -----------------

    private static void WriteScoutMissions(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.ScoutMissions.Count);
        foreach (var (scoutId, m) in world.ScoutMissions) // SortedDictionary → id order
        {
            bw.Write(scoutId);
            bw.Write(m.OwnerId);
            bw.Write(m.DispatchTick);
            bw.Write((byte)m.State);
            // Dispatch plan + recall rule (M20 Phase 3).
            bw.Write(m.HomeTile.X);
            bw.Write(m.HomeTile.Y);
            bw.Write((byte)m.ReturnRule);
            bw.Write(m.ElapsedLimitTicks);
            bw.Write(m.WaypointCursor);
            bw.Write(m.Waypoints.Count);
            foreach (var wp in m.Waypoints) { bw.Write(wp.X); bw.Write(wp.Y); }
            bw.Write(m.Legs.Count);
            foreach (var leg in m.Legs)
            {
                bw.Write(leg.Tick);
                bw.Write(leg.Center.X);
                bw.Write(leg.Center.Y);
                bw.Write(leg.Radius);
                bw.Write(leg.Sightings.Count);
                foreach (var s in leg.Sightings)
                {
                    bw.Write(s.Tile.X);
                    bw.Write(s.Tile.Y);
                    bw.Write((byte)s.Biome);
                    bw.Write(s.Units.Count);
                    foreach (var su in s.Units)
                    {
                        bw.Write(su.UnitId);
                        bw.Write(su.OwnerId);
                        bw.Write((byte)su.Role);
                        bw.Write((byte)su.Activity);
                    }
                    if (s.Structure is null)
                    {
                        bw.Write(false);
                    }
                    else
                    {
                        bw.Write(true);
                        bw.Write((byte)s.Structure.Kind);
                        bw.Write((byte)s.Structure.TargetKind);
                        bw.Write(s.Structure.OwnerId);
                        bw.Write(s.Structure.ProgressTicks);
                        bw.Write(s.Structure.BuildDurationTicks);
                    }
                }
            }
        }
    }

    private static void ReadScoutMissions(BinaryReader br, GameWorld world)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var scoutId = br.ReadInt32();
            var ownerId = br.ReadInt32();
            var dispatchTick = br.ReadInt64();
            var state = (Sim.Core.Scouting.ScoutMissionState)br.ReadByte();
            var homeTile = new TileCoord(br.ReadInt32(), br.ReadInt32());
            var returnRule = (Sim.Core.Scouting.ScoutReturnRule)br.ReadByte();
            var elapsedLimit = br.ReadInt64();
            var waypointCursor = br.ReadInt32();
            var waypointCount = br.ReadInt32();
            var waypoints = new List<TileCoord>(capacity: waypointCount);
            for (var w = 0; w < waypointCount; w++)
                waypoints.Add(new TileCoord(br.ReadInt32(), br.ReadInt32()));
            var mission = new Sim.Core.Scouting.ScoutMission
            {
                ScoutUnitId = scoutId,
                OwnerId = ownerId,
                DispatchTick = dispatchTick,
                State = state,
                HomeTile = homeTile,
                ReturnRule = returnRule,
                ElapsedLimitTicks = elapsedLimit,
                WaypointCursor = waypointCursor,
            };
            mission.Waypoints.AddRange(waypoints);
            var legCount = br.ReadInt32();
            for (var l = 0; l < legCount; l++)
            {
                var leg = new Sim.Core.Scouting.ObservationLeg
                {
                    Tick = br.ReadInt64(),
                    Center = new TileCoord(br.ReadInt32(), br.ReadInt32()),
                    Radius = br.ReadInt32(),
                };
                var sightCount = br.ReadInt32();
                for (var s = 0; s < sightCount; s++)
                {
                    var tile = new TileCoord(br.ReadInt32(), br.ReadInt32());
                    var biome = (Biome)br.ReadByte();
                    var unitCount = br.ReadInt32();
                    var units = new List<Sim.Core.Scouting.SightedUnit>(capacity: unitCount);
                    for (var u = 0; u < unitCount; u++)
                        units.Add(new Sim.Core.Scouting.SightedUnit(
                            br.ReadInt32(), br.ReadInt32(), (UnitRole)br.ReadByte(), (Activity)br.ReadByte()));
                    Sim.Core.Scouting.SightedStructure? structure = null;
                    if (br.ReadBoolean())
                        structure = new Sim.Core.Scouting.SightedStructure
                        {
                            Kind = (StructureKind)br.ReadByte(),
                            TargetKind = (StructureKind)br.ReadByte(),
                            OwnerId = br.ReadInt32(),
                            ProgressTicks = br.ReadInt64(),
                            BuildDurationTicks = br.ReadInt64(),
                        };
                    leg.Sightings.Add(new Sim.Core.Scouting.Sighting
                    {
                        Tile = tile,
                        Biome = biome,
                        Units = units,
                        Structure = structure,
                    });
                }
                mission.Legs.Add(leg);
            }
            world.ScoutMissions.Add(scoutId, mission);
        }
    }

    // ----- groups (id-sorted) -------------------------------------------

    private static void WriteGroups(BinaryWriter bw, GameWorld world)
    {
        bw.Write(world.NextGroupId);   // v48
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

            // v48: the record — name, kind, place in the tree (children ascending).
            bw.Write(g.Name);
            bw.Write((byte)g.Kind);
            WriteNullableInt(bw, g.ParentId);
            bw.Write(g.Children.Count);
            foreach (var childId in g.Children) bw.Write(childId);

            WriteNullableTileCoord(bw, g.PathFinalDest);
            bw.Write(g.MovementEpoch);

            // v49: the march (GroupMarch).
            if (g.MarchPath is { } path)
            {
                bw.Write(path.Count);
                foreach (var p in path) { bw.Write(p.X); bw.Write(p.Y); }
            }
            else bw.Write(-1);
            bw.Write(g.MarchLead);
            WriteNullableLong(bw, g.NextStepTick);
            WriteNullableLong(bw, g.NextStepSeq);
            bw.Write(g.Stragglers.Count);
            foreach (var sid in g.Stragglers) bw.Write(sid);
        }
    }

    private static void ReadGroups(BinaryReader br, GameWorld world)
    {
        world.NextGroupId = br.ReadInt32();   // v48
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

            var name     = br.ReadString();             // v48
            var kind     = (GroupKind)br.ReadByte();
            var parentId = ReadNullableInt(br);
            var childCount = br.ReadInt32();
            var childIds = new int[childCount];
            for (var c = 0; c < childCount; c++) childIds[c] = br.ReadInt32();

            var pathDest = ReadNullableTileCoord(br);
            var epoch    = br.ReadByte();

            var marchCount = br.ReadInt32();            // v49
            List<Sim.Core.Battlefields.WorldSubtile>? march = null;
            if (marchCount >= 0)
            {
                march = new List<Sim.Core.Battlefields.WorldSubtile>(marchCount);
                for (var k = 0; k < marchCount; k++)
                    march.Add(new Sim.Core.Battlefields.WorldSubtile(br.ReadInt32(), br.ReadInt32()));
            }
            var marchLead = br.ReadInt32();
            var stepTick  = ReadNullableLong(br);
            var stepSeq   = ReadNullableLong(br);
            var stragglerCount = br.ReadInt32();
            var stragglers = new int[stragglerCount];
            for (var k = 0; k < stragglerCount; k++) stragglers[k] = br.ReadInt32();

            var g = new Group(id) { OwnerId = ownerId, Kind = kind, Name = name, ParentId = parentId };
            foreach (var m in memberIds) g.Members.Add(m);
            foreach (var c in childIds) g.Children.Add(c);
            g.Position = pos;
            g.State = state;
            g.RendezvousTile = rendez;
            g.PendingArrivals = pending;
            g.PathFinalDest = pathDest;
            g.RestoreMovementEpoch(epoch);
            g.MarchPath = march;
            g.MarchLead = marchLead;
            g.NextStepTick = stepTick;
            g.NextStepSeq = stepSeq;
            foreach (var sid in stragglers) g.Stragglers.Add(sid);

            world.Groups[id] = g;
        }
    }
}
