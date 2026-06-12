namespace Sim.Core.Automation;

// M18 — removes a standing order. Removing the order releases its unit
// claims implicitly (claims live on the order; the exclusivity check scans
// orders). In-flight effects of already-emitted action intents are NOT
// cancelled — a hauler mid-trip finishes its trip; it just won't be sent
// again. Editing an order = Clear + Set.
public sealed class ClearStandingOrderIntent : Intent
{
    public int OrderId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ClearStandingOrderIntent(int orderId) { OrderId = orderId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.StandingOrders.TryGetValue(OrderId, out var order))
            return IntentOutcome.Reject($"order {OrderId} does not exist");
        if (order.OwnerId != PlayerId)
            return IntentOutcome.Reject($"order {OrderId} not owned by player {PlayerId}");

        world.StandingOrders.Remove(OrderId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ClearStandingOrder({OrderId})";
}
