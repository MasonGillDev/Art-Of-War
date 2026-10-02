namespace Sim.Core.Intents;

// RETASKING — "the player has restated where this body should be."
//
// MoveIntent has always been authoritative: a march pulls a unit off whatever
// it was doing (a post, a build, a haul, a goal in flight) and the structure
// that depended on it gets cleaned up. The goal-shaped assign intents used to
// be the opposite — they skipped every non-Idle unit — so a busy builder sent
// to a new site (found in play, 2026-10-01) simply did not move, while the
// same builder sent to the empty tile next door did. Both gestures are the
// same decision, so both now go through here.
//
// The one thing a retask never overrides is a breeding cycle already conceived
// (docs/goal-shaped-intents.md); grouped and embarked bodies are not solo
// bodies and are refused as before.
public static class Retask
{
    // Why a solo order cannot take this unit, or null when it can.
    public static string? Refusal(Simulation sim, Unit unit)
    {
        if (unit.GroupId is not null)
            return $"unit {unit.Id} is in group {unit.GroupId}";
        // M12 — embarked units are off-tile passengers; solo intents are
        // blocked until the boat disembarks them.
        if (unit.IsEmbarked)
            return $"unit {unit.Id} is embarked on boat {unit.EmbarkedOn}";
        // M8 follow-up: breeding is a commitment, not a retaskable assignment.
        // A parent in an active cycle is locked to the house until BirthEvent
        // (or stop-on-removal) frees them. No cancel — the player can't back out.
        if (Sim.Core.Population.Population.GetActiveBreedingFor(sim.World, unit.Id) is { } house)
            return $"unit {unit.Id} is breeding at house ({house.At.X},{house.At.Y}) and cannot be moved";
        return null;
    }

    // Pull the unit off whatever it is doing so a new order can anchor. Ends
    // Idle, goal-free, plan-free, with the epoch bumped so every pending event
    // of the old task fences out. Cargo stays aboard.
    public static void Release(Simulation sim, Unit unit)
    {
        // M30 — a new order countermands a goal. Dissolving here (rather than
        // letting the anchor ride) is what keeps a countermanded goal from
        // firing on arrival at the NEW destination.
        if (unit.Goal is not null)
            GoalRules.Dissolve(sim, unit, "countermanded by a new order");
        // M44 — likewise a survey in flight (walking or digging).
        if (unit.Survey is not null)
            Sim.Core.Mining.SurveyRules.Cancel(sim, unit, "countermanded by a new order");

        // Move-on-busy: any structure depending on this unit gets cleaned up.
        // Pending per-unit events from the old task fence on AssignmentEpoch
        // and no-op when they fire.
        if (unit.Activity != Activity.Idle)
            WorkAssignment.Release(sim, unit);   // bumps epoch via TrySetActivity(Idle)
        else
            unit.BumpEpoch();                               // Idle→Idle: fence any prior walk explicitly

        // M36 — a countermanded haul is OVER. Its plan used to ride along: the
        // unit arrived somewhere else, went Idle, and kept a dead HaulPlan
        // forever — every "is this hauler free?" test read it as busy. Found in
        // play: a breed order walked a laden queue hauler into a house mid-trip.
        // The cargo stays aboard (unload or haul it later), only the plan goes.
        unit.HaulPlan = null;
    }
}
