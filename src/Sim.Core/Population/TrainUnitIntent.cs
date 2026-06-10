using Sim.Core.World;

namespace Sim.Core.Population;

// Training — instant intent that flips a unit's UnitRole.
//
// Preconditions (re-checked at resolution time):
//   * Unit exists and is owned by PlayerId.
//   * Unit is standing on the trainer structure for NewRole, owned by
//     PlayerId — RoleTrainerCatalog routes civilian roles to the School
//     and military roles (Soldier/Archer) to the Barracks.
//   * Unit passes Population.CanTrain (>= MinTrainAge years old) — the
//     same age gate AssignBuildersIntent / AssignWorkersIntent use.
//   * Unit is Idle (not Working / Building / Hauling / Moving). Forces
//     the player to retask before retraining; cleaner than auto-cancel.
//   * Unit is not in a Group / embarked / breeding.
//   * NewRole has a trainer (UnitRole.Boat maps to none — boats are
//     dock-produced, not trained from a citizen).
//
// Effects:
//   * Equipment buffs are stripped first — items drop to the trainer
//     tile, HealthModifiers reverse (docs/equipment-model.md).
//   * unit.Role = NewRole.
//   * unit.Health shifts by the BaseHealth delta between roles, clamped
//     to min 1 (docs/military-training.md — wounds persist absolutely).
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

        // Trainer routing (docs/military-training.md): civilian roles
        // train at the School, military roles at the Barracks, Boat at
        // nothing (dock-produced, not trained).
        var trainerKind = RoleTrainerCatalog.TrainerFor(NewRole);
        if (trainerKind is null)
            return IntentOutcome.Reject($"role {NewRole} is not trainable from a citizen");

        if (!Population.CanTrain(unit, sim.Now, world.PopulationConfig))
            return IntentOutcome.Reject(
                $"unit {UnitId} is too young to train " +
                $"(age {Population.AgeYears(unit, sim.Now, world.PopulationConfig)} " +
                $"< MinTrainAge {world.PopulationConfig.MinTrainAge})");

        // Trainer requirement: the unit must be standing on the trainer
        // structure for the requested role, owned by the same player.
        if (!world.Structures.TryGetValue(unit.Position, out var s) || s.Kind != trainerKind)
            return IntentOutcome.Reject(
                $"unit {UnitId} is not on a {trainerKind} (at {unit.Position.X},{unit.Position.Y})");
        if (s.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"{trainerKind} at {unit.Position.X},{unit.Position.Y} not owned by player {PlayerId}");

        // Strip equipment BEFORE the role flip: a Farmer can't keep the
        // sword their Soldier self equipped. Items drop to the trainer
        // tile (recoverable by haul); the helper also reverses any
        // HealthModifier the equipment granted (clamped to min 1).
        Sim.Core.Equipment.Equipment.DropEquipmentToGround(world, unit, unit.Position);

        var oldRole = unit.Role;

        // Apply. Role is init-only via the property API, but we own
        // Unit; expose an internal setter to keep mutation localized.
        unit.SetRoleForTraining(NewRole);
        unit.BumpEpoch();

        // Health delta (docs/military-training.md): absolute wounds
        // persist across retrains. A Farmer (10) trained to Soldier (30)
        // gains +20; a wounded Soldier retrained to Farmer keeps the
        // same absolute damage, clamped so the retrain can't kill.
        unit.Health += Sim.Core.Combat.UnitCombatCatalog.Spec(NewRole).BaseHealth
                     - Sim.Core.Combat.UnitCombatCatalog.Spec(oldRole).BaseHealth;
        if (unit.Health < 1) unit.Health = 1;

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"Train(unit={UnitId} -> {NewRole})";
}
