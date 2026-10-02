namespace Sim.Core.Battlefields;

// M41 — a battle order for one of the player's units on an open battlefield
// (docs/battlefield-grid.md §5 "Orders"). It stands until done or replaced
// and counts for the next beat; the other side never sees it.
//
//   Kind      — Hold, MoveTo, Route, Swap, Withdraw; or Stop (0): drop the
//               order and go back to doctrine.
//   Target    — MoveTo's subtile (just outside an edge = leave across it).
//   Waypoints — Route's traced subtiles, contiguous, only the last may be
//               outside an edge.
//   SwapWith  — Swap's partner: an adjacent ally on the same board.
public sealed class SetBattleOrderIntent : Intent
{
    public const byte Stop = 0;

    public int UnitId { get; }
    public byte Kind { get; }
    public Subtile Target { get; }
    public IReadOnlyList<Subtile> Waypoints { get; }
    public int SwapWith { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetBattleOrderIntent(int unitId, byte kind, Subtile target = default,
        IReadOnlyList<Subtile>? waypoints = null, int swapWith = 0)
    {
        UnitId = unitId;
        Kind = kind;
        Target = target;
        Waypoints = waypoints ?? Array.Empty<Subtile>();
        SwapWith = swapWith;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (!Battlefields.TryGet(world, unit, out var bf))
            return IntentOutcome.Reject($"unit {UnitId} is not on a battlefield");
        var slot = unit.Board!;
        if (unit.Subtile is null) return IntentOutcome.Reject($"unit {UnitId} has no subtile");

        BattleOrder? order;
        try
        {
            order = (BattleOrderKind)Kind switch
            {
                _ when Kind == Stop => null,
                BattleOrderKind.Hold => BattleOrder.Hold(),
                BattleOrderKind.Withdraw => BattleOrder.Withdraw(),
                BattleOrderKind.MoveTo => BattleOrder.MoveTo(Target),
                BattleOrderKind.Route => BattleOrder.Route(Waypoints),
                BattleOrderKind.Swap => BattleOrder.Swap(SwapWith),
                _ => throw new ArgumentException($"unknown battle order kind {Kind}"),
            };
        }
        catch (ArgumentException e)
        {
            return IntentOutcome.Reject(e.Message);
        }

        var layer = Battlefields.LayerFor(world, bf.Tile);
        var mover = Battlefields.MoverFor(world, layer, unit);
        if (order is { Kind: BattleOrderKind.MoveTo } && order.Destination.IsOnBoard && !layer.CanStand(order.Destination, mover))
            return IntentOutcome.Reject($"subtile {order.Destination} can't be stood on");
        if (order is { Kind: BattleOrderKind.Route })
            foreach (var w in order.Waypoints)
                if (w.IsOnBoard && !layer.CanStand(w, mover))
                    return IntentOutcome.Reject($"waypoint {w} can't be stood on");
        if (order is { Kind: BattleOrderKind.Swap })
        {
            if (!world.Units.TryGetValue(SwapWith, out var partner) || partner.OwnerId != unit.OwnerId
                || partner.Board is not { } ps || ps.Tile != slot.Tile || partner.Subtile is null)
                return IntentOutcome.Reject($"unit {SwapWith} is not an ally on this battlefield");
        }

        slot.Order = order;
        Battlefields.Wake(sim, bf);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetBattleOrder(unit={UnitId}, kind={Kind})";
}

// M41 — a unit's standing battle doctrine (§7): what it does on a board with
// no order. Can be set anywhere, in or out of battle; it waits for the next
// battle. Behaviour is DoctrineBehaviour; WithdrawBelow 0 = never.
public sealed class SetBattleDoctrineIntent : Intent
{
    public int UnitId { get; }
    public byte Behaviour { get; }
    public int WithdrawBelow { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetBattleDoctrineIntent(int unitId, byte behaviour, int withdrawBelow = 0)
    {
        UnitId = unitId;
        Behaviour = behaviour;
        WithdrawBelow = withdrawBelow;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (DoctrineCatalog.Blocker(unit.Role, (DoctrineBehaviour)Behaviour) is { } why)
            return IntentOutcome.Reject(why);
        if (WithdrawBelow < 0 || WithdrawBelow > Subtile.Count)
            return IntentOutcome.Reject($"withdraw threshold {WithdrawBelow} out of range");
        unit.Doctrine = new BattleDoctrine((DoctrineBehaviour)Behaviour, WithdrawBelow);
        if (Battlefields.TryGet(sim.World, unit, out var bf)) Battlefields.Wake(sim, bf);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetBattleDoctrine(unit={UnitId}, {(DoctrineBehaviour)Behaviour})";
}
