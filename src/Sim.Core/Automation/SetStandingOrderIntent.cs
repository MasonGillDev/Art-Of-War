using Sim.Core.World;

namespace Sim.Core.Automation;

// M18 — installs a standing order (docs/m18-automation-engine-spec.md).
// The order is durable sim state; evaluation happens server-side. This
// Resolve validates the order's SHAPE and the player's entitlements —
// whether each step's action can actually apply is re-validated by the
// emitted intents at their own resolution time (docs/intent-validation.md),
// because the world will have moved on by then (targets may not be built
// yet at Set time, and that's legal).
//
// Preconditions (fail-clean — nothing mutates on reject):
//   * Player under the order cap (AutomationConstants.MaxOrdersPerPlayer).
//   * 1..MaxStepsPerOrder steps; every Kind/Loop/atom enum value defined.
//   * Claimed units: <= MaxClaimedUnitsPerOrder, distinct, exist, owned by
//     PlayerId, not grouped, not embarked, not claimed by any other order.
//     (Busy is fine — the driver waits for idle-by-anchors.)
//   * Every action that names a unit names a CLAIMED unit.
//   * Unit-subject conditions (CargoFull/CargoEmpty/UnitAtTile) reference
//     claimed units only.
//   * Tiles used by conditions/actions are in bounds; resources named where
//     the atom requires one; thresholds non-negative (ElapsedTicks > 0).
//
// FUTURE GATE SEAM: the per-branch unlock check ("player owns the
// automation structure for this OrderKind") slots in at the top of Resolve
// when that milestone lands. One precondition, by design.
public sealed class SetStandingOrderIntent : Intent
{
    public OrderKind Kind { get; }
    public LoopMode Loop { get; }
    public int[] ClaimedUnits { get; }
    public List<OrderStep> Steps { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetStandingOrderIntent(OrderKind kind, LoopMode loop, int[] claimedUnits, List<OrderStep> steps)
    {
        Kind = kind;
        Loop = loop;
        ClaimedUnits = claimedUnits;
        Steps = steps;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        if (!Enum.IsDefined(Kind))
            return IntentOutcome.Reject($"unknown order kind {(byte)Kind}");
        if (!Enum.IsDefined(Loop))
            return IntentOutcome.Reject($"unknown loop mode {(byte)Loop}");

        var owned = 0;
        foreach (var (_, existing) in world.StandingOrders)
            if (existing.OwnerId == PlayerId) owned++;
        if (owned >= AutomationConstants.MaxOrdersPerPlayer)
            return IntentOutcome.Reject(
                $"player {PlayerId} is at the order cap ({AutomationConstants.MaxOrdersPerPlayer})");

        if (Steps is null || Steps.Count == 0)
            return IntentOutcome.Reject("order has no steps");
        if (Steps.Count > AutomationConstants.MaxStepsPerOrder)
            return IntentOutcome.Reject(
                $"order has {Steps.Count} steps (max {AutomationConstants.MaxStepsPerOrder})");

        // ---- claimed units ----
        var claims = ClaimedUnits ?? Array.Empty<int>();
        if (claims.Length > AutomationConstants.MaxClaimedUnitsPerOrder)
            return IntentOutcome.Reject(
                $"order claims {claims.Length} units (max {AutomationConstants.MaxClaimedUnitsPerOrder})");
        var claimSet = new HashSet<int>();
        foreach (var id in claims)
        {
            if (!claimSet.Add(id))
                return IntentOutcome.Reject($"unit {id} claimed twice");
            if (!world.Units.TryGetValue(id, out var u))
                return IntentOutcome.Reject($"claimed unit {id} does not exist");
            if (u.OwnerId != PlayerId)
                return IntentOutcome.Reject($"claimed unit {id} not owned by player {PlayerId}");
            if (u.GroupId is not null)
                return IntentOutcome.Reject($"claimed unit {id} is in group {u.GroupId}");
            if (u.IsEmbarked)
                return IntentOutcome.Reject($"claimed unit {id} is embarked");
        }
        foreach (var (_, existing) in world.StandingOrders)
            foreach (var id in existing.ClaimedUnits)
                if (claimSet.Contains(id))
                    return IntentOutcome.Reject(
                        $"unit {id} is already claimed by order {existing.OrderId}");

        // ---- steps ----
        foreach (var step in Steps)
        {
            if (step is null)
                return IntentOutcome.Reject("null step");
            var stepError = ValidateStep(world, step, claimSet);
            if (stepError is not null)
                return IntentOutcome.Reject(stepError);
        }

        // ---- apply ----
        var orderId = world.NextOrderId++;
        var order = new StandingOrder
        {
            OrderId = orderId,
            OwnerId = PlayerId,
            Kind = Kind,
            Loop = Loop,
            StepEnteredTick = sim.Now,
        };
        // Canonical ascending claim list, regardless of submission order.
        var sorted = claims.ToArray();
        Array.Sort(sorted);
        order.ClaimedUnits.AddRange(sorted);
        // Defensive deep copy — the world's order must not alias lists owned
        // by this (transient) intent instance.
        foreach (var step in Steps)
        {
            var copy = new OrderStep { Action = step.Action };
            copy.Conditions.AddRange(step.Conditions);
            order.Steps.Add(copy);
        }
        world.StandingOrders.Add(orderId, order);
        return IntentOutcome.Applied;
    }

    private static string? ValidateStep(GameWorld world, OrderStep step, HashSet<int> claimSet)
    {
        foreach (var c in step.Conditions)
        {
            switch (c.Kind)
            {
                case ConditionKind.Always:
                    break;
                case ConditionKind.StoreAtLeast:
                case ConditionKind.StoreBelow:
                    if (!world.Grid.InBounds(c.SubjectTile))
                        return $"condition tile {c.SubjectTile.X},{c.SubjectTile.Y} out of bounds";
                    if (c.Resource == Resource.None)
                        return $"{c.Kind} condition names no resource";
                    if (c.Threshold < 0)
                        return $"{c.Kind} threshold {c.Threshold} is negative";
                    break;
                case ConditionKind.CargoFull:
                case ConditionKind.CargoEmpty:
                    if (!claimSet.Contains(c.SubjectUnitId))
                        return $"{c.Kind} condition references unclaimed unit {c.SubjectUnitId}";
                    break;
                case ConditionKind.UnitAtTile:
                    if (!claimSet.Contains(c.SubjectUnitId))
                        return $"UnitAtTile condition references unclaimed unit {c.SubjectUnitId}";
                    if (!world.Grid.InBounds(c.SubjectTile))
                        return $"condition tile {c.SubjectTile.X},{c.SubjectTile.Y} out of bounds";
                    break;
                case ConditionKind.ElapsedTicks:
                    if (c.Threshold <= 0)
                        return $"ElapsedTicks threshold {c.Threshold} must be positive";
                    break;
                default:
                    return $"unknown condition kind {(byte)c.Kind}";
            }
        }

        var a = step.Action;
        if (a.NamesUnit && !claimSet.Contains(a.UnitId))
            return $"{a.Kind} action references unclaimed unit {a.UnitId}";
        switch (a.Kind)
        {
            case ActionKind.MoveTo:
                if (!world.Grid.InBounds(a.TargetTile))
                    return $"MoveTo target {a.TargetTile.X},{a.TargetTile.Y} out of bounds";
                break;
            case ActionKind.HaulTrip:
                if (!world.Grid.InBounds(a.TargetTile))
                    return $"HaulTrip source {a.TargetTile.X},{a.TargetTile.Y} out of bounds";
                if (!world.Grid.InBounds(a.SecondTile))
                    return $"HaulTrip dest {a.SecondTile.X},{a.SecondTile.Y} out of bounds";
                if (a.Resource == Resource.None)
                    return "HaulTrip names no resource";
                break;
            case ActionKind.LoadCargo:
                if (a.Resource == Resource.None)
                    return "LoadCargo names no resource";
                break;
            case ActionKind.UnloadCargo:
                break;
            case ActionKind.Train:
                if (a.Role == UnitRole.None)
                    return "Train names no role";
                break;
            case ActionKind.Craft:
                if (!world.Grid.InBounds(a.TargetTile))
                    return $"Craft barracks {a.TargetTile.X},{a.TargetTile.Y} out of bounds";
                if (a.Resource == Resource.None)
                    return "Craft names no item";
                break;
            case ActionKind.AssignWorkers:
            case ActionKind.UnassignWorkers:
                if (!world.Grid.InBounds(a.TargetTile))
                    return $"{a.Kind} extractor {a.TargetTile.X},{a.TargetTile.Y} out of bounds";
                break;
            default:
                return $"unknown action kind {(byte)a.Kind}";
        }
        return null;
    }

    public override string Describe() =>
        $"SetStandingOrder({Kind}, {Steps?.Count ?? 0} steps, claims=[{string.Join(",", ClaimedUnits ?? Array.Empty<int>())}])";
}
