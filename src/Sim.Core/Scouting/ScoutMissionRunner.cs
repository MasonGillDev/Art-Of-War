namespace Sim.Core.Scouting;

// M20 Phase 3 — drives a scout through its waypoints and home again, entirely
// IN-SIM. This follows the HaulPlan precedent (multi-hop unit behaviour
// sequenced by MoveArrivalEvent, see MoveArrivalEvent.DispatchOnFinalArrival),
// NOT a server-side driver: because every transition is a deterministic
// consequence of an in-sim arrival event, replay-from-intent-log and
// mid-mission snapshot/restore are correct for free (the mission's plan +
// cursor + state are all snapshotted; the in-flight move regenerates from the
// unit's anchors).
//
// Recurring patrols ("ride the northern pass weekly") layer ON TOP as M18
// automation — a standing order whose action re-issues DispatchScoutIntent.
// That ActionKind is deferred; this milestone ships the single sortie.
//
// MUTATION CONTRACT (docs/determinism-audit.md): the mission's State /
// WaypointCursor are written ONLY here; creation is DispatchScoutIntent; the
// Legs are ScoutObservation.Capture. All three are event/intent-driven.
public static class ScoutMissionRunner
{
    // The scout is standing on a target tile (its dispatch tile at launch, or a
    // reached waypoint/home at arrival). Update the mission and march toward
    // the next target. ONE entry point for both the dispatch intent and the
    // arrival hook — Advance figures out "already there vs needs a move".
    public static void Advance(Simulation sim, Unit scout)
    {
        if (!sim.World.ScoutMissions.TryGetValue(scout.Id, out var m)) return;
        if (m.State == ScoutMissionState.Returned) return;
        // A waypoint-less mission is "manual / log-only": no autopilot, the
        // player drives the scout by hand and the log still fills on every
        // arrival. DispatchScoutIntent always supplies >= 1 waypoint, so this
        // is the manual-observation path, not a normal sortie.
        if (m.State == ScoutMissionState.Active && m.Waypoints.Count == 0) return;
        Drive(sim, scout, m);
    }

    private static void Drive(Simulation sim, Unit scout, ScoutMission m)
    {
        // Bounded loop: each turn either schedules a real move (and returns to
        // wait for its arrival) or advances the cursor/state past a target
        // that needs no travel (already standing on it, or unreachable). The
        // cursor only climbs and Returning→Returned is terminal, so this
        // terminates in at most Waypoints.Count + 1 turns — no wedge.
        while (true)
        {
            if (m.State == ScoutMissionState.Returned) return;
            var dest = m.State == ScoutMissionState.Returning
                ? m.HomeTile
                : m.Waypoints[m.WaypointCursor];

            if (scout.Position != dest)
            {
                scout.BumpEpoch(); // fence any stale move events from a prior leg
                MoveIntent.BeginMove(sim, scout, dest);
                if (scout.PathRemaining is { Count: > 0 }) return; // marching; wait for arrival
                // else: unreachable — fall through and advance as if arrived
            }

            // Standing on `dest` (already, or because it was unreachable): the
            // scout has "reached" this target. Decide the next one.
            if (m.State == ScoutMissionState.Returning)
            {
                m.State = ScoutMissionState.Returned;
                return;
            }
            if (m.WaypointCursor >= m.Waypoints.Count - 1 || RecallRuleFired(sim, m))
                m.State = ScoutMissionState.Returning;
            else
                m.WaypointCursor++;
        }
    }

    private static bool RecallRuleFired(Simulation sim, ScoutMission m) => m.ReturnRule switch
    {
        ScoutReturnRule.ElapsedTicks   => sim.Now - m.DispatchTick >= m.ElapsedLimitTicks,
        ScoutReturnRule.HostileSighted => LastLegSawHostile(sim, m),
        _ => false, // WaypointsExhausted — the cursor backstop in Drive handles it
    };

    // Did the leg just captured at this waypoint show a hostile force? Reads
    // only the mission's own log (fog-honest) + diplomacy (public knowledge).
    private static bool LastLegSawHostile(Simulation sim, ScoutMission m)
    {
        if (m.Legs.Count == 0) return false;
        var leg = m.Legs[^1];
        foreach (var s in leg.Sightings)
            foreach (var u in s.Units)
                if (sim.World.Diplomacy.AreHostile(m.OwnerId, u.OwnerId))
                    return true;
        return false;
    }
}
