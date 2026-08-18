using Sim.Core.Intents;

namespace Sim.Core.Automation;

// Append-only enum (serialized in durable intent JSON).
public enum OrderStatusOp : byte
{
    MarkFired  = 1, // work dispatched this tick — stamps LastFiredTick, clears retries
    BumpRetry  = 2, // fired but produced nothing; walks toward auto-disable
    Disable    = 3, // retry budget exhausted (or player stop)
    Enable     = 4, // player resume — clears the retry budget
    // Routine only — the caravan finished a stop; move to the next, wrapping
    // at the end. Fenced on ExpectedStep so a stale advance (the player
    // re-authored the circuit between think and resolution) no-ops cleanly.
    AdvanceStep = 5,
}

// The ONLY mutation path for an Order's durable status block (Enabled,
// RetryCount, LastFiredTick). Definition fields are init-only; Set/Clear
// own the dictionary.
//
// SERVER-INTERNAL (wire-rejected, driver-submitted) but DURABLE like every
// intent: status moves live in the intent log, so recovery resumes an
// order's retry budget exactly and the driverless replay reproduces it.
// The same class as M18's AdvanceOrderCursorIntent, which this replaces.
//
// Why status is durable at all: without it, a restart would hand every
// wedged order a fresh retry budget and the auto-disable rule — the M16
// "wedge forever" fix — would silently never fire on a long-running server.
public sealed class OrderStatusIntent : Intent
{
    public int OrderId { get; }
    public OrderStatusOp Op { get; }
    // AdvanceStep only — the cursor the driver OBSERVED at think time. A
    // mismatch means the world moved on and the advance is stale.
    public int ExpectedStep { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public OrderStatusIntent(int orderId, OrderStatusOp op, int expectedStep = 0)
    {
        OrderId = orderId;
        Op = op;
        ExpectedStep = expectedStep;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Orders.TryGetValue(OrderId, out var order))
            return IntentOutcome.Reject($"order {OrderId} does not exist");
        // The driver speaks AS the owner, so attribution stays per-player.
        if (order.OwnerId != PlayerId)
            return IntentOutcome.Reject($"order {OrderId} not owned by player {PlayerId}");

        switch (Op)
        {
            case OrderStatusOp.MarkFired:
                order.LastFiredTick = sim.Now;
                order.RetryCount = 0;
                return IntentOutcome.Applied;

            case OrderStatusOp.BumpRetry:
                order.RetryCount++;
                return IntentOutcome.Applied;

            case OrderStatusOp.Disable:
                if (!order.Enabled)
                    return IntentOutcome.Reject($"order {OrderId} is already disabled");
                order.Enabled = false;
                return IntentOutcome.Applied;

            case OrderStatusOp.Enable:
                if (order.Enabled)
                    return IntentOutcome.Reject($"order {OrderId} is already enabled");
                order.Enabled = true;
                order.RetryCount = 0;
                return IntentOutcome.Applied;

            case OrderStatusOp.AdvanceStep:
                if (order.Steps.Count == 0)
                    return IntentOutcome.Reject($"order {OrderId} has no circuit to advance");
                if (order.CurrentStep != ExpectedStep)
                    return IntentOutcome.Reject(
                        $"cursor fence: order {OrderId} is at step {order.CurrentStep}, " +
                        $"expected {ExpectedStep}");
                order.CurrentStep = (order.CurrentStep + 1) % order.Steps.Count;
                order.LastFiredTick = sim.Now;
                order.RetryCount = 0;
                return IntentOutcome.Applied;

            default:
                return IntentOutcome.Reject($"unknown status op {(byte)Op}");
        }
    }

    public override string Describe() => $"OrderStatus(order={OrderId}, {Op})";
}
