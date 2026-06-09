using Sim.Core.World;

namespace Sim.Core.Population;

// Training — instant intent that flips a unit's UnitRole.
//
// Preconditions (re-checked at resolution time):
//   * Unit exists and is owned by PlayerId.
//   * Unit is standing on a School tile owned by PlayerId.
//   * Unit passes Population.CanTrain (>= MinTrainAge years old) — the
//     same age gate AssignBuildersIntent / AssignWorkersIntent use.
//   * Unit is Idle (not Working / Building / Hauling / Moving). Forces
//     the player to retask before retraining; cleaner than auto-cancel.
//   * Unit is not in a Group / embarked / breeding.
//   * NewRole is not UnitRole.Boat (boats are dock-produced, not
//     trained from a citizen).
//
// Effects:
//   * unit.Role = NewRole. That's the entire payload.
//   * AssignmentEpoch bumped so any latent solo events fence. (None
//     should exist because Idle was the precondition, but defensive.)
public sealed class TrainUnitIntent : Intent
{
    public int UnitId { get; }
    public UnitRole NewRole { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public TrainUnitIntent(int unitId, UnitRole newRole)
    {
        UnitId = unitId;
        NewRole = newRole;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.GroupId is not null)
            return IntentOutcome.Reject($"unit {UnitId} is in a group");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        if (Population.GetActiveBreedingFor(world, UnitId) is not null)
            return IntentOutcome.Reject($"unit {UnitId} is locked breeding");

        if (NewRole == UnitRole.Boat)
            return IntentOutcome.Reject("citizens cannot be trained into boats");

        if (!Population.CanTrain(unit, sim.Now, world.PopulationConfig))
            return IntentOutcome.Reject(
                $"unit {UnitId} is too young to train " +
                $"(age {Population.AgeYears(unit, sim.Now, world.PopulationConfig)} " +
                $"< MinTrainAge {world.PopulationConfig.MinTrainAge})");

        // School requirement: the unit must be standing on a School tile
        // owned by the same player.
        if (!world.Structures.TryGetValue(unit.Position, out var s) || s is not School)
            return IntentOutcome.Reject(
                $"unit {UnitId} is not on a School (at {unit.Position.X},{unit.Position.Y})");
        if (s.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"School at {unit.Position.X},{unit.Position.Y} not owned by player {PlayerId}");

        // Apply. Role is init-only via the property API, but we own
        // Unit; expose an internal setter to keep mutation localized.
        unit.SetRoleForTraining(NewRole);
        unit.BumpEpoch();

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"Train(unit={UnitId} -> {NewRole})";
}
