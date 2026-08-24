using Sim.Core.World;

namespace Sim.Core.Population;

// M30 follow-up — the per-unit TRAINING implementation, extracted so the two
// roads to a trained citizen share one body of code.
//
// Same argument that put WorkAssignment.TryAssignWorker beside Release: there
// is now more than one way to arrive at the job. TrainUnitIntent applies this
// to a unit already standing on the trainer; GoalRules applies it to a unit
// that has just walked there. One implementation, so "trained by hand" and
// "trained on arrival" can never come to mean different things — in
// particular the equipment strip and the absolute-wound health delta, both of
// which are easy to forget in a second copy.
//
// Returns false rather than throwing: callers treat it exactly like the
// per-id skips they already have.
public static class TrainingRules
{
    // Can this body be trained into this role AT ALL, ignoring where it is
    // standing? The pure-read half — used by the intent to reject malformed
    // requests up front, and by the goal path to re-check on arrival, since
    // the world may have moved on during the walk.
    public static string? Blocker(Simulation sim, Unit unit, UnitRole newRole)
    {
        if (unit.GroupId is not null) return "in a group";
        if (unit.IsEmbarked) return "embarked";
        if (Population.GetActiveBreedingFor(sim.World, unit.Id) is not null) return "locked breeding";
        if (RoleTrainerCatalog.TrainerFor(newRole) is null)
            return $"role {newRole} is not trainable from a citizen";
        if (!Population.CanTrain(unit, sim.Now, sim.World.PopulationConfig))
            return $"too young to train (age " +
                   $"{Population.AgeYears(unit, sim.Now, sim.World.PopulationConfig)} < " +
                   $"MinTrainAge {sim.World.PopulationConfig.MinTrainAge})";
        return null;
    }

    // The trainer structure this unit is standing on, if it is the right one
    // for `newRole` and owned by the same player. Null otherwise.
    public static Structure? TrainerUnder(Simulation sim, Unit unit, UnitRole newRole)
    {
        var kind = RoleTrainerCatalog.TrainerFor(newRole);
        if (kind is null) return null;
        if (!sim.World.Structures.TryGetValue(unit.Position, out var s)) return null;
        if (s.Kind != kind) return null;
        if (s.OwnerId != unit.OwnerId) return null;
        return s;
    }

    // Apply the training. Assumes Blocker returned null and the unit is
    // standing on a valid trainer; both roads check that immediately before
    // calling.
    public static bool Train(Simulation sim, Unit unit, UnitRole newRole)
    {
        if (unit.Activity != Activity.Idle) return false;
        if (TrainerUnder(sim, unit, newRole) is null) return false;

        // Strip equipment BEFORE the role flip: a Farmer can't keep the sword
        // their Soldier self equipped. Items drop to the trainer tile
        // (recoverable by haul); the helper also reverses any HealthModifier
        // the equipment granted (clamped to min 1).
        Sim.Core.Equipment.Equipment.DropEquipmentToGround(sim.World, unit, unit.Position);

        var oldRole = unit.Role;
        unit.SetRoleForTraining(newRole);
        unit.BumpEpoch();

        // Health delta (docs/military-training.md): absolute wounds persist
        // across retrains. A Farmer (10) trained to Soldier (30) gains +20; a
        // wounded Soldier retrained to Farmer keeps the same absolute damage,
        // clamped so the retrain can't kill.
        unit.Health += Sim.Core.Combat.UnitCombatCatalog.Spec(newRole).BaseHealth
                     - Sim.Core.Combat.UnitCombatCatalog.Spec(oldRole).BaseHealth;
        if (unit.Health < 1) unit.Health = 1;
        return true;
    }
}
