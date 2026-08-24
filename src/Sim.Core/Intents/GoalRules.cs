using Sim.Core.World;

namespace Sim.Core.Intents;

// M30 — the goal engine (docs/goal-shaped-intents.md).
//
// THE PRINCIPLE: an intent expresses a goal; the sim handles every mechanical
// step between now and that goal. Travel is a step. Waiting for materials, a
// partner, or food is a step. None of them are decisions, so none of them
// belong to the player as an appointment to keep.
//
// THE BOUNDARY this file must not cross: goal-completion finishes executing a
// decision the player already made. It never makes a new one. No participant
// is ever chosen here, no substitute is ever found for a dead one, and nothing
// is ever re-fired after it completes. Those are the automation tier's
// features, and they stay locked (docs/automation-as-core-game.md).
//
// LIFECYCLE. Begin binds the anchor and either completes on the spot (the unit
// was already standing there) or walks the unit over. MoveArrivalEvent calls
// OnArrival at the end of the walk, which either completes the goal or parks
// the unit in Activity.Waiting. Waiting goals are woken by state-change events
// that already exist (a partner arriving, food landing in the house) — never
// by polling. Every exit path runs through Dissolve or Complete, both of which
// leave the unit Idle and the anchors clear.
public static class GoalRules
{
    // Bind `goal` to `unit` and start executing it. Returns false if the goal
    // could not even be started (unreachable target, already-impossible
    // precondition) — the caller treats that like any other per-id skip.
    //
    // The unit must be Idle: every caller has already gated on it, and a
    // non-Idle body belongs to whatever intent chain owns it.
    public static bool Begin(Simulation sim, Unit unit, GoalPlan goal)
    {
        if (unit.Activity != Activity.Idle) return false;
        if (!sim.World.Structures.ContainsKey(goal.TargetTile)) return false;

        unit.Goal = goal;

        if (unit.Position == goal.TargetTile)
        {
            // Already there — no travel step to execute. This is the
            // pre-M30 path, reached through the same door as the walk so
            // there is exactly one completion implementation.
            OnArrival(sim, unit);
            return true;
        }

        // Idle → Idle move: bump explicitly so any prior movement chain's
        // MoveArrivalEvents fence out, exactly as MoveIntent.Resolve does.
        unit.BumpEpoch();
        MoveIntent.BeginMove(sim, unit, goal.TargetTile);

        if (unit.PathRemaining is null)
        {
            // No route (or the target is unreachable through fog-free ground
            // truth). A goal that can never start must not linger as a silent
            // anchor — dissolve now, while the player is still looking at the
            // intent they just fired.
            Dissolve(sim, unit, "unreachable");
            return false;
        }
        return true;
    }

    // Called by MoveArrivalEvent when a unit carrying a goal finishes its
    // walk, and by Begin for a unit that was already standing on the target.
    public static void OnArrival(Simulation sim, Unit unit)
    {
        if (unit.Goal is not { } goal) return;

        // The walk may have outlived the target: razed, replaced, or (for a
        // build goal) already completed into the real structure.
        if (!sim.World.Structures.TryGetValue(goal.TargetTile, out var structure))
        {
            Dissolve(sim, unit, "target gone");
            return;
        }
        if (structure.OwnerId != unit.OwnerId)
        {
            Dissolve(sim, unit, "target no longer ours");
            return;
        }

        switch (goal.Kind)
        {
            case GoalKind.AssignWorker:
                if (structure is not Extractor extractor)
                {
                    Dissolve(sim, unit, "target is not a workplace");
                    return;
                }
                if (WorkAssignment.TryAssignWorker(sim, extractor, unit))
                {
                    Complete(unit);
                    WorkAssignment.ArmIfNewlyRunnable(sim, extractor);
                }
                else
                {
                    // The only non-malformed reason left is a full roster,
                    // and nothing will free a slot on a schedule the sim can
                    // wake on. Waiting here would be a silent stall, so the
                    // goal dissolves and the body is handed back.
                    Dissolve(sim, unit, "no work slot free");
                }
                return;

            case GoalKind.AssignBuilder:
                if (structure is not ConstructionSite site)
                {
                    Dissolve(sim, unit, "target is not a construction site");
                    return;
                }
                if (WorkAssignment.TryAssignBuilder(sim, site, unit))
                {
                    Complete(unit);
                    // The other half of the conjunction may already be
                    // satisfied — materials could have arrived while this
                    // builder was walking. Same call the deposit path makes.
                    if (!site.IsActive && site.ConditionsMet(sim.World))
                        site.StartOrResume(sim);
                }
                else
                {
                    Dissolve(sim, unit, "cannot build here");
                }
                return;

            case GoalKind.Train:
            {
                var role = (UnitRole)goal.Arg;
                // Re-check on arrival, not just at firing: the school may have
                // been razed and rebuilt as something else, the unit may have
                // aged into or out of eligibility, the owner may have changed.
                if (Sim.Core.Population.TrainingRules.Blocker(sim, unit, role) is { } why)
                {
                    Dissolve(sim, unit, why);
                    return;
                }
                if (Sim.Core.Population.TrainingRules.Train(sim, unit, role))
                    Complete(unit);
                else
                    Dissolve(sim, unit, "no trainer here for that role");
                return;
            }

            case GoalKind.Breed:
                if (structure is not House house)
                {
                    Dissolve(sim, unit, "target is not a house");
                    return;
                }
                Sim.Core.Population.BreedGoal.OnArrival(sim, unit, house);
                return;

            default:
                Dissolve(sim, unit, "unknown goal");
                return;
        }
    }

    // The goal did what it was fired to do. The anchor clears; the unit keeps
    // whatever activity the completion put it in (Working, Building, …).
    internal static void Complete(Unit unit) => unit.Goal = null;

    // Clean teardown, used by every failure path. The unit ends Idle and
    // anchor-free wherever it happens to be standing — never mid-path, never
    // holding a reservation. TrySetActivity(Idle) bumps the epoch, which
    // stales any arrival still in flight for this chain.
    public static void Dissolve(Simulation sim, Unit unit, string reason)
    {
        if (unit.Goal is not { } goal) return;
        unit.Goal = null;
        unit.PathRemaining = null;
        unit.PathFinalDest = null;
        unit.NextArrivalTick = null;
        unit.NextArrivalSeq = null;
        unit.TrySetActivity(Activity.Idle);
        ReleaseRegistration(sim, unit, goal, reason);
        sim.Schedule(sim.Now, new GoalDissolvedEvent(unit.Id, goal.Kind, goal.TargetTile, reason));
    }

    // Some goals reserve something on the target structure while they are in
    // flight. Tearing that reservation down is part of dissolving, and it must
    // happen for EVERY exit path — which is why it lives inside Dissolve
    // rather than at the call sites.
    //
    // A breeding pair dissolves TOGETHER: one parent walking away ends the
    // match, and the sim does not go looking for a replacement (that is the
    // automation tier's auto-replacement feature, deliberately absent here —
    // docs/goal-shaped-intents.md §"No silent substitution"). The recursion
    // terminates because Dissolve clears unit.Goal before calling this, so the
    // partner's own teardown finds nothing left to tear down.
    private static void ReleaseRegistration(Simulation sim, Unit unit, GoalPlan goal, string reason)
    {
        if (goal.Kind != GoalKind.Breed) return;
        if (!sim.World.Structures.TryGetValue(goal.TargetTile, out var s) || s is not House house) return;
        if (house.PendingBreed is not { } pending || !pending.ContainsParent(unit.Id)) return;

        house.PendingBreed = null;
        var otherId = pending.OtherParent(unit.Id);
        if (sim.World.Units.TryGetValue(otherId, out var other) && other.Goal is not null)
            Dissolve(sim, other, "the match was broken");
    }

    // Every structure-removal path (raze, build-completion replacing a site)
    // funnels here so no unit is left walking toward a tile whose structure
    // is gone or has become something else. Walking units are cheap to catch
    // late — they re-check on arrival — but a WAITING unit has nothing left
    // to wake it, so it must be released now.
    public static void OnStructureRemoved(Simulation sim, TileCoord tile, string reason = "target gone")
    {
        List<Unit>? affected = null;
        foreach (var u in sim.World.Units.Values)
        {
            if (u.Goal is { } g && g.TargetTile == tile)
                (affected ??= new List<Unit>()).Add(u);
        }
        if (affected is null) return;
        // Canonical order: dissolution mutates units, and a deterministic
        // sequence keeps twin runs byte-identical.
        affected.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        foreach (var u in affected) Dissolve(sim, u, reason);
    }

    // How many units are already walking toward `tile` for `kind`. Used as an
    // ADVISORY cap check at dispatch time so a cap-1 extractor doesn't
    // attract five hopefuls who all dissolve on arrival. Ground truth is
    // still re-checked on arrival, per docs/intent-validation.md.
    public static int PendingCountFor(GameWorld world, TileCoord tile, GoalKind kind)
    {
        var n = 0;
        foreach (var u in world.Units.Values)
            if (u.Goal is { } g && g.Kind == kind && g.TargetTile == tile) n++;
        return n;
    }
}
