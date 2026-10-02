using Sim.Core.Battlefields;
using Sim.Core.World;

namespace Sim.Core.Groups;

// M46 Phase B — where each member of a group stands when the group stops around an
// anchor tile (docs/m46-groups-spec.md, "Formation layout"). A muster, a halt, a
// merge and a landing all form up through here.
//
// THE BLOCK SPILLS. A tile holds 16 of a side, fewer on a structure; an army doesn't
// fit on one. Places are filled outward from the anchor tile's centre, nearest first,
// so the anchor tile fills before any neighbour and the block grows ring by ring onto
// the tiles around it. Only tiles the group could walk to from the anchor are used
// (a lake or a wall line bounds the block), out to MaxRadius tiles.
//
// NOBODY IS DROPPED SILENTLY. A member with no place gets null, and the caller says so
// (the muster reports it). The pre-M46 group move just left such members behind.
//
// Pure read: planned as the owner sees the world (`visible`, the same fog contract as
// Walk.Begin), writing nothing.
public static class FormationLayout
{
    // How far, in tiles, the block may spill from the anchor: room for 17 × 17 tiles.
    public const int MaxRadius = 8;

    // The order members take places in: by role, then id. The same group always forms
    // up the same way, and like stands with like.
    public static List<Unit> FillOrder(IEnumerable<Unit> members) =>
        members.OrderBy(u => (byte)u.Role).ThenBy(u => u.Id).ToList();

    // A place for each member, in the order given (see FillOrder), or null where there
    // is no room within reach.
    public static List<(Unit Member, WorldSubtile? Place)> Places(
        GameWorld world, int owner, TileCoord anchor, IReadOnlyList<Unit> members, HashSet<TileCoord>? visible)
    {
        var result = new List<(Unit, WorldSubtile?)>(members.Count);
        if (members.Count == 0) return result;

        var footRules = new SubtileStepRules(world, new StepMover(owner, Traversal.Foot, false), visible);
        var candidates = Candidates(world, footRules, anchor);

        // Taken: a subtile someone outside the group stands on (on a board only enemies
        // share one, Walk.HeldByStanding). Members standing in the area don't count:
        // they are the ones moving into the block.
        var memberIds = new HashSet<int>(members.Select(m => m.Id));
        var taken = new HashSet<WorldSubtile>();
        foreach (var o in world.Units.Values)
        {
            if (memberIds.Contains(o.Id) || o.IsEmbarked || o.IsWalking || o.Subtile is not { } sub) continue;
            if (world.Diplomacy.AreHostile(owner, o.OwnerId)) continue;
            taken.Add(WorldSubtile.Of(o.Position, sub));
        }

        // A member places by its own step rules (an archer may stand on a tower's
        // subtile, a soldier may not).
        var rulesByMover = new Dictionary<StepMover, SubtileStepRules>();
        var next = 0;   // every candidate before this one is taken or refused by everyone so far
        foreach (var m in members)
        {
            var mover = StepMover.Of(m) with { Owner = owner };
            if (!rulesByMover.TryGetValue(mover, out var rules))
                rulesByMover[mover] = rules = new SubtileStepRules(world, mover, visible);
            WorldSubtile? place = null;
            for (var i = next; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (taken.Contains(c) || !rules.CanStand(c)) continue;
                place = c;
                taken.Add(c);
                break;
            }
            while (next < candidates.Count && taken.Contains(candidates[next])) next++;
            result.Add((m, place));
        }
        return result;
    }

    // Every subtile a foot unit may stand on, on the tiles connected to the anchor within
    // MaxRadius, nearest the anchor's centre first (ties by row, then column).
    private static List<WorldSubtile> Candidates(GameWorld world, SubtileStepRules rules, TileCoord anchor)
    {
        var list = new List<WorldSubtile>();
        if (!world.Grid.InBounds(anchor) || !AnyStand(rules, anchor)) return list;

        var region = new HashSet<TileCoord> { anchor };
        var frontier = new Queue<TileCoord>();
        frontier.Enqueue(anchor);
        while (frontier.Count > 0)
        {
            var t = frontier.Dequeue();
            foreach (var (dx, dy) in Around)
            {
                var n = new TileCoord(t.X + dx, t.Y + dy);
                if (Math.Max(Math.Abs(n.X - anchor.X), Math.Abs(n.Y - anchor.Y)) > MaxRadius) continue;
                if (region.Contains(n) || !world.Grid.InBounds(n)) continue;
                if (!CanCross(rules, t, n)) continue;
                region.Add(n);
                frontier.Enqueue(n);
            }
        }

        foreach (var t in region)
            foreach (var s in Subtile.All())
            {
                var at = WorldSubtile.Of(t, s);
                if (rules.CanStand(at)) list.Add(at);
            }

        // Distance from the anchor tile's centre, in doubled subtile units (integers).
        var cx = anchor.X * Subtile.Size * 2 + Subtile.Size;
        var cy = anchor.Y * Subtile.Size * 2 + Subtile.Size;
        long D(WorldSubtile w)
        {
            long dx = 2 * w.X + 1 - cx, dy = 2 * w.Y + 1 - cy;
            return dx * dx + dy * dy;
        }
        list.Sort((a, b) =>
        {
            var c = D(a).CompareTo(D(b));
            if (c != 0) return c;
            c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.X.CompareTo(b.X);
        });
        return list;
    }

    private static bool AnyStand(SubtileStepRules rules, TileCoord tile)
    {
        foreach (var s in Subtile.All())
            if (rules.CanStand(WorldSubtile.Of(tile, s))) return true;
        return false;
    }

    // Can a foot unit step from tile `a` into the 4-adjacent tile `b` anywhere along their
    // shared edge (a river ford, a gate, open ground)?
    private static bool CanCross(SubtileStepRules rules, TileCoord a, TileCoord b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        for (var i = 0; i < Subtile.Size; i++)
        {
            // The pair of subtiles facing each other across the edge.
            var from = dx != 0
                ? new WorldSubtile(a.X * Subtile.Size + (dx > 0 ? Subtile.Size - 1 : 0), a.Y * Subtile.Size + i)
                : new WorldSubtile(a.X * Subtile.Size + i, a.Y * Subtile.Size + (dy > 0 ? Subtile.Size - 1 : 0));
            var to = new WorldSubtile(from.X + dx, from.Y + dy);
            if (rules.CanStand(from) && rules.CanStand(to) && rules.Problem(from, to) is null) return true;
        }
        return false;
    }

    private static readonly (int Dx, int Dy)[] Around = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // N, E, S, W
}
