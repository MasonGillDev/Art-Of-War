using Sim.Core.Intents;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Combat;

// M29 — chase a hostile unit down (docs/patrols.md).
//
// PLAYER-FACING, unlike the automation substrate's bookkeeping intents. The
// patrol driver submits one of these per crew member when it engages, and the
// wire accepts them too, so "run that raider down" is a manual verb the player
// gets for free — which serves the doctrine that contact is never automated
// away from the player rather than fighting it.
//
// It commits nothing but POSTURE. There is no attack here: combat triggers on
// same-tile co-location (CombatTrigger) and resolves itself. This intent only
// says "close the distance", and PursuitRules decides, per tile, whether that
// is still worth doing.
//
// Validation is resolution-time against the live world (docs/intent-validation.md)
// and is deliberately narrow — structural, never tactical. "Should I chase
// this?" is the player's problem; a patrol chasing something it cannot catch
// is a legal, visible mistake.
public sealed class EngageUnitIntent : Intent
{
    public int UnitId { get; }
    public int TargetUnitId { get; }
    public TileCoord LeashTile { get; }
    public int LeashRadius { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public EngageUnitIntent(int unitId, int targetUnitId, TileCoord leashTile, int leashRadius)
    {
        UnitId = unitId;
        TargetUnitId = targetUnitId;
        LeashTile = leashTile;
        LeashRadius = leashRadius;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        // Group movement is owned by the group, not the unit — a member
        // chasing off on its own would desync the formation.
        if (Sim.Core.Groups.GroupRules.UnderCommand(world, unit))
            return IntentOutcome.Reject($"unit {UnitId} is in group {unit.GroupId}");
        if (unit.CargoAmount > 0)
            return IntentOutcome.Reject(
                $"unit {UnitId} is carrying {unit.CargoAmount} {unit.CargoResource} — a laden unit does not give chase");

        if (UnitId == TargetUnitId)
            return IntentOutcome.Reject("a unit cannot pursue itself");
        if (!world.Units.TryGetValue(TargetUnitId, out var target))
            return IntentOutcome.Reject($"target {TargetUnitId} does not exist");
        if (target.IsEmbarked)
            return IntentOutcome.Reject($"target {TargetUnitId} is aboard a boat");

        // HOSTILE ONLY — and hostility is the diplomacy layer's word, never
        // this intent's guess. A patrol must not be able to start a war by
        // walking into a neutral's scout (docs/patrols.md); declaring war is a
        // player act with a telegraph.
        if (!world.Diplomacy.AreHostile(unit.OwnerId, target.OwnerId))
            return IntentOutcome.Reject(
                $"player {unit.OwnerId} is not at war with player {target.OwnerId}");

        // THE FOG CONTRACT: you may only chase what this unit can SEE. Checked
        // against the pursuer's own eyes, matching the per-hop rule that keeps
        // the chase running.
        if (PursuitRules.Chebyshev(unit.Position, target.Position) > Sight.RadiusFor(unit.Role))
            return IntentOutcome.Reject($"target {TargetUnitId} is not within sight of unit {UnitId}");

        if (LeashRadius < 0)
            return IntentOutcome.Reject("leash radius must be >= 0");
        if (!world.Grid.InBounds(LeashTile))
            return IntentOutcome.Reject($"leash tile {LeashTile.X},{LeashTile.Y} out of bounds");

        // Replaces any prior commitment: a haul is abandoned (cargo already
        // rejected above, so nothing is stranded), and a previous chase is
        // simply retargeted.
        unit.HaulPlan = null;
        unit.Pursuit = new Pursuit(TargetUnitId, LeashTile, LeashRadius);
        // Fence any arrival already queued from whatever this unit was doing,
        // so a stale hop can't land after the chase re-paths.
        unit.BumpEpoch();

        PursuitRules.Step(sim, unit);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"Engage(unit={UnitId} -> target={TargetUnitId}, leash={LeashRadius} from {LeashTile.X},{LeashTile.Y})";
}
