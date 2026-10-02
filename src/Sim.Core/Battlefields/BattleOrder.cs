namespace Sim.Core.Battlefields;

// M41 — a unit's standing battle order (docs/battlefield-grid.md §5 "Orders").
// An order stands until it is done or replaced; a unit with none follows its
// doctrine. Stop is not an order: it is clearing the order (back to doctrine).
public enum BattleOrderKind : byte
{
    // Stay put, overriding doctrine.
    Hold = 1,

    // Go to a subtile by the shortest path, re-planned each turn. A
    // destination just outside an edge means "leave across that edge".
    MoveTo = 2,

    // A traced list of subtiles, followed exactly and never re-planned. A
    // unit knocked off it walks back to the next waypoint. A last waypoint
    // outside an edge leaves across it.
    Route = 3,

    // Trade subtiles with an adjacent ally this turn (into a duel: relief).
    // One order for both units; tried again next turn if it fails.
    Swap = 4,

    // Leave the battle back the way the unit came (its EnteredFrom edge), or
    // by the nearest edge for a unit that never crossed in.
    Withdraw = 5,
}

public sealed record BattleOrder
{
    public BattleOrderKind Kind { get; }
    public Subtile Destination { get; }
    public IReadOnlyList<Subtile> Waypoints { get; }
    public int NextWaypoint { get; }
    public int SwapWith { get; }

    private BattleOrder(BattleOrderKind kind, Subtile destination, IReadOnlyList<Subtile> waypoints,
        int nextWaypoint, int swapWith)
    {
        Kind = kind;
        Destination = destination;
        Waypoints = waypoints;
        NextWaypoint = nextWaypoint;
        SwapWith = swapWith;
    }

    public static BattleOrder Hold() => new(BattleOrderKind.Hold, default, Array.Empty<Subtile>(), 0, 0);

    public static BattleOrder MoveTo(Subtile destination)
    {
        if (!destination.IsOnBoard && destination.EdgeBeyond is null)
            throw new ArgumentException($"{destination} is neither on the board nor just outside an edge.");
        return new(BattleOrderKind.MoveTo, destination, Array.Empty<Subtile>(), 0, 0);
    }

    // Waypoints must be a contiguous orthogonal chain: each one step from the
    // one before. Only the last may be off the board (leaving).
    public static BattleOrder Route(IReadOnlyList<Subtile> waypoints)
    {
        if (waypoints.Count == 0) throw new ArgumentException("A route needs at least one waypoint.");
        for (var i = 0; i < waypoints.Count; i++)
        {
            var w = waypoints[i];
            var last = i == waypoints.Count - 1;
            if (!w.IsOnBoard && !(last && w.EdgeBeyond is not null))
                throw new ArgumentException($"Waypoint {w} is off the board (only the last may cross an edge).");
            if (i > 0 && !waypoints[i - 1].IsAdjacentTo(w))
                throw new ArgumentException($"Waypoints {waypoints[i - 1]} and {w} are not one step apart.");
        }
        return new(BattleOrderKind.Route, default, waypoints.ToArray(), 0, 0);
    }

    // Snapshot restore only: rebuild an order exactly as it was saved.
    internal static BattleOrder Restore(BattleOrderKind kind, Subtile destination, IReadOnlyList<Subtile> waypoints,
        int nextWaypoint, int swapWith) => new(kind, destination, waypoints, nextWaypoint, swapWith);

    public static BattleOrder Swap(int partnerId) =>
        new(BattleOrderKind.Swap, default, Array.Empty<Subtile>(), 0, partnerId);

    public static BattleOrder Withdraw() => new(BattleOrderKind.Withdraw, default, Array.Empty<Subtile>(), 0, 0);

    // The order that stands after a turn, given where the unit now is and
    // whether it moved. Null when the order is done (the unit returns to
    // doctrine). A unit that left the board has no board order any more; the
    // world carries on from there.
    public BattleOrder? After(Subtile now, bool moved)
    {
        switch (Kind)
        {
            case BattleOrderKind.MoveTo:
                return now == Destination ? null : this;
            case BattleOrderKind.Route:
                var next = NextWaypoint;
                // Arriving on the next waypoint (or a later one, after a swap
                // knocked it along) moves the route on past it.
                for (var i = Waypoints.Count - 1; i >= next; i--)
                    if (Waypoints[i] == now) { next = i + 1; break; }
                if (next >= Waypoints.Count) return null;
                return next == NextWaypoint ? this : new(Kind, default, Waypoints, next, 0);
            case BattleOrderKind.Swap:
                return moved ? null : this;
            default:
                return this;   // Hold and Withdraw stand until replaced (or the unit leaves)
        }
    }
}

// M41 — what a unit does with no order (§7). Worked out from the positions at
// the start of each turn. Inert state set by intents; it never picks who
// fights, only how.
public enum DoctrineBehaviour : byte
{
    // Stay on the current subtile; fight whatever enters.
    Hold = 0,

    // Move toward the nearest enemy.
    Advance = 1,

    // Move next to the nearest friend in a duel; otherwise hold (archers).
    Support = 2,

    // Leave the battle (the default for non-combatants, decision D6).
    Withdraw = 3,
}

// WithdrawBelow: "withdraw at threshold" — when fewer than this many of the
// unit's own owner remain on the board, it withdraws whatever its behaviour.
// 0 = never.
public readonly record struct BattleDoctrine(DoctrineBehaviour Behaviour, int WithdrawBelow = 0)
{
    public static BattleDoctrine Hold => new(DoctrineBehaviour.Hold);
    public static BattleDoctrine Advance => new(DoctrineBehaviour.Advance);
    public static BattleDoctrine Support => new(DoctrineBehaviour.Support);
    public static BattleDoctrine Withdraw => new(DoctrineBehaviour.Withdraw);

    // The default by role (build decision D6, 2026-09-25): soldiers hold,
    // archers support, bandits advance, everyone who isn't a fighter
    // withdraws. Royalty is not a fighter: losing the king mid-battle is real.
    // A driver may set a threshold on top (the bandits' flee).
    // THE doctrine a unit fights with this turn (the one place it is worked out): its own,
    // set by the player (SetBattleDoctrineIntent); else what its group's stance makes of its
    // role (M49, GroupStances.DoctrineFor); else its role's default. Pure read.
    public static BattleDoctrine Effective(GameWorld world, Unit u) =>
        u.Doctrine
        ?? (Sim.Core.Groups.GroupStances.Of(world, u) is { } stance ? Sim.Core.Groups.GroupStances.DoctrineFor(stance, u.Role) : (BattleDoctrine?)null)
        ?? DefaultFor(u.Role);

    public static BattleDoctrine DefaultFor(UnitRole role) => role switch
    {
        UnitRole.Soldier => Hold,
        UnitRole.Archer => Support,
        UnitRole.Bandit => Advance,
        _ => Withdraw,
    };
}
