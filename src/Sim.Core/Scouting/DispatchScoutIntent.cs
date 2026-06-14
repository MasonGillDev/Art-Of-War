namespace Sim.Core.Scouting;

// M20 Phase 3 — send an idle scout on a reconnaissance sortie: a list of
// waypoints to ride and a rule for when to turn back. Gated by a completed,
// owned Lodge. Creates the durable ScoutMission (the observation log fills as
// the scout travels) and launches the march; ScoutMissionRunner sequences the
// rest in-sim. See docs/m20-scouting-reports-spec.md.
//
// Validated at resolution time (the standing contract): a rejected dispatch
// mutates nothing.
public sealed class DispatchScoutIntent : Intent
{
    public int ScoutUnitId { get; }
    public List<TileCoord> Waypoints { get; }
    public ScoutReturnRule ReturnRule { get; }
    public long ElapsedLimitTicks { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DispatchScoutIntent(
        int scoutUnitId, List<TileCoord> waypoints,
        ScoutReturnRule returnRule = ScoutReturnRule.WaypointsExhausted,
        long elapsedLimitTicks = 0)
    {
        ScoutUnitId = scoutUnitId;
        Waypoints = waypoints ?? new List<TileCoord>();
        ReturnRule = returnRule;
        ElapsedLimitTicks = elapsedLimitTicks;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(ScoutUnitId, out var scout))
            return IntentOutcome.Reject($"unit {ScoutUnitId} does not exist");
        if (scout.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {ScoutUnitId} not owned by player {PlayerId}");
        if (scout.Role != UnitRole.Scout)
            return IntentOutcome.Reject($"unit {ScoutUnitId} is not a Scout");
        if (scout.IsEmbarked)
            return IntentOutcome.Reject($"unit {ScoutUnitId} is embarked");
        if (scout.GroupId is not null)
            return IntentOutcome.Reject($"unit {ScoutUnitId} is in a group");
        if (scout.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {ScoutUnitId} is busy ({scout.Activity})");
        if (Waypoints.Count == 0)
            return IntentOutcome.Reject("a dispatch needs at least one waypoint");
        if (Waypoints.Count > ScoutConstants.MaxWaypoints)
            return IntentOutcome.Reject($"too many waypoints (max {ScoutConstants.MaxWaypoints})");
        foreach (var wp in Waypoints)
            if (!world.Grid.InBounds(wp))
                return IntentOutcome.Reject($"waypoint {wp.X},{wp.Y} out of bounds");
        if (ReturnRule == ScoutReturnRule.ElapsedTicks && ElapsedLimitTicks <= 0)
            return IntentOutcome.Reject("ElapsedTicks rule needs a positive time budget");

        // Gate: the owner must have a COMPLETED, owned Lodge.
        var hasLodge = false;
        foreach (var s in world.Structures.Values)
            if (s.Kind == StructureKind.Lodge && s.OwnerId == PlayerId) { hasLodge = true; break; }
        if (!hasLodge)
            return IntentOutcome.Reject("no intelligence Lodge built");

        // Create the mission (replacing any prior sortie by this scout — the
        // report store, not the sim, keeps history) and launch the march.
        var mission = new ScoutMission
        {
            ScoutUnitId = ScoutUnitId,
            OwnerId = PlayerId,
            DispatchTick = sim.Now,
            State = ScoutMissionState.Active,
            HomeTile = scout.Position,
            ReturnRule = ReturnRule,
            ElapsedLimitTicks = ElapsedLimitTicks,
            WaypointCursor = 0,
        };
        mission.Waypoints.AddRange(Waypoints);
        world.ScoutMissions[ScoutUnitId] = mission;

        ScoutMissionRunner.Advance(sim, scout);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"DispatchScout(unit={ScoutUnitId}, {Waypoints.Count} waypoints, rule={ReturnRule})";
}
