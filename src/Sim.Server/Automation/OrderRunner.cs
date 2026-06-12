using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Server.Automation;

// M18 — sequences ONE standing order. Owns only ephemeral bookkeeping (the
// in-flight action intent reference + its harvested outcome); every durable
// effect goes through intents: the step's action (an ordinary intent voiced
// as the owner) and the cursor moves (AdvanceOrderCursorIntent).
//
// THE STEP LIFECYCLE, one transition per think at most:
//   waiting    — cursor fence down. ALL conditions met AND the subject unit
//                ready? Submit action + MarkDispatched.
//   dispatched — action submitted, outcome not yet harvested: wait.
//   rejected   — BumpRetry (re-evaluates + re-dispatches next think), or
//                Disable once the config'd retry budget is exhausted. The
//                structural anti-wedge rule: nothing retries silently
//                forever.
//   applied    — process atoms (MoveTo / HaulTrip) wait until the subject
//                unit is idle BY ANCHORS again; instant atoms complete
//                immediately. Then AdvanceStep.
//
// WHAT DOES NOT COUNT AS FAILURE: conditions not met (that's what waiting
// IS — "until cargo full" can legitimately wait hours), and a claimed unit
// busied by a manual player intent (manual control wins; the step stalls
// until the unit is free). Only a REJECTED action intent, a vanished
// claimed unit, or a cold-start ambiguity bumps the retry counter.
//
// COLD START (server restart mid-step): the cursor says dispatched but the
// intent reference is gone with the old process. We can't know whether the
// action applied, so BumpRetry — the fence clears and conditions re-gate
// the re-dispatch. A re-run atom is harmless where it matters (supply-line
// templates re-check StoreBelow; a re-Train rejects). Replay correctness is
// untouched either way — the log already holds whatever the old process
// actually submitted.
public sealed class OrderRunner
{
    // The submitted-but-not-yet-harvested action intent (reference-matched
    // against the ResolvedLog by the driver), and its outcome once seen.
    public Intent? PendingAction;
    public IntentOutcome? PendingOutcome;

    public void Think(Simulation sim, StandingOrder order, IReadOnlySet<TileCoord> visible,
        long now, AutomationConfig cfg)
    {
        if (!order.Enabled || order.Steps.Count == 0) return;
        var step = order.Steps[order.CurrentStep];

        if (PendingAction is not null)
        {
            if (PendingOutcome is null) return; // still in flight — wait

            if (PendingOutcome.IsRejected)
            {
                SubmitRetryOrDisable(sim, order, now, cfg);
                PendingAction = null;
                PendingOutcome = null;
                return;
            }

            // Applied. Process atoms hold the step open until the unit's
            // in-flight anchors clear (M16 pitfall: a marching unit reads
            // Activity.Idle — anchors are the truth, never Activity).
            if (WaitsForUnitIdle(step.Action.Kind))
            {
                if (!sim.World.Units.TryGetValue(step.Action.UnitId, out var u))
                {
                    // Unit died mid-action: no completion will ever come.
                    SubmitRetryOrDisable(sim, order, now, cfg);
                    PendingAction = null;
                    PendingOutcome = null;
                    return;
                }
                if (!IsIdleByAnchors(u)) return; // in progress — wait
            }
            sim.SubmitIntent(now, new AdvanceOrderCursorIntent(order.OrderId,
                CursorOp.AdvanceStep, order.CurrentStep) { PlayerId = order.OwnerId });
            PendingAction = null;
            PendingOutcome = null;
            return;
        }

        if (order.ActionDispatched)
        {
            // Dispatched per the durable cursor, but this process never
            // submitted it — cold start. Clear the fence via BumpRetry (or
            // give up if the budget is gone) and re-evaluate next think.
            SubmitRetryOrDisable(sim, order, now, cfg);
            return;
        }

        // ---- waiting: gate on conditions + subject readiness ----
        foreach (var c in step.Conditions)
            if (!ConditionEvaluator.IsMet(sim.World, order.OwnerId, c, order.StepEnteredTick, now, visible))
                return; // legitimately waiting — never a retry bump

        if (step.Action.NamesUnit)
        {
            if (!sim.World.Units.TryGetValue(step.Action.UnitId, out var u)
                || u.OwnerId != order.OwnerId)
            {
                // Claimed unit is gone (died / captured) — burn the retry
                // budget toward auto-disable so the order doesn't wedge.
                SubmitRetryOrDisable(sim, order, now, cfg);
                return;
            }
            // Manual control wins: only act on a unit that is free. The one
            // exception is UnassignWorkers, whose subject is Working by
            // definition.
            if (RequiresIdleToDispatch(step.Action.Kind) && !IsIdleByAnchors(u))
                return; // stalled behind manual orders — wait, no bump
        }

        var action = IntentFactory.Create(step.Action, order.OwnerId);
        sim.SubmitIntent(now, action);
        sim.SubmitIntent(now, new AdvanceOrderCursorIntent(order.OrderId,
            CursorOp.MarkDispatched, order.CurrentStep) { PlayerId = order.OwnerId });
        PendingAction = action;
        PendingOutcome = null;
    }

    private static void SubmitRetryOrDisable(Simulation sim, StandingOrder order, long now, AutomationConfig cfg)
    {
        if (order.StepRetryCount + 1 >= cfg.MaxStepRetries)
            sim.SubmitIntent(now, new AdvanceOrderCursorIntent(order.OrderId,
                CursorOp.Disable, order.CurrentStep) { PlayerId = order.OwnerId });
        else
            sim.SubmitIntent(now, new AdvanceOrderCursorIntent(order.OrderId,
                CursorOp.BumpRetry, order.CurrentStep) { PlayerId = order.OwnerId });
    }

    // Atoms that start a PROCESS — the step completes when the unit's
    // in-flight anchors clear. Everything else is instant: applied = done.
    // (AssignWorkers parks the unit Working on purpose — waiting for idle
    // would deadlock the order.)
    private static bool WaitsForUnitIdle(ActionKind kind) =>
        kind is ActionKind.MoveTo or ActionKind.HaulTrip;

    private static bool RequiresIdleToDispatch(ActionKind kind) =>
        kind is not ActionKind.UnassignWorkers;

    private static bool IsIdleByAnchors(Unit u) =>
        u.Activity == Activity.Idle
        && u.PathRemaining is null
        && u.NextArrivalTick is null
        && u.HaulPlan is null
        && u.GroupId is null
        && !u.IsEmbarked;
}
