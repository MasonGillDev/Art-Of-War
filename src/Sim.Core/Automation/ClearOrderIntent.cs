using Sim.Core.Intents;

namespace Sim.Core.Automation;

// Remove an automation order and release every unit it holds.
//
// THE RELEASE IS THE POINT: an order's claims are the only thing standing
// between its crew and the dormant pool, so clearing must hand them back
// atomically. A cleared order that leaked claims would strand its units —
// permanently invisible to every other order, alive but unemployable.
public sealed class ClearOrderIntent : Intent
{
    public int OrderId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ClearOrderIntent(int orderId) { OrderId = orderId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Orders.TryGetValue(OrderId, out var order))
            return IntentOutcome.Reject($"order {OrderId} does not exist");
        if (order.OwnerId != PlayerId)
            return IntentOutcome.Reject($"order {OrderId} not owned by player {PlayerId}");

        // Release every claim this order holds — named crew AND any
        // in-flight pull it had committed to. Collected first, then
        // removed: mutating the ledger while enumerating it would throw.
        foreach (var unitId in ClaimLedger.UnitsOf(world, OrderId))
            world.Claims.Remove(unitId);

        world.Orders.Remove(OrderId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ClearOrder(order={OrderId})";
}
