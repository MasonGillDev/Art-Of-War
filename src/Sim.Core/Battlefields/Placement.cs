using Sim.Core.Movement;

namespace Sim.Core.Battlefields;

// M42 phase 1 — every unit stands on a subtile (docs/subtile-movement.md).
// THE one writer of Unit.Subtile. Only enemies share a subtile: a subtile holds at most one unit
// of any non-hostile owner, and two hostile units on one is a duel.
//
// This file is the DIRECT placements, the ones that never involve walking:
//   * Seat    — a unit that was created (genesis, a birth, a spawn, a landing, a boat's passenger stepping off).
//               Centre rows first, then the outer rows, lowest lane first; open
//               ground before walls, gates and towers; only on USABLE subtiles
//               (the unit may stand there and can reach it from some edge);
//               a subtile nobody holds before one held only by enemies.
//   * Reseat  — the ground changed under standing units (a structure was added:
//               a completed building, a wall going up): each unit that may no
//               longer stand where it is is popped to the nearest open subtile.
//   * SeparateNonHostile — two owners stopped being hostile (peace) while their
//               units shared a subtile: the lowest id stays, the rest are popped.
//
// A unit with no room (a tile over its cap, a pooled-era test that piles units
// on one tile) simply has no subtile (null); it is never placed on a friend.
public static class Placement
{
    // ---- created units ------------------------------------------------------

    // Give `u` a subtile on the tile it stands on (or clear it when it is
    // aboard a boat).
    public static void Seat(GameWorld world, Unit u)
    {
        u.Subtile = u.IsEmbarked ? null : Choose(world, u, u.Position);
    }

    private static Subtile? Choose(GameWorld world, Unit u, TileCoord tile)
    {
        // A hull sails: a footprint doesn't shape its water, so it takes the first centre-out
        // subtile no other unit holds (or the first, when they all are).
        if (u.Traversal == Traversal.Water)
        {
            var heldByOthers = Holders(world, tile, u);
            foreach (var c in Battlefields.CentreFirstOrder)
                if (!heldByOthers.ContainsKey(c)) return c;
            return Battlefields.CentreFirstOrder[0];
        }
        var layer = Battlefields.LayerFor(world, tile);
        var mover = Battlefields.MoverFor(world, layer, u);
        var held = Holders(world, tile, u);
        Subtile? shared = null;
        foreach (var c in Ordered(layer, Usable(layer, mover)))
        {
            if (!held.TryGetValue(c, out var list)) return c;
            if (shared is null && list.All(o => world.Diplomacy.AreHostile(u.OwnerId, o.OwnerId))) shared = c;
        }
        return shared;
    }

    // ---- the ground changed -----------------------------------------------------

    // Pop every unit on `tile` that may no longer stand where it is. Units go in
    // ascending id order, each taking the nearest free subtile as things stand.
    public static void Reseat(GameWorld world, TileCoord tile)
    {
        var here = world.Units.Values
            .Where(u => u.Position == tile && u.Subtile is not null && !u.IsEmbarked && u.Traversal != Traversal.Water)
            .OrderBy(u => u.Id)
            .ToList();
        if (here.Count == 0) return;
        var layer = Battlefields.LayerFor(world, tile);
        foreach (var u in here)
        {
            var mover = Battlefields.MoverFor(world, layer, u);
            var at = u.Subtile!.Value;
            // A wall, tower or gate that rose under a unit not on the structure's side
            // is not something it climbed (2026-10-01: anyone may stand on a wall it
            // reached through an open side, so Usable alone would keep it there): it
            // is popped off, and onto open ground.
            var roseUnderIt = !mover.Friendly && IsFortified(layer, at);
            if (!roseUnderIt && Usable(layer, mover).Contains(at)) continue;
            Pop(world, u, layer, mover, roseUnderIt ? s => !IsFortified(layer, s) : null);
        }
    }

    private static bool IsFortified(SubtileLayer layer, Subtile s) =>
        layer.KindAt(s) is SubtileKind.Wall or SubtileKind.Tower or SubtileKind.Gate;

    // Two owners are no longer hostile: units of theirs that share a subtile
    // are separated. Deterministic: tiles in (y, x) order, subtiles in index
    // order, the lowest unit id in a shared subtile stays.
    public static void SeparateNonHostile(GameWorld world)
    {
        var crowded = world.Units.Values
            .Where(u => u.Subtile is not null && !u.IsEmbarked)
            .GroupBy(u => (u.Position, At: u.Subtile!.Value))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Position.Y).ThenBy(g => g.Key.Position.X).ThenBy(g => g.Key.At.Index)
            .ToList();
        foreach (var g in crowded)
        {
            var kept = new List<Unit>();
            foreach (var u in g.OrderBy(u => u.Id))
            {
                if (kept.All(k => world.Diplomacy.AreHostile(k.OwnerId, u.OwnerId))) { kept.Add(u); continue; }
                var layer = Battlefields.LayerFor(world, u.Position);
                Pop(world, u, layer, Battlefields.MoverFor(world, layer, u));
            }
        }
    }

    // A board is opening (or units have joined an open one) on `tile`: friends who were WALKING
    // through each other share subtiles legally, but on a board only enemies share. The lowest
    // id on a shared subtile stays; the rest are popped to the nearest subtile nobody holds.
    public static void SeparateOn(GameWorld world, TileCoord tile)
    {
        var crowded = world.Units.Values
            .Where(u => u.Position == tile && u.Subtile is not null && !u.IsEmbarked)
            .GroupBy(u => u.Subtile!.Value)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Index)
            .ToList();
        if (crowded.Count == 0) return;
        var layer = Battlefields.LayerFor(world, tile);
        foreach (var g in crowded)
        {
            var kept = new List<Unit>();
            foreach (var u in g.OrderBy(u => u.Id))
            {
                if (kept.All(k => world.Diplomacy.AreHostile(k.OwnerId, u.OwnerId))) { kept.Add(u); continue; }
                Pop(world, u, layer, Battlefields.MoverFor(world, layer, u));
            }
        }
    }

    // Move `u` to the nearest subtile nobody holds, by walking distance over
    // subtiles it may enter (not straight-line, so never through a wall),
    // ties in the breadth-first order N, E, S, W. Never onto a held subtile
    // (that would force a duel). `landOn` narrows where it may end up (the walk
    // there may still cross other subtiles). No free subtile on the tile: the
    // nearest tile with room, as a birth does, and seated there.
    private static void Pop(GameWorld world, Unit u, SubtileLayer layer, BoardMover mover, Func<Subtile, bool>? landOn = null)
    {
        var tile = u.Position;
        var start = u.Subtile!.Value;
        var usable = Usable(layer, mover);
        var held = Holders(world, tile, u);

        var seen = new HashSet<Subtile> { start };
        var queue = new Queue<Subtile>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var h in Headings.All)
            {
                var next = at.Step(h);
                if (!next.IsOnBoard || !usable.Contains(next) || seen.Contains(next)) continue;
                if (layer.IsClosed(next, h.Opposite()) || (usable.Contains(at) && layer.IsClosed(at, h))) continue;
                seen.Add(next);
                if (!held.ContainsKey(next) && (landOn is null || landOn(next))) { u.Subtile = next; return; }
                queue.Enqueue(next);
            }
        }

        // No free subtile here (enemies may hold the rest): it takes the best one left, even
        // one shared with an enemy (better a duel than a lost unit), else keeps the subtile it
        // has. A pop NEVER changes a unit's tile (M43, docs/fix-combat-m43.md): a move to
        // another tile would be a jump the client can only draw as a teleport. Things that are
        // CREATED (a birth, a refugee, a landing) still go to the nearest tile with room
        // (TileCapacity.RoomNear): nothing is animating there.
        if (Choose(world, u, tile) is { } best) u.Subtile = best;
    }

    // ---- shared helpers ---------------------------------------------------------

    // The subtiles `mover` may stand on and can reach from some edge: a unit
    // is never placed in a walled-in pocket.
    public static HashSet<Subtile> Usable(SubtileLayer layer, BoardMover mover)
    {
        var set = new HashSet<Subtile>();
        foreach (var h in Headings.All) set.UnionWith(BattlePathing.ReachableFrom(layer, mover, h));
        return set;
    }

    // Centre rows first, lowest lane first; open ground before walls, gates
    // and towers (the sort is stable, so the centre-first order holds within
    // each group).
    private static IEnumerable<Subtile> Ordered(SubtileLayer layer, HashSet<Subtile> usable) =>
        Battlefields.CentreFirstOrder.Where(usable.Contains)
            .OrderBy(c => layer.KindAt(c) is SubtileKind.Open or SubtileKind.Cover ? 0 : 1);

    // The subtiles of `tile` anyone but `except` holds.
    public static HashSet<Subtile> HeldSubtiles(GameWorld world, TileCoord tile, Unit except) =>
        Holders(world, tile, except).Keys.ToHashSet();

    // Who holds each subtile of `tile`, other than `except`.
    private static Dictionary<Subtile, List<Unit>> Holders(GameWorld world, TileCoord tile, Unit except)
    {
        var held = new Dictionary<Subtile, List<Unit>>();
        foreach (var o in world.Units.Values)
        {
            if (o == except || o.Position != tile || o.Subtile is not { } at || o.IsEmbarked) continue;
            if (!held.TryGetValue(at, out var list)) held[at] = list = new List<Unit>();
            list.Add(o);
        }
        return held;
    }
}
