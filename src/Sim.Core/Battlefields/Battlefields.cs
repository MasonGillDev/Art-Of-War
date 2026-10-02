using Sim.Core.Combat;
using Sim.Core.Fortifications;

namespace Sim.Core.Battlefields;

// M41 — the battlefield grid in the world (docs/battlefield-grid.md,
// docs/m41-status.md Phase 3). THE one writer of GameWorld.Battlefields and
// Unit.Board. The game's one combat model (M43).
//
// M42 phase 4 (docs/subtile-movement.md): a board READS the subtiles units
// already stand on. Nothing seats anyone when a board opens, nothing shelters,
// and nothing waits in an "outside lane": a unit's place is Unit.Subtile, the
// same as at world scale, and a battle "opens with everyone where they stood".
// Board state per unit (Unit.Board) is only its order, the subtile it last moved
// from, and why its last step failed.
//
// Lifecycle:
//   * OPEN — when hostile units share a tile (called from CombatTrigger, the
//     same presence-gated hook as the pooled rounds). Every unit on the tile
//     that has a subtile joins the board where it stands; a subtile route it was
//     walking becomes a battle order. The first turn is the first beat at least
//     half a turn away.
//   * TURNS — BattlefieldTurnEvent on every beat (ticks divisible by
//     RoundIntervalTicks, the same for every board): orders and doctrine
//     become steps (TurnPlanner), the resolver runs (TurnResolver), and the
//     result goes back through the world's own primitives.
//   * ARRIVALS while open — a unit that walks onto an open board's tile joins it on
//     the subtile it stepped onto (Admit, from the tile entry); no beat is waited.
//   * LEAVING — across any edge, on the beat: the unit steps onto the neighbour's
//     edge subtile (§4.1) and resumes the walk it was on; nothing pins it and it
//     no longer counts here.
//   * CLOSE — when the tile is no longer contested. Units resume the walk they
//     were on (Walk.Resume) and any interrupted errand; then the tile is
//     re-checked, so a waiting enemy reopens it or a siege takes over (D4).
public static class Battlefields
{
    // Units that count as on `tile` for fighting: on it, not aboard a boat.
    public static List<Unit> Present(GameWorld world, TileCoord tile)
    {
        var list = new List<Unit>();
        foreach (var u in world.Units.Values)
            if (u.Position == tile && !u.IsEmbarked) list.Add(u);
        return list;
    }

    public static bool HasHostilePair(GameWorld world, IEnumerable<Unit> units)
    {
        var owners = units.Select(u => u.OwnerId).Distinct().OrderBy(o => o).ToList();
        for (var i = 0; i < owners.Count; i++)
            for (var j = i + 1; j < owners.Count; j++)
                if (world.Diplomacy.AreHostile(owners[i], owners[j])) return true;
        return false;
    }

    // The next beat strictly after `now`.
    public static long NextBeat(GameWorld world, long now)
    {
        var rt = world.CombatConfig.RoundIntervalTicks;
        return (now / rt + 1) * rt;
    }

    // The first turn of a board opened at `now`: the first beat at least half
    // a turn away, so both sides get time to plan (§5).
    public static long FirstTurn(GameWorld world, long now)
    {
        var rt = world.CombatConfig.RoundIntervalTicks;
        var first = (now + rt / 2 + rt - 1) / rt * rt;
        return first <= now ? first + rt : first;
    }

    // ---- presence: the combat trigger in a Grid world -----------------------

    // Called whenever units arrive on (or reappear at) a tile. Opens a board on a
    // newly contested tile, admits newcomers to an open one, and hands a
    // unit-free siege to the pooled rounds (decision D4).
    public static void OnPresenceChanged(Simulation sim, TileCoord tile)
    {
        var world = sim.World;
        if (world.Battlefields.TryGetValue(tile, out var open))
        {
            Admit(sim, open);
            return;
        }
        var present = Present(world, tile);
        if (present.Count > 0 && HasHostilePair(world, present))
        {
            Open(sim, tile, present);
            return;
        }
        CombatTrigger.MaybeBeginSiege(sim, tile, present);
    }

    private static void Open(Simulation sim, TileCoord tile, List<Unit> present)
    {
        var world = sim.World;
        // A siege already running here yields to the fight between units: the
        // board decides who holds the tile first (D4). Removing the anchor
        // fences the queued siege round.
        world.CombatStates.Remove(tile);

        var bf = new Battlefield(tile, sim.Now);
        world.Battlefields[tile] = bf;

        // Everyone on the tile joins where they stand (friends who were walking through each
        // other are first set apart: on a board only enemies share a subtile).
        Placement.SeparateOn(world, tile);
        foreach (var u in present.OrderBy(u => u.Id)) Enroll(sim, u, tile);

        bf.NextTurnTick = FirstTurn(world, sim.Now);
        bf.NextTurnSeq = sim.Schedule(bf.NextTurnTick, new BattlefieldTurnEvent(tile));
    }

    // A board is open here and units have just landed (or were born, trained or
    // spawned on the tile): they join where they stand.
    private static void Admit(Simulation sim, Battlefield bf)
    {
        Placement.SeparateOn(sim.World, bf.Tile);
        var any = false;
        foreach (var u in Present(sim.World, bf.Tile).OrderBy(u => u.Id))
        {
            if (u.Board is not null) continue;
            any = true;
            Enroll(sim, u, bf.Tile);
        }
        if (any) Wake(sim, bf);
    }

    // A unit joins the board on the subtile it stands on. A unit with no subtile
    // (a tile over its cap) gets one if it can, and otherwise isn't part of the
    // fight: it can neither act nor be hit. A walk it was on becomes a battle order
    // (its destination is remembered: it resumes when it leaves); a group it belongs
    // to halts as a body (its members fight as individuals, section 8).
    //
    // ONLY ENEMIES SHARE A SUBTILE ON A BOARD. A unit whose subtile a friend on this board
    // already holds (a tile filled to the cap with one more friend passing through, frozen
    // where it stood when the fight opened) has no room here: it stays off the board, keeps
    // walking if it was, and otherwise walks to the nearest free place (Walk.Settle). The
    // landing crash of 2026-10-01 ("Owner 1 has two units on (1,0)") was this case reaching
    // BoardState.
    private static void Enroll(Simulation sim, Unit u, TileCoord tile)
    {
        HaltGroup(sim, u);
        if (u.Subtile is null) Placement.Seat(sim.World, u);
        if (u.Subtile is null) { u.Board = null; return; }
        if (FriendOnBoardHolds(sim.World, u, tile, u.Subtile.Value))
        {
            u.Board = null;
            Walk.Settle(sim, u);
            return;
        }
        var arrived = u.IsWalking && u.PathFinalDest == tile;
        u.Board = new BoardSlot(tile);
        Sim.Core.Healing.Rest.Interrupt(u);   // no healing on a board (docs/unit-healing.md)
        AdoptRoute(u);
        // A walk that was bound for this very tile has arrived: the errand it carries still
        // runs (a hauler caught on the castle still deposits).
        if (arrived) Walk.Finished(sim, u);
    }

    // Does a unit already on `tile`'s board, and not hostile to `u`, stand on `at`?
    private static bool FriendOnBoardHolds(GameWorld world, Unit u, TileCoord tile, Subtile at)
    {
        foreach (var o in world.Units.Values)
        {
            if (o == u || o.Board is not { } s || s.Tile != tile || o.Subtile != at) continue;
            if (!world.Diplomacy.AreHostile(u.OwnerId, o.OwnerId)) return true;
        }
        return false;
    }

    // A walk becomes a battle route: its steps inside this tile are walked one a turn, and
    // if it goes on across an edge the last waypoint is the subtile just outside (leaving
    // there). What lies beyond is dropped, but the tile it was heading for is kept
    // (Unit.PathFinalDest): the unit leaves, then plans afresh from where it lands.
    internal static void AdoptRoute(Unit u)
    {
        if (u.SubtileRoute is not { Count: > 0 } route || u.Subtile is not { } sub || u.Board is not { } slot) return;
        var prev = WorldSubtile.Of(slot.Tile, sub);
        var waypoints = new List<Subtile>();
        foreach (var w in route)
        {
            if (w.Tile == slot.Tile) { waypoints.Add(w.Sub); prev = w; continue; }
            if (prev.HeadingTo(w) is { } h) waypoints.Add(prev.Sub.Step(h));
            break;
        }
        SubtileRoutes.Cancel(u);
        if (waypoints.Count > 0) slot.Order = BattleOrder.Route(waypoints);
    }

    // A moving group whose member is drawn into a fight halts as a body: every member
    // stops where it is (the player re-orders it).
    private static void HaltGroup(Simulation sim, Unit u)
    {
        if (u.GroupId is not { } gid || !sim.World.Groups.TryGetValue(gid, out var group) || group.State != GroupState.Moving) return;
        foreach (var id in group.Members)
            if (sim.World.Units.TryGetValue(id, out var m)) Walk.Stop(m);
        group.State = GroupState.Idle;
        group.PendingArrivals = 0;
        group.PathFinalDest = null;
        group.BumpEpoch();
    }

    // The edge of `tile` that faces the 4-adjacent tile `other`, or null.
    public static Heading? EdgeToward(TileCoord tile, TileCoord other)
    {
        foreach (var h in Headings.All)
            if (tile.X + h.Dx() == other.X && tile.Y + h.Dy() == other.Y) return h;
        return null;
    }

    public static TileCoord Across(TileCoord tile, Heading h) => new(tile.X + h.Dx(), tile.Y + h.Dy());

    // The centre rows first (y = 1, then 2), then the outer rows (build
    // decision D2: units already on the tile stand in the centre rows). Where
    // created units are placed (Placement).
    internal static readonly Subtile[] CentreFirstOrder = new[] { 1, 2, 0, 3 }
        .SelectMany(y => Enumerable.Range(0, Subtile.Size).Select(x => new Subtile(x, y)))
        .ToArray();

    // The subtile `depth` rows in from edge `h`, in lane `lane`.
    public static Subtile Cell(Heading h, int depth, int lane) => h switch
    {
        Heading.North => new Subtile(lane, depth),
        Heading.South => new Subtile(lane, Subtile.Size - 1 - depth),
        Heading.West => new Subtile(depth, lane),
        _ => new Subtile(Subtile.Size - 1 - depth, lane),
    };

    // How the layer sees a world unit: friendly when its owner is the layer's
    // owner or an ally of it; an archer when its role is ranged.
    public static BoardMover MoverFor(GameWorld world, SubtileLayer layer, Unit u) => new(
        layer.Owner is { } o && Fortification.IsOwnOrAllied(world, u.OwnerId, o),
        UnitCombatCatalog.Spec(u.Role).Ranged);

    // An order or an arrival wakes a suspended board: it resolves on the next beat.
    public static void Wake(Simulation sim, Battlefield bf)
    {
        if (!bf.Suspended) return;
        bf.Suspended = false;
        bf.NextTurnTick = NextBeat(sim.World, sim.Now);
        bf.NextTurnSeq = sim.Schedule(bf.NextTurnTick, new BattlefieldTurnEvent(bf.Tile));
    }

    // The subtile just outside edge `h` closest to `from`.
    public static Subtile OutsideNearest(Subtile from, Heading h)
    {
        var lane = h is Heading.North or Heading.South ? from.X : from.Y;
        lane = Math.Clamp(lane, 0, Subtile.Size - 1);
        return Subtile.OutsideLane(h, lane);
    }

    // ---- the turn ---------------------------------------------------------------------

    // Which edges a unit may leave across: a neighbour tile inside the world
    // that feet can walk onto.
    public static IReadOnlySet<Heading> Exits(Simulation sim, TileCoord tile)
    {
        var world = sim.World;
        var exits = new HashSet<Heading>();
        foreach (var h in Headings.All)
        {
            var n = Across(tile, h);
            if (!world.Grid.InBounds(n)) continue;
            if (MovementCost.TerrainCostFor(world, tile, n, sim.Now, Traversal.Foot) >= Sim.Core.World.Biomes.Impassable) continue;
            exits.Add(h);
        }
        return exits;
    }

    // Who stands on each neighbour of `tile` (a unit of theirs there makes that edge no way out
    // for an enemy: TurnPlanner.WithdrawEdge).
    private static IReadOnlyDictionary<Heading, IReadOnlySet<int>> OwnersBeyond(GameWorld world, TileCoord tile)
    {
        var beyond = new Dictionary<Heading, IReadOnlySet<int>>();
        foreach (var h in Headings.All)
        {
            var n = Across(tile, h);
            if (!world.Grid.InBounds(n)) continue;
            var owners = new HashSet<int>();
            foreach (var u in world.Units.Values)
                if (u.Position == n && !u.IsEmbarked) owners.Add(u.OwnerId);
            if (owners.Count > 0) beyond[h] = owners;
        }
        return beyond;
    }

    internal static void RunTurn(Simulation sim, Battlefield bf)
    {
        var world = sim.World;
        var tile = bf.Tile;
        var cfg = world.CombatConfig.Battle;

        // Everyone on the tile has a place; a place without its unit is dropped.
        // (A unit born, trained or spawned here since the last turn joins now.)
        var present = Present(world, tile);
        Placement.SeparateOn(world, tile);
        foreach (var u in present.Where(u => u.Board is null || u.Board.Tile != tile).OrderBy(u => u.Id))
            Enroll(sim, u, tile);
        var slotted = world.Units.Values.Where(u => u.Board is { } s && s.Tile == tile).ToList();
        foreach (var u in slotted)
            if (u.Position != tile || u.IsEmbarked) u.Board = null;
        slotted = slotted.Where(u => u.Board is not null && u.Subtile is not null).ToList();
        // The last line of defence for BoardState's invariant: whatever put them there, a
        // second unit of one side on one subtile leaves the board and walks to free ground,
        // rather than one bad placement taking the whole server down.
        var (kept, crowded) = OneASubtile(slotted);
        foreach (var u in crowded) { u.Board = null; Walk.Settle(sim, u); }
        slotted = kept;

        var board = ReadBoard(sim, tile, slotted);
        var orders = new Dictionary<int, BattleOrder>();
        var doctrines = new Dictionary<int, BattleDoctrine>();
        var enteredFrom = new Dictionary<int, Heading>();
        foreach (var u in slotted)
        {
            if (u.Board!.Order is { } o) orders[u.Id] = o;
            doctrines[u.Id] = u.Doctrine ?? BattleDoctrine.DefaultFor(u.Role);
            if (u.EnteredFrom is { } from && EdgeToward(tile, from) is { } h) enteredFrom[u.Id] = h;
        }
        var steps = TurnPlanner.PlanAll(board, orders, doctrines, new BoardSurroundings(Exits(sim, tile), enteredFrom, OwnersBeyond(world, tile)));

        // Siege from the board (docs/structure-footprints.md, Update 2026-10-01): a unit
        // hostile to a destructible structure on this tile works on it whenever it is not
        // fighting a unit. Bandits never besiege (M16). So a defender on the wall keeps the
        // board open but no longer keeps the castle whole.
        var siegeTarget = CombatRules.SiegeableStructureOn(world, tile);
        Func<int, bool>? besieges = siegeTarget is null ? null
            : o => o != Sim.Core.Bandits.BanditConstants.OwnerId && world.Diplomacy.AreHostile(o, siegeTarget.OwnerId);

        bf.TurnNumber++;
        if (!TurnResolver.HasWork(board, steps, besieges))
        {
            if (!StillContested(world, tile)) { Close(sim, bf); return; }
            bf.Suspended = true;
            bf.LastTurn = null;
            return;
        }

        var owners = slotted.ToDictionary(u => u.Id, u => u.OwnerId);
        var result = TurnResolver.Resolve(board, steps, cfg, besieges);
        Apply(sim, bf, slotted, steps, result, siegeTarget);
        bf.LastTurn = new BattleTurnRecord(sim.Now, bf.TurnNumber, result, owners);

        if (!StillContested(world, tile)) { Close(sim, bf); return; }
        bf.NextTurnTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        bf.NextTurnSeq = sim.Schedule(bf.NextTurnTick, new BattlefieldTurnEvent(tile));
    }

    // Split board units into those BoardState can take and the ones that would be a second
    // unit of their owner on one subtile (BoardState's invariant). Lowest id keeps the subtile.
    private static (List<Unit> Kept, List<Unit> Crowded) OneASubtile(IEnumerable<Unit> units)
    {
        var kept = new List<Unit>();
        var crowded = new List<Unit>();
        var taken = new HashSet<(int Owner, Subtile At)>();
        foreach (var u in units.OrderBy(u => u.Id))
            (taken.Add((u.OwnerId, u.Subtile!.Value)) ? kept : crowded).Add(u);
        return (kept, crowded);
    }

    // The board as the resolver sees it, read from the real units.
    public static BoardState ReadBoard(Simulation sim, TileCoord tile, IEnumerable<Unit> slotted)
    {
        var world = sim.World;
        var units = new List<BoardUnit>();
        foreach (var u in slotted)
        {
            if (u.Subtile is not { } at) continue;
            units.Add(new BoardUnit(u.Id, u.OwnerId, at, u.Health,
                CombatRules.EffectivePower(world, u, sim.Now),
                UnitCombatCatalog.Spec(u.Role).Ranged) { CameFrom = u.Board!.CameFrom });
        }
        return new BoardState(LayerFor(world, tile), units, world.Diplomacy.AreHostile,
            (a, b) => Fortification.IsOwnOrAllied(world, a, b));
    }

    // What stands on the board: the tile's structure's footprint, set by its
    // facing and, for a wall line, the neighbours it joins
    // (docs/structure-footprints.md); open ground where there is none. The one
    // place terrain and cover will fill subtiles later.
    public static SubtileLayer LayerFor(GameWorld world, TileCoord tile) => Footprints.For(world, tile);

    private static void Apply(Simulation sim, Battlefield bf, List<Unit> slotted, Dictionary<int, PlannedStep> steps, TurnResult r,
        Structure? siegeTarget)
    {
        var world = sim.World;
        var byId = slotted.ToDictionary(u => u.Id);

        foreach (var u in slotted)
            u.Board!.LastNote = r.Failed.TryGetValue(u.Id, out var why) ? why
                : steps.TryGetValue(u.Id, out var s) && s.To is null ? s.Note : StepNote.None;

        var movedIds = new HashSet<int>();
        foreach (var m in r.Moves)
        {
            var u = byId[m.UnitId];
            if (m.Left)
            {
                // A unit that can't get across (no room on a neighbour that is itself a
                // board) stays on this one with its order, and tries again next beat.
                if (Leave(sim, u, bf, m.To.EdgeBeyond!.Value)) movedIds.Add(u.Id);
                continue;
            }
            movedIds.Add(u.Id);
            u.Subtile = m.To;
            u.Board!.CameFrom = m.From;
        }

        foreach (var u in slotted)
        {
            if (u.Board is not { } slot || slot.Tile != bf.Tile || u.Subtile is not { } at) continue;
            if (slot.Order is { } o) slot.Order = o.After(at, movedIds.Contains(u.Id));
            if (r.After.Get(u.Id) is { } after) u.Health = after.Hp;
        }

        foreach (var id in r.Deaths)
        {
            if (!world.Units.TryGetValue(id, out var dead)) continue;
            dead.Health = 0;
            dead.Board = null;
            CombatRules.OnUnitDeath(sim, dead);
        }

        // The besiegers' work: the structure loses HP and, at zero, is razed (a razed
        // castle defeats its owner). The board carries on over the rubble.
        if (siegeTarget is not null && r.StructureDamage > 0)
            CombatRules.DealSiegeDamage(sim, siegeTarget, r.StructureDamage, r.Besiegers.Select(id => byId[id].OwnerId));
    }

    // Off the board on the beat: the unit steps onto the neighbour's edge subtile like
    // any step across a tile edge (section 4.1), and walks on toward the tile it was heading for
    // (Walk.Resume). A step the ground refuses (a wall went up) leaves it on the board.
    private static bool Leave(Simulation sim, Unit u, Battlefield bf, Heading edge)
    {
        var world = sim.World;
        var from = WorldSubtile.Of(bf.Tile, u.Subtile!.Value);
        var to = new WorldSubtile(from.X + edge.Dx(), from.Y + edge.Dy());
        if (!world.Grid.InBounds(to.Tile) || new SubtileStepRules(world, StepMover.Of(u), null).Problem(from, to) is not null) return false;
        // The same guard as a walker's step onto a board (SubtileRoutes.Step): a neighbour that
        // is an open board takes the newcomer only where there is room for it.
        if (world.Battlefields.ContainsKey(to.Tile)
            && (Walk.HeldByStanding(world, u, to.Tile, to.Sub) || !TileCapacity.HasRoom(world, to.Tile, u.OwnerId)))
            return false;
        u.Board = null;
        TileEntry.Enter(sim, u, bf.Tile, to.Tile, to.Sub);
        CombatTrigger.MaybeBeginCombatOnTile(sim, to.Tile);
        if (u.Board is not null) return true;   // it walked into another fight
        if (u.PathFinalDest is not null) Walk.Resume(sim, u);
        else Walk.Settle(sim, u);
        return true;
    }

    public static bool StillContested(GameWorld world, TileCoord tile)
    {
        var fighting = world.Units.Values.Where(u => u.Board is { } s && s.Tile == tile);
        return HasHostilePair(world, fighting);
    }

    private static void Close(Simulation sim, Battlefield bf)
    {
        var world = sim.World;
        world.Battlefields.Remove(bf.Tile);
        var resume = new List<Unit>();
        foreach (var u in world.Units.Values)
        {
            if (u.Board is not { } s || s.Tile != bf.Tile) continue;
            u.Board = null;
            if (u.PathFinalDest is not null) resume.Add(u);
        }
        // The walk each was on carries on from where it stands; any errand without one resumes.
        foreach (var u in resume) Walk.Resume(sim, u);
        CombatRules.ResumeInterrupted(sim, bf.Tile);
        // Survivors standing on their own shelter start to heal (docs/unit-healing.md).
        Sim.Core.Healing.Rest.ArmAllOn(sim, bf.Tile);
        // A waiting enemy reopens the tile; attackers left alone with a
        // hostile structure lay siege to it (D4).
        OnPresenceChanged(sim, bf.Tile);
    }

    // ---- read helpers for intents and the view --------------------------------------

    public static bool TryGet(GameWorld world, Unit u, out Battlefield bf)
    {
        bf = null!;
        return u.Board is { } s && world.Battlefields.TryGetValue(s.Tile, out bf!);
    }

    // What each of the owner's units will try next beat, from positions now
    // (§10: "show the move it will try on the next beat"). Pure.
    public static Dictionary<int, PlannedStep> PlanPreview(Simulation sim, Battlefield bf)
    {
        var world = sim.World;
        var slotted = OneASubtile(world.Units.Values.Where(u => u.Board is { } s && s.Tile == bf.Tile && u.Subtile is not null)).Kept;
        var board = ReadBoard(sim, bf.Tile, slotted);
        var orders = new Dictionary<int, BattleOrder>();
        var doctrines = new Dictionary<int, BattleDoctrine>();
        var enteredFrom = new Dictionary<int, Heading>();
        foreach (var u in slotted)
        {
            if (u.Board!.Order is { } o) orders[u.Id] = o;
            doctrines[u.Id] = u.Doctrine ?? BattleDoctrine.DefaultFor(u.Role);
            if (u.EnteredFrom is { } from && EdgeToward(bf.Tile, from) is { } h) enteredFrom[u.Id] = h;
        }
        return TurnPlanner.PlanAll(board, orders, doctrines, new BoardSurroundings(Exits(sim, bf.Tile), enteredFrom, OwnersBeyond(world, bf.Tile)));
    }
}
