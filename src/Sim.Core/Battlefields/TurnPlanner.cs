namespace Sim.Core.Battlefields;

// Why a unit will not (or did not) step this turn. Shown to the owner next to
// the unit (docs/battlefield-grid.md §10: "if its last move failed, why").
public enum StepNote : byte
{
    None = 0,
    Holding = 1,             // a Hold order
    SwapPartnerGone = 2,     // the swap partner isn't an adjacent ally any more
    CannotPassOpponent = 3,  // no passing through (§3)
    NoWay = 4,               // nothing reachable (blocked subtiles)
    CannotLeaveThere = 5,    // the edge leads nowhere a unit can go
    AllyInTheWay = 6,        // resolver: a friend ends the turn there
    Clashed = 7,             // resolver: charged an enemy that charged back
}

// One unit's step for a turn: the subtile it will try (null = stay). A step
// just outside an edge is leaving the board across it.
public readonly record struct PlannedStep(Subtile? To, StepNote Note = StepNote.None);

// M41 — what the world tells the planner about the board's surroundings.
//   Exits        — the edges a unit may leave across (a neighbour tile it can
//                  walk onto). v1 worlds: every edge with a land neighbour.
//   EnteredFrom  — per unit, the edge it came in by (Withdraw goes back out
//                  that way); absent for units that never crossed in.
public sealed record BoardSurroundings(IReadOnlySet<Heading> Exits, IReadOnlyDictionary<int, Heading> EnteredFrom,
    IReadOnlyDictionary<Heading, IReadOnlySet<int>>? OwnersBeyond = null)
{
    public static readonly BoardSurroundings Anywhere =
        new(new HashSet<Heading>(Headings.All), new Dictionary<int, Heading>());
}

// M41 — turns standing orders and doctrine into this turn's steps, from the
// positions at the start of the turn (§5, §7). Pure: reads the board, returns
// steps. The resolver then decides what actually happens.
public static class TurnPlanner
{
    public static Dictionary<int, PlannedStep> PlanAll(
        BoardState board,
        IReadOnlyDictionary<int, BattleOrder> orders,
        IReadOnlyDictionary<int, BattleDoctrine> doctrines,
        BoardSurroundings around)
    {
        var steps = new Dictionary<int, PlannedStep>();
        foreach (var u in board.Units)
        {
            orders.TryGetValue(u.Id, out var order);
            var doctrine = doctrines.TryGetValue(u.Id, out var d) ? d : BattleDoctrine.Hold;
            steps[u.Id] = Plan(board, u, order, doctrine, around);
        }

        // A swap is one order for both units: the partner steps the other way
        // whatever its own order said (§3, "It replaces both units' orders").
        foreach (var u in board.Units)
        {
            if (!orders.TryGetValue(u.Id, out var o) || o.Kind != BattleOrderKind.Swap) continue;
            if (steps[u.Id].To is null) continue;
            var partner = board.Get(o.SwapWith)!;
            steps[partner.Id] = new PlannedStep(u.At);
        }
        return steps;
    }

    public static PlannedStep Plan(BoardState board, BoardUnit u, BattleOrder? order, BattleDoctrine doctrine,
        BoardSurroundings around)
    {
        // Waiting just outside an edge: come on in that lane.
        if (!u.OnBoard)
        {
            var edge = u.At.EdgeBeyond ?? throw new InvalidOperationException($"Unit {u.Id} is lost at {u.At}.");
            var inside = u.At.Step(edge.Opposite());
            return board.CanStep(u, u.At, inside) ? new PlannedStep(inside) : new PlannedStep(null, StepNote.NoWay);
        }

        if (order is not null)
        {
            switch (order.Kind)
            {
                case BattleOrderKind.Hold:
                    return new PlannedStep(null, StepNote.Holding);
                case BattleOrderKind.Swap:
                    var partner = board.Get(order.SwapWith);
                    if (partner is null || !partner.OnBoard || partner.OwnerId != u.OwnerId || !partner.At.IsAdjacentTo(u.At))
                        return new PlannedStep(null, StepNote.SwapPartnerGone);
                    if (!board.CanStep(u, u.At, partner.At) || !board.CanStep(partner, partner.At, u.At))
                        return new PlannedStep(null, StepNote.NoWay);
                    return new PlannedStep(partner.At);
                case BattleOrderKind.Withdraw:
                    return Withdraw(board, u, around);
                case BattleOrderKind.MoveTo:
                    return Toward(board, u, order.Destination, around);
                case BattleOrderKind.Route:
                    return Toward(board, u, order.Waypoints[order.NextWaypoint], around);
            }
        }
        return Doctrine(board, u, doctrine, around);
    }

    private static PlannedStep Doctrine(BoardState board, BoardUnit u, BattleDoctrine doctrine, BoardSurroundings around)
    {
        if (doctrine.WithdrawBelow > 0 && board.OnBoard.Count(o => o.OwnerId == u.OwnerId) < doctrine.WithdrawBelow)
            return Withdraw(board, u, around);

        switch (doctrine.Behaviour)
        {
            case DoctrineBehaviour.Withdraw:
                return Withdraw(board, u, around);

            case DoctrineBehaviour.Advance:
            {
                if (board.InDuel(u)) return default;
                var foe = board.OnBoard
                    .Where(o => board.AreHostile(u.OwnerId, o.OwnerId) && board.CanStand(u, o.At))
                    .OrderBy(o => Subtile.Manhattan(u.At, o.At)).ThenBy(o => o.Id)
                    .FirstOrDefault();
                if (foe is null) return default;
                return Checked(board, u, AroundFriends(board, u, s => s == foe.At));
            }

            case DoctrineBehaviour.Support:
            {
                if (board.InDuel(u)) return default;
                var friend = board.OnBoard
                    .Where(f => f.OwnerId == u.OwnerId && f.Id != u.Id && board.InDuel(f))
                    .OrderBy(f => Subtile.Manhattan(u.At, f.At)).ThenBy(f => f.Id)
                    .FirstOrDefault();
                if (friend is null || u.At.IsAdjacentTo(friend.At)) return default;
                var step = AroundFriends(board, u, s => s.IsAdjacentTo(friend.At) && board.IsEmpty(s));
                return Checked(board, u, step);
            }

            default:
                return default;
        }
    }

    // Doctrine's pathing: round the unit's own friends where a way round
    // exists, so a unit acting on its own doesn't walk into the back of its
    // own line every turn. Falls back to the plain rule (units ignored, §3)
    // when friends close every way. Orders always use the plain rule.
    private static Subtile? AroundFriends(BoardState board, BoardUnit u, Func<Subtile, bool> goal)
    {
        var friends = new HashSet<Subtile>(board.OnBoard.Where(f => f.OwnerId == u.OwnerId && f.Id != u.Id).Select(f => f.At));
        var mover = board.MoverOf(u);
        var detour = BattlePathing.FirstStep(board.Layer, mover, u.At, goal, s => !friends.Contains(s) || goal(s));
        return detour ?? BattlePathing.FirstStep(board.Layer, mover, u.At, goal);
    }

    // One step toward `target`. A target just outside an edge is left across
    // once the unit stands next to it.
    private static PlannedStep Toward(BoardState board, BoardUnit u, Subtile target, BoardSurroundings around)
    {
        if (u.At == target) return default;
        var mover = board.MoverOf(u);
        if (!target.IsOnBoard)
        {
            var edge = target.EdgeBeyond!.Value;
            if (!around.Exits.Contains(edge)) return new PlannedStep(null, StepNote.CannotLeaveThere);
            var inside = target.Step(edge.Opposite());
            if (!board.Layer.CanStep(inside, target, mover)) return new PlannedStep(null, StepNote.CannotLeaveThere);
            if (u.At.IsAdjacentTo(target)) return new PlannedStep(target);
            var toEdge = BattlePathing.FirstStepTo(board.Layer, mover, u.At, inside);
            return toEdge is null ? new PlannedStep(null, StepNote.NoWay) : Checked(board, u, toEdge);
        }
        var next = board.Layer.CanStep(u.At, target, mover)
            ? target
            : BattlePathing.FirstStepTo(board.Layer, mover, u.At, target);
        return next is null ? new PlannedStep(null, StepNote.NoWay) : Checked(board, u, next);
    }

    // §4.2: back out the way it came, or the nearest edge for a unit that
    // never crossed in — preferring an edge with no enemy on its row, then
    // the nearest, then N, E, S, W.
    private static PlannedStep Withdraw(BoardState board, BoardUnit u, BoardSurroundings around)
    {
        var edge = WithdrawEdge(board, u, around);
        if (edge is null) return new PlannedStep(null, StepNote.CannotLeaveThere);
        var h = edge.Value;
        var mover = board.MoverOf(u);
        if (u.At.IsOnEdgeRow(h) && board.Layer.CanStep(u.At, u.At.Step(h), mover)) return new PlannedStep(u.At.Step(h));
        var step = BattlePathing.FirstStep(board.Layer, mover, u.At, s => s.IsOnEdgeRow(h) && board.Layer.CanStep(s, s.Step(h), mover));
        return step is null ? new PlannedStep(null, StepNote.NoWay) : Checked(board, u, step);
    }

    // M43 (docs/fix-combat-m43.md): an edge whose neighbour tile holds a unit hostile to this one
    // is no way out: a withdrawing unit that "backed out" into the enemy only opened a fight on the
    // next tile and, backing out of that one, bounced between two tiles for ever. If every way
    // out leads to an enemy it stays (CannotLeaveThere) and the board carries on.
    public static Heading? WithdrawEdge(BoardState board, BoardUnit u, BoardSurroundings around)
    {
        var mover = board.MoverOf(u);
        bool CanGetOut(Heading h) => Subtile.All().Any(s => s.IsOnEdgeRow(h) && board.Layer.CanStep(s, s.Step(h), mover));
        bool EnemyBeyond(Heading h) => around.OwnersBeyond is { } beyond && beyond.TryGetValue(h, out var owners)
            && owners.Any(o => board.AreHostile(u.OwnerId, o));
        if (around.EnteredFrom.TryGetValue(u.Id, out var came) && around.Exits.Contains(came) && CanGetOut(came) && !EnemyBeyond(came)) return came;
        Heading? best = null;
        (int Enemy, int Dist) bestKey = default;
        foreach (var h in Headings.All)
        {
            if (!around.Exits.Contains(h) || !CanGetOut(h) || EnemyBeyond(h)) continue;
            var enemyOnRow = board.OnBoard.Any(o => o.At.IsOnEdgeRow(h) && board.AreHostile(u.OwnerId, o.OwnerId)) ? 1 : 0;
            var key = (enemyOnRow, u.At.DistanceToEdge(h));
            if (best is null || key.CompareTo(bestKey) < 0) { best = h; bestKey = key; }
        }
        return best;
    }

    // §3 "no passing through": a unit leaving a duel may not step onto the
    // subtile its opponent came from, nor carry straight on in the direction
    // it entered the duel's subtile. It can back off or slide sideways.
    private static PlannedStep Checked(BoardState board, BoardUnit u, Subtile? next)
    {
        if (next is null) return default;
        if (IsPassingThrough(board, u, next.Value)) return new PlannedStep(null, StepNote.CannotPassOpponent);
        return new PlannedStep(next);
    }

    public static bool IsPassingThrough(BoardState board, BoardUnit u, Subtile next)
    {
        var opp = board.OpponentOf(u);
        if (opp is null) return false;
        if (opp.CameFrom is { } oppFrom && next == oppFrom) return true;
        if (u.CameFrom is { } from)
        {
            var ahead = new Subtile(2 * u.At.X - from.X, 2 * u.At.Y - from.Y);
            if (next == ahead) return true;
        }
        return false;
    }
}
