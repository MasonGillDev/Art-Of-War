namespace Sim.Core.Battlefields;

// M41 — shortest orthogonal paths on the board (docs/battlefield-grid.md §3).
// Breadth-first over the steps the mover may take (SubtileLayer.CanStep: who
// may stand where, and closed sides), expanding neighbours N, E, S, W, so
// among equally short paths the one that turns north first wins: "ties broken
// N, E, S, W". Units are ignored when pathing (a path runs through friends
// and enemies alike; collisions are the resolver's business, a turn at a
// time).
public static class BattlePathing
{
    // The first step of a shortest path from `from` to the nearest subtile
    // satisfying `goal`, or null if `from` is already a goal or none is
    // reachable. Goals must be on the board.
    public static Subtile? FirstStep(SubtileLayer layer, BoardMover mover, Subtile from, Func<Subtile, bool> goal) =>
        FirstStep(layer, mover, from, goal, _ => true);

    // As above, through only the subtiles `open` allows (as well as the layer).
    public static Subtile? FirstStep(SubtileLayer layer, BoardMover mover, Subtile from, Func<Subtile, bool> goal,
        Func<Subtile, bool> open)
    {
        if (!from.IsOnBoard || goal(from)) return null;

        var first = new Dictionary<Subtile, Subtile>();   // subtile → first step taken to reach it
        var queue = new Queue<Subtile>();
        var seen = new HashSet<Subtile> { from };
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var h in Headings.All)
            {
                var next = at.Step(h);
                if (!next.IsOnBoard || !layer.CanStep(at, next, mover) || !open(next) || !seen.Add(next)) continue;
                var step = at == from ? next : first[at];
                first[next] = step;
                if (goal(next)) return step;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    public static Subtile? FirstStepTo(SubtileLayer layer, BoardMover mover, Subtile from, Subtile to) =>
        FirstStep(layer, mover, from, s => s == to);

    // Every on-board subtile the mover can reach coming on across edge `h`,
    // from any lane (units ignored). Where an arrival may be placed.
    public static HashSet<Subtile> ReachableFrom(SubtileLayer layer, BoardMover mover, Heading h)
    {
        var seen = new HashSet<Subtile>();
        var queue = new Queue<Subtile>();
        for (var lane = 0; lane < Subtile.Size; lane++)
        {
            var outside = Subtile.OutsideLane(h, lane);
            var inside = outside.Step(h.Opposite());
            if (layer.CanStep(outside, inside, mover) && seen.Add(inside)) queue.Enqueue(inside);
        }
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var d in Headings.All)
            {
                var next = at.Step(d);
                if (next.IsOnBoard && layer.CanStep(at, next, mover) && seen.Add(next)) queue.Enqueue(next);
            }
        }
        return seen;
    }
}
