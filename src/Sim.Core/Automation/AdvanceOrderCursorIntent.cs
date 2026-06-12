namespace Sim.Core.Automation;

// Append-only enum (serialized in durable intent JSON).
public enum CursorOp : byte
{
    MarkDispatched = 1, // step's action submitted; don't re-submit next think
    AdvanceStep    = 2, // step complete → next step (wrap/disable per LoopMode)
    BumpRetry      = 3, // no progress this think; clears the dispatch fence so the action re-submits
    Disable        = 4, // retry budget exhausted (or external stop) → Enabled = false
}

// M18 — SERVER-INTERNAL (the wire rejects it; the AutomationDriver submits
// in-process) but DURABLE like any intent: cursor moves live in the intent
// log, so crash recovery resumes a mid-route order exactly and the headline
// driverless replay reproduces cursor state. Same class as the M16 bandit
// intents — see docs/intent-authorization.md.
//
// This is the ONLY mutation path for the StandingOrder cursor block
// (Enabled / CurrentStep / StepEnteredTick / StepRetryCount /
// ActionDispatched). Definition fields are init-only; Set/Clear own the
// dictionary. docs/determinism-audit.md.
//
// FENCE: ExpectedStep carries the CurrentStep the driver observed at think
// time. A player Clear+Set or a competing cursor move between think and
// resolution makes the fence mismatch and the op no-ops cleanly (reject) —
// the standard stale-event discipline (architecture §2.6), applied to a
// stale INTENT.
public sealed class AdvanceOrderCursorIntent : Intent
{
    public int OrderId { get; }
    public CursorOp Op { get; }
    public int ExpectedStep { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public AdvanceOrderCursorIntent(int orderId, CursorOp op, int expectedStep)
    {
        OrderId = orderId;
        Op = op;
        ExpectedStep = expectedStep;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.StandingOrders.TryGetValue(OrderId, out var order))
            return IntentOutcome.Reject($"order {OrderId} does not exist");
        // Defense in depth behind the wire guard: the driver speaks AS the
        // order's owner, so attribution (notices, audit) stays per-player.
        if (order.OwnerId != PlayerId)
            return IntentOutcome.Reject($"order {OrderId} not owned by player {PlayerId}");
        if (order.CurrentStep != ExpectedStep)
            return IntentOutcome.Reject(
                $"cursor fence: order {OrderId} is at step {order.CurrentStep}, expected {ExpectedStep}");

        switch (Op)
        {
            case CursorOp.MarkDispatched:
                if (order.ActionDispatched)
                    return IntentOutcome.Reject($"order {OrderId} step {ExpectedStep} already dispatched");
                order.ActionDispatched = true;
                return IntentOutcome.Applied;

            case CursorOp.AdvanceStep:
                var next = order.CurrentStep + 1;
                if (next >= order.Steps.Count)
                {
                    if (order.Loop == LoopMode.Loop)
                    {
                        next = 0;
                    }
                    else
                    {
                        // Once-mode completion: park at step 0, disabled.
                        order.Enabled = false;
                        next = 0;
                    }
                }
                order.CurrentStep = next;
                order.StepEnteredTick = sim.Now;
                order.StepRetryCount = 0;
                order.ActionDispatched = false;
                return IntentOutcome.Applied;

            case CursorOp.BumpRetry:
                order.StepRetryCount++;
                order.ActionDispatched = false;
                return IntentOutcome.Applied;

            case CursorOp.Disable:
                if (!order.Enabled)
                    return IntentOutcome.Reject($"order {OrderId} is already disabled");
                order.Enabled = false;
                return IntentOutcome.Applied;

            default:
                return IntentOutcome.Reject($"unknown cursor op {(byte)Op}");
        }
    }

    public override string Describe() =>
        $"AdvanceOrderCursor(order={OrderId}, {Op}, expectedStep={ExpectedStep})";
}
