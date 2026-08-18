using Sim.Core.World;

namespace Sim.Core.Automation;

// Append-only enum (serialized in durable intent JSON).
public enum ClaimOp : byte
{
    Claim   = 1, // commit this order to this unit (fails if already claimed)
    Release = 2, // give the unit back to the pool
    Repurpose = 3, // InFlight → Crew (the errand landed; keep the unit)
}

// THE ONLY mutation path for the Claims Ledger (GameWorld.Claims).
//
// SERVER-INTERNAL (the wire rejects it; the automation driver submits it
// in-process) but DURABLE like every other intent: claims live in the
// intent log, so crash recovery resumes a half-finished pull — the unit
// walking to the school is still spoken for after a restart — and the
// headline driverless replay reproduces the ledger exactly. Same class as
// M16's bandit intents and M18's AdvanceOrderCursorIntent; see
// docs/intent-authorization.md.
//
// CLAIM-ON-COMMIT (docs/automation-substrate.md): the driver submits this
// the moment it DECIDES to use a unit, before dispatching the work. That
// ordering is the whole point — a claim that landed only when the work
// finished would leave the unit in the pool for every other order to grab
// in the meantime.
//
// FENCE: Claim fails if the unit already holds a claim. That failure is
// the arbitration — two orders reaching for the same dormant unit in one
// think pass resolve first-come (canonical order → deterministic), and
// the loser sees the rejection and takes the next unit or stands down.
// No separate lock, no retry storm: the ledger IS the mutex.
public sealed class ClaimUnitIntent : Intent
{
    public int UnitId { get; }
    public int OrderId { get; }
    public ClaimOp Op { get; }
    public ClaimPurpose Purpose { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ClaimUnitIntent(int unitId, int orderId, ClaimOp op, ClaimPurpose purpose)
    {
        UnitId = unitId;
        OrderId = orderId;
        Op = op;
        Purpose = purpose;
    }

    // The factories carry playerId because the driver speaks AS the order's
    // owner: attribution (notices, audit, the per-player intent log) has to
    // land on the player whose automation acted, not on player 0.
    public static ClaimUnitIntent Claim(int unitId, int orderId, ClaimPurpose purpose, int playerId = 0) =>
        new(unitId, orderId, ClaimOp.Claim, purpose) { PlayerId = playerId };

    public static ClaimUnitIntent Release(int unitId, int orderId, int playerId = 0) =>
        new(unitId, orderId, ClaimOp.Release, ClaimPurpose.Crew) { PlayerId = playerId };

    public static ClaimUnitIntent Repurpose(int unitId, int orderId, ClaimPurpose to, int playerId = 0) =>
        new(unitId, orderId, ClaimOp.Repurpose, to) { PlayerId = playerId };

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        var held = world.Claims.TryGetValue(UnitId, out var existing);

        // Resolution-time validation against the live world
        // (docs/intent-validation.md) — the unit may have died, been
        // captured, or changed hands between the driver's think and now.
        //
        // ONLY Claim requires a live, owned unit. RELEASE MUST WORK ON THE
        // DEAD: a crew member killed on the road is exactly the case that
        // has to be cleaned up, and requiring the body to exist would strand
        // their claim in the ledger forever — the unit is gone, but its
        // reservation would keep an order believing it still has hands.
        if (Op == ClaimOp.Claim)
        {
            if (!world.Units.TryGetValue(UnitId, out var unit))
                return IntentOutcome.Reject($"unit {UnitId} does not exist");
            // The driver speaks AS the order's owner, so attribution
            // (notices, audit) stays per-player. Defense in depth behind
            // the wire guard.
            if (unit.OwnerId != PlayerId)
                return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        }
        else if (held && world.Units.TryGetValue(UnitId, out var living)
                 && living.OwnerId != PlayerId)
        {
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        }

        switch (Op)
        {
            case ClaimOp.Claim:
                // The arbitration point. Losing here is ORDINARY — the
                // driver's next pass picks a different unit.
                if (held)
                    return IntentOutcome.Reject(
                        $"unit {UnitId} is already claimed by order {existing.OrderId}");
                world.Claims[UnitId] = new Claim(UnitId, OrderId, Purpose);
                return IntentOutcome.Applied;

            case ClaimOp.Release:
                if (!held)
                    return IntentOutcome.Reject($"unit {UnitId} holds no claim");
                if (existing.OrderId != OrderId)
                    return IntentOutcome.Reject(
                        $"unit {UnitId} is claimed by order {existing.OrderId}, not {OrderId}");
                world.Claims.Remove(UnitId);
                return IntentOutcome.Applied;

            case ClaimOp.Repurpose:
                if (!held)
                    return IntentOutcome.Reject($"unit {UnitId} holds no claim");
                if (existing.OrderId != OrderId)
                    return IntentOutcome.Reject(
                        $"unit {UnitId} is claimed by order {existing.OrderId}, not {OrderId}");
                if (existing.Purpose == Purpose)
                    return IntentOutcome.Reject(
                        $"unit {UnitId} claim is already {Purpose}");
                world.Claims[UnitId] = existing with { Purpose = Purpose };
                return IntentOutcome.Applied;

            default:
                return IntentOutcome.Reject($"unknown claim op {(byte)Op}");
        }
    }

    public override string Describe() =>
        $"ClaimUnit(unit={UnitId}, order={OrderId}, {Op}, {Purpose})";
}
