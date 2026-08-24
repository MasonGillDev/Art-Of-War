using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Population;

// Training — GOAL-SHAPED (docs/goal-shaped-intents.md).
//
// "Train him as a builder" used to mean: walk him to the school yourself,
// wait, then remember to come back and fire this. That middle step is an
// appointment, and appointments are the thing goal-shaping exists to delete.
// Name a TrainerTile and the sim does the walking; the role flips on arrival.
//
// TrainerTile is OPTIONAL and back-compatible: omit it and the pre-existing
// contract holds exactly (the unit must already be standing on the right
// trainer). That is what lets the AI ladders and the client adopt this one
// call site at a time instead of in a single flag-day change.
//
// Preconditions (re-checked at resolution time, and AGAIN on arrival — a walk
// takes game-days and the world moves):
//   * Unit exists, owned by PlayerId, not grouped / embarked / breeding.
//   * Unit is Idle. Forces the player to retask before retraining; cleaner
//     than auto-cancel.
//   * Unit passes Population.CanTrain (>= MinTrainAge). Too young REJECTS
//     rather than waiting: the wait would be years spent standing in a school,
//     which is a trap dressed as a feature (same call as BeginBreeding's).
//   * NewRole has a trainer — RoleTrainerCatalog routes civilian roles to the
//     School and military ones to the Barracks. Boat maps to none: boats are
//     dock-produced, not trained from a citizen.
//   * The trainer at TrainerTile (or under the unit) is that structure kind
//     and owned by PlayerId.
//
// Effects live in TrainingRules.Train, shared with the goal path so the
// equipment strip and the absolute-wound health delta can never diverge
// between "trained by hand" and "trained on arrival".
public sealed class TrainUnitIntent : Intent
{
    public int UnitId { get; }
    public UnitRole NewRole { get; }

    // Where the training happens. Null = "wherever the unit is standing",
    // the pre-goal contract.
    public TileCoord? TrainerTile { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public TrainUnitIntent(int unitId, UnitRole newRole, TileCoord? trainerTile = null)
    {
        UnitId = unitId;
        NewRole = newRole;
        TrainerTile = trainerTile;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        if (TrainingRules.Blocker(sim, unit, NewRole) is { } why)
            return IntentOutcome.Reject($"unit {UnitId} {why}");

        var trainerKind = RoleTrainerCatalog.TrainerFor(NewRole)!.Value;

        // Where are we training? An explicit tile is the goal-shaped form; no
        // tile means the old "wherever you're standing" contract.
        var tile = TrainerTile ?? unit.Position;
        if (!world.Structures.TryGetValue(tile, out var s) || s.Kind != trainerKind)
            return IntentOutcome.Reject(
                $"no {trainerKind} at {tile.X},{tile.Y}");
        if (s.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"{trainerKind} at {tile.X},{tile.Y} not owned by player {PlayerId}");

        if (unit.Position == tile)
            return TrainingRules.Train(sim, unit, NewRole)
                ? IntentOutcome.Applied
                : IntentOutcome.Reject($"unit {UnitId} could not be trained at {tile.X},{tile.Y}");

        // Elsewhere: walk there and train on arrival. Travel is a mechanical
        // step between the player and their stated goal, so it is the sim's.
        return GoalRules.Begin(sim, unit, new GoalPlan(GoalKind.Train, tile, arg: (int)NewRole))
            ? IntentOutcome.Applied
            : IntentOutcome.Reject($"unit {UnitId} cannot reach the {trainerKind} at {tile.X},{tile.Y}");
    }

    public override string Describe() =>
        TrainerTile is { } t
            ? $"Train(unit={UnitId} -> {NewRole} @ {t.X},{t.Y})"
            : $"Train(unit={UnitId} -> {NewRole})";
}
