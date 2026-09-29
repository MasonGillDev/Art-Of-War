using Sim.Core.Combat;
using Sim.Core.Fortifications;

namespace Sim.Core.Battlefields;

// M41 — the battlefield grid in the world (docs/battlefield-grid.md,
// docs/m41-status.md Phase 3). THE one writer of GameWorld.Battlefields and
// Unit.Board. Only runs when CombatConfig.Model is Grid; a Pooled world never
// reaches any of this.
//
// Lifecycle:
//   * OPEN — when hostile units share a tile (called from CombatTrigger, the
//     same presence-gated hook as the pooled rounds). Every unit on the tile
//     gets a place: arrivals on the edge row facing where they came from,
//     everyone else in the centre rows, overflow sheltered (§2, D1–D2). The
//     first turn is the first beat at least half a turn away.
//   * TURNS — BattlefieldTurnEvent on every beat (ticks divisible by
//     RoundIntervalTicks, the same for every board): orders and doctrine
//     become steps (TurnPlanner), the resolver runs (TurnResolver), and the
//     result goes back through the world's own primitives.
//   * ARRIVALS while open — a unit whose hop lands on an open board waits
//     just outside the edge it came in by and steps on at the next beat.
//   * LEAVING — across any edge, on the beat, then a normal world hop onto
//     the neighbour (§4.1); nothing pins it and it no longer counts here.
//   * CLOSE — when the tile is no longer contested. Units resume the world
//     route they were on and any interrupted errand; then the tile is
//     re-checked, so a waiting enemy reopens it or a siege takes over (D4).
public static class Battlefields
{
    public static bool IsGrid(GameWorld world) => world.CombatConfig.Model == CombatModel.Grid;

    // Units that count as on `tile` for fighting: standing there, not aboard
    // a boat, not in the middle of the hop off a board they left.
    public static List<Unit> Present(GameWorld world, TileCoord tile)
    {
        var list = new List<Unit>();
        foreach (var u in world.Units.Values)
            if (u.Position == tile && !u.IsEmbarked && u.LeavingBoard != tile) list.Add(u);
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

    // Called whenever units arrive on (or reappear at) a tile. Opens a board
    // on a newly contested tile, admits newcomers to an open one, and hands a
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

        var taken = new HashSet<(int Owner, Subtile At)>();
        // Arrivals stand where they could have walked to from their edge; one
        // with nowhere to stand (or no way in) waits outside its lane.
        var layer = LayerFor(world, tile);
        var arrivals = present.Where(u => ArrivedNow(sim, u, tile) is not null).OrderBy(u => u.Id).ToList();
        foreach (var u in arrivals)
        {
            var edge = ArrivedNow(sim, u, tile)!.Value;
            var reachable = BattlePathing.ReachableFrom(layer, MoverFor(world, layer, u), edge);
            var cell = FirstFree(taken, u.OwnerId, EdgeFirstOrder(edge).Where(reachable.Contains));
            if (cell is null)
            {
                StopWorldHop(sim, u);
                u.Board = new BoardSlot(tile, Subtile.OutsideLane(edge, EntryLane(world, tile, u, edge)));
                continue;
            }
            Seat(sim, u, tile, cell, taken);
        }
        // Everyone else already on the tile: centre first, but only where it
        // could have walked to from the edge it came in by. One that walked on
        // and has no way in (it was waiting outside when the last board here
        // closed) waits outside that edge again.
        foreach (var u in present.Where(u => u.Board is null).OrderBy(u => u.Id))
        {
            var cell = FirstFree(taken, u.OwnerId, Reachable(world, layer, tile, u, Standable(world, layer, u, CentreFirstOrder)));
            if (cell is null && CameInBy(u, tile) is { } edge)
            {
                StopWorldHop(sim, u);
                u.Board = new BoardSlot(tile, Subtile.OutsideLane(edge, EntryLane(world, tile, u, edge)));
                continue;
            }
            Seat(sim, u, tile, cell, taken);
        }

        bf.NextTurnTick = FirstTurn(world, sim.Now);
        bf.NextTurnSeq = sim.Schedule(bf.NextTurnTick, new BattlefieldTurnEvent(tile));
    }

    // A board is open here and units have just arrived (or were born,
    // trained or spawned on the tile): give each a place.
    private static void Admit(Simulation sim, Battlefield bf)
    {
        var world = sim.World;
        var any = false;
        foreach (var u in Present(world, bf.Tile))
        {
            if (u.Board is not null) continue;
            any = true;
            StopWorldHop(sim, u);
            if (ArrivedNow(sim, u, bf.Tile) is { } edge)
                u.Board = new BoardSlot(bf.Tile, Subtile.OutsideLane(edge, EntryLane(world, bf.Tile, u, edge)));
            else
                u.Board = new BoardSlot(bf.Tile, default, sheltered: true);
        }
        if (any) Wake(sim, bf);
    }

    private static void Seat(Simulation sim, Unit u, TileCoord tile, Subtile? cell, HashSet<(int, Subtile)> taken)
    {
        StopWorldHop(sim, u);
        if (cell is { } c)
        {
            taken.Add((u.OwnerId, c));
            u.Board = new BoardSlot(tile, c);
        }
        else u.Board = new BoardSlot(tile, default, sheltered: true);
    }

    // The edge a unit came in by, if it arrived on `tile` this very tick.
    private static Heading? ArrivedNow(Simulation sim, Unit u, TileCoord tile)
    {
        if (u.EnteredTick != sim.Now || u.EnteredFrom is not { } from) return null;
        return EdgeToward(tile, from);
    }

    // The edge of `tile` that faces the 4-adjacent tile `other`, or null.
    public static Heading? EdgeToward(TileCoord tile, TileCoord other)
    {
        foreach (var h in Headings.All)
            if (tile.X + h.Dx() == other.X && tile.Y + h.Dy() == other.Y) return h;
        return null;
    }

    public static TileCoord Across(TileCoord tile, Heading h) => new(tile.X + h.Dx(), tile.Y + h.Dy());

    // A unit standing on a contested tile stops walking: its queued hop is
    // fenced (the epoch bump), but its committed world path is KEPT, so it
    // leaves the board in that direction and walks on when the battle ends.
    // Moving groups halt as a body (their members fight as individuals, §8).
    private static void StopWorldHop(Simulation sim, Unit u)
    {
        if (u.NextArrivalSeq is not null)
        {
            u.NextArrivalTick = null;
            u.NextArrivalSeq = null;
            u.BumpEpoch();
        }
        if (u.GroupId is { } gid && sim.World.Groups.TryGetValue(gid, out var group)
            && (group.PathRemaining is not null || group.NextArrivalSeq is not null))
        {
            MoveGroupIntent.ClearMovementAnchors(group);
            group.State = GroupState.Idle;
            group.BumpEpoch();
        }
    }

    // Edge row first (lanes 0→3), then each row inward.
    private static IEnumerable<Subtile> EdgeFirstOrder(Heading edge)
    {
        for (var depth = 0; depth < Subtile.Size; depth++)
            for (var lane = 0; lane < Subtile.Size; lane++)
                yield return Cell(edge, depth, lane);
    }

    // The centre rows first (y = 1, then 2), then the outer rows (build
    // decision D2: units already on the tile stand in the centre rows).
    private static readonly Subtile[] CentreFirstOrder = new[] { 1, 2, 0, 3 }
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

    private static Subtile? FirstFree(HashSet<(int Owner, Subtile At)> taken, int owner, IEnumerable<Subtile> order)
    {
        foreach (var c in order)
            if (!taken.Contains((owner, c))) return c;
        return null;
    }

    // The subtiles of `order` that u may stand on: open ground first, then
    // walls, gates and towers, each in `order`'s order (a castle's defenders
    // fill the courtyard before the ring).
    private static IEnumerable<Subtile> Standable(GameWorld world, SubtileLayer layer, Unit u, IEnumerable<Subtile> order)
    {
        var mover = MoverFor(world, layer, u);
        return order.Where(c => layer.CanStand(c, mover))
            .OrderBy(c => layer.KindAt(c) is SubtileKind.Open or SubtileKind.Cover ? 0 : 1);
    }

    // The subtiles of `order` u could have reached from the edge it came onto
    // this tile by (all of them for a unit that never walked on: born,
    // trained or placed here).
    private static IEnumerable<Subtile> Reachable(GameWorld world, SubtileLayer layer, TileCoord tile, Unit u, IEnumerable<Subtile> order)
    {
        if (CameInBy(u, tile) is not { } edge) return order;
        var reachable = BattlePathing.ReachableFrom(layer, MoverFor(world, layer, u), edge);
        return order.Where(reachable.Contains);
    }

    // The edge of `tile` a unit walked on by, if its last hop was onto this tile.
    private static Heading? CameInBy(Unit u, TileCoord tile) =>
        u.EnteredFrom is { } from ? EdgeToward(tile, from) : null;

    // How the layer sees a world unit: friendly when its owner is the layer's
    // owner or an ally of it; an archer when its role is ranged.
    public static BoardMover MoverFor(GameWorld world, SubtileLayer layer, Unit u) => new(
        layer.Owner is { } o && Fortification.IsOwnOrAllied(world, u.OwnerId, o),
        UnitCombatCatalog.Spec(u.Role).Ranged);

    // The lane a newcomer waits in: one it can step in by, whose edge subtile
    // its side doesn't hold, with the fewest of its side already waiting
    // there, then the lowest.
    private static int EntryLane(GameWorld world, TileCoord tile, Unit u, Heading edge)
    {
        var layer = LayerFor(world, tile);
        var mover = MoverFor(world, layer, u);
        var best = 0;
        (int Shut, int Held, int Queue, int Lane) bestKey = (int.MaxValue, 0, 0, 0);
        for (var lane = 0; lane < Subtile.Size; lane++)
        {
            var cell = Cell(edge, 0, lane);
            var outside = Subtile.OutsideLane(edge, lane);
            var shut = layer.CanStep(outside, cell, mover) ? 0 : 1;
            var held = 0;
            var queue = 0;
            foreach (var o in world.Units.Values)
            {
                if (o.OwnerId != u.OwnerId || o.Board is not { } s || s.Tile != tile || s.Sheltered) continue;
                if (s.At == cell) held = 1;
                if (s.At == outside) queue++;
            }
            var key = (shut, held, queue, lane);
            if (key.CompareTo(bestKey) < 0) { bestKey = key; best = lane; }
        }
        return best;
    }

    // An order or an arrival wakes a suspended board: it resolves on the next beat.
    public static void Wake(Simulation sim, Battlefield bf)
    {
        if (!bf.Suspended) return;
        bf.Suspended = false;
        bf.NextTurnTick = NextBeat(sim.World, sim.Now);
        bf.NextTurnSeq = sim.Schedule(bf.NextTurnTick, new BattlefieldTurnEvent(bf.Tile));
    }

    // ---- a world move given to a unit on a board --------------------------------

    // MoveIntent.BeginMove on a unit that has a board place (a player's world
    // order, a haul re-issued by the queue, a driver's march): the path is
    // committed but NOT walked. On the board it becomes a move to the edge
    // facing the path's first hop, and the unit walks on once it has left
    // (§4.1: "a move whose next step crosses the edge leaves this way and
    // keeps its order").
    internal static void DeferWorldMove(Simulation sim, Unit u, List<TileCoord>? path, TileCoord finalDest)
    {
        var slot = u.Board!;
        u.NextArrivalTick = null;
        u.NextArrivalSeq = null;
        if (path is null || path.Count < 2)
        {
            u.PathRemaining = null;
            u.PathFinalDest = null;
            return;
        }
        u.PathRemaining = path.Skip(1).ToList();
        u.PathFinalDest = finalDest;
        if (EdgeToward(slot.Tile, u.PathRemaining[0]) is { } edge)
        {
            var from = slot.OnBoard ? slot.At : new Subtile(1, 1);
            slot.Order = BattleOrder.MoveTo(OutsideNearest(from, edge));
        }
        if (sim.World.Battlefields.TryGetValue(slot.Tile, out var bf)) Wake(sim, bf);
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
            if (MovementCost.ExecutionCost(world, tile, n, sim.Now, Traversal.Foot) >= Sim.Core.World.Biomes.Impassable) continue;
            exits.Add(h);
        }
        return exits;
    }

    internal static void RunTurn(Simulation sim, Battlefield bf)
    {
        var world = sim.World;
        var tile = bf.Tile;
        var cfg = world.CombatConfig.Battle;

        // Everyone on the tile has a place; a place without its unit is dropped.
        var present = Present(world, tile);
        foreach (var u in present.Where(u => u.Board is null || u.Board.Tile != tile))
        {
            StopWorldHop(sim, u);
            u.Board = new BoardSlot(tile, default, sheltered: true);
        }
        var slotted = world.Units.Values.Where(u => u.Board is { } s && s.Tile == tile).ToList();
        foreach (var u in slotted)
            if (u.Position != tile || u.IsEmbarked) u.Board = null;
        slotted = slotted.Where(u => u.Board is not null).ToList();

        // The sheltered come on into free subtiles, centre first (D1).
        var placedShelter = false;
        var layer = LayerFor(world, tile);
        var taken = new HashSet<(int, Subtile)>(slotted.Where(u => u.Board!.OnBoard).Select(u => (u.OwnerId, u.Board!.At)));
        foreach (var u in slotted.Where(u => u.Board!.Sheltered))
            if (FirstFree(taken, u.OwnerId, Reachable(world, layer, tile, u, Standable(world, layer, u, CentreFirstOrder))) is { } c)
            {
                taken.Add((u.OwnerId, c));
                u.Board!.Sheltered = false;
                u.Board.At = c;
                u.Board.CameFrom = null;
                placedShelter = true;
            }

        // A unit that carries a world path and no board order heads for the
        // edge that path leaves by.
        foreach (var u in slotted)
            if (u.Board!.OnBoard && u.Board.Order is null && u.PathRemaining is { Count: > 0 } p
                && EdgeToward(tile, p[0]) is { } edge)
                u.Board.Order = BattleOrder.MoveTo(OutsideNearest(u.Board.At, edge));

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
        var steps = TurnPlanner.PlanAll(board, orders, doctrines, new BoardSurroundings(Exits(sim, tile), enteredFrom));

        bf.TurnNumber++;
        if (!TurnResolver.HasWork(board, steps) && !placedShelter)
        {
            if (!StillContested(world, tile)) { Close(sim, bf); return; }
            bf.Suspended = true;
            bf.LastTurn = null;
            return;
        }

        var owners = slotted.ToDictionary(u => u.Id, u => u.OwnerId);
        var result = TurnResolver.Resolve(board, steps, cfg);
        Apply(sim, bf, slotted, steps, result);
        bf.LastTurn = new BattleTurnRecord(sim.Now, bf.TurnNumber, result, owners);

        if (!StillContested(world, tile)) { Close(sim, bf); return; }
        bf.NextTurnTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        bf.NextTurnSeq = sim.Schedule(bf.NextTurnTick, new BattlefieldTurnEvent(tile));
    }

    // The board as the resolver sees it, read from the real units.
    public static BoardState ReadBoard(Simulation sim, TileCoord tile, IEnumerable<Unit> slotted)
    {
        var world = sim.World;
        var units = new List<BoardUnit>();
        foreach (var u in slotted)
        {
            var s = u.Board!;
            if (s.Sheltered) continue;
            units.Add(new BoardUnit(u.Id, u.OwnerId, s.At, u.Health,
                CombatRules.EffectivePower(world, u, sim.Now),
                UnitCombatCatalog.Spec(u.Role).Ranged) { CameFrom = s.CameFrom });
        }
        return new BoardState(LayerFor(world, tile), units, world.Diplomacy.AreHostile,
            (a, b) => Fortification.IsOwnOrAllied(world, a, b));
    }

    // What stands on the board: the tile's structure's footprint, set by its
    // facing and, for a wall line, the neighbours it joins
    // (docs/structure-footprints.md); open ground where there is none. The one
    // place terrain and cover will fill subtiles later.
    public static SubtileLayer LayerFor(GameWorld world, TileCoord tile) => Footprints.For(world, tile);

    private static void Apply(Simulation sim, Battlefield bf, List<Unit> slotted, Dictionary<int, PlannedStep> steps, TurnResult r)
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
            movedIds.Add(u.Id);
            if (m.Left) { Leave(sim, u, bf, m.To.EdgeBeyond!.Value); continue; }
            u.Board!.At = m.To;
            u.Board.CameFrom = m.From;
        }

        foreach (var u in slotted)
        {
            if (u.Board is not { } slot || slot.Tile != bf.Tile) continue;
            if (slot.Order is { } o) slot.Order = o.After(slot.At, movedIds.Contains(u.Id));
            if (r.After.Get(u.Id) is { } after) u.Health = after.Hp;
        }

        foreach (var id in r.Deaths)
        {
            if (!world.Units.TryGetValue(id, out var dead)) continue;
            dead.Health = 0;
            dead.Board = null;
            CombatRules.OnUnitDeath(sim, dead);
        }
    }

    // Off the board on the beat, then a normal world hop onto the neighbour
    // (§4.1). A unit already committed to a path through that edge walks it on.
    private static void Leave(Simulation sim, Unit u, Battlefield bf, Heading edge)
    {
        var next = Across(bf.Tile, edge);
        u.Board = null;
        u.LeavingBoard = bf.Tile;
        if (u.PathRemaining is not { Count: > 0 } path || path[0] != next || u.PathFinalDest is null)
        {
            u.PathRemaining = new List<TileCoord> { next };
            u.PathFinalDest = next;
        }
        MoveIntent.ScheduleNextHop(sim, u);
        if (u.NextArrivalSeq is null)
        {
            // The hop was refused (a wall went up): it stays on the tile.
            u.LeavingBoard = null;
            u.Board = new BoardSlot(bf.Tile, default, sheltered: true);
        }
    }

    public static bool StillContested(GameWorld world, TileCoord tile)
    {
        var fighting = world.Units.Values.Where(u => u.Board is { } s && s.Tile == tile && !s.Waiting);
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
            if (u.PathRemaining is { Count: > 0 } && u.PathFinalDest is not null) resume.Add(u);
        }
        // The route each was on walks on; any errand without a route resumes.
        foreach (var u in resume) MoveIntent.ScheduleNextHop(sim, u);
        CombatRules.ResumeInterrupted(sim, bf.Tile);
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
        var slotted = world.Units.Values.Where(u => u.Board is { } s && s.Tile == bf.Tile).ToList();
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
        return TurnPlanner.PlanAll(board, orders, doctrines, new BoardSurroundings(Exits(sim, bf.Tile), enteredFrom));
    }
}
