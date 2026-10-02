using Sim.Core.World;

namespace Sim.Core.Logistics;

// Releasing a unit from whatever it was assigned to.
//
// EVERY path that takes a unit off a job must go through here, because the
// structure keeps its OWN roster (Extractor.Workers, a site's builder count)
// and those rosters are load-bearing:
//
//   * Extractor.Workers.Count is checked against WorkerCap at assign time, so
//     a stale entry permanently consumes a work slot;
//   * the WorkersBelow/WorkersAtLeast predicates read the same count, so a
//     stale entry makes a Staff order believe the building is fully manned
//     and it will never re-staff it;
//   * ProductionTickEvent reschedules while Workers.Count > 0, so a roster of
//     ghosts keeps a dead building ticking at zero rate forever.
//
// This was originally private to MoveIntent, which meant the MOVE path
// released assignments and the DEATH path did not: a worker who starved or
// fell in combat stayed on the payroll for the rest of the game, and the
// building they had staffed could never be worked again. Shared here so the
// two can never drift apart again.
public static class WorkAssignment
{
    // ---- M30: the two per-unit ASSIGN implementations ----
    //
    // These live here, beside Release, for the same reason Release exists: the
    // roster is load-bearing, and there is now more than one road to a job.
    // The intent applies them to a unit already standing on the tile; the goal
    // engine applies them to a unit that has just walked there
    // (docs/goal-shaped-intents.md). One implementation, so the immediate path
    // and the walked path can never mean different things.
    //
    // Each returns false rather than throwing: callers treat a false exactly
    // like the per-id skip they already had. Position IS checked here — both
    // roads end with the body on the tile, and a caller that forgot would
    // otherwise teleport a job.

    public static bool TryAssignWorker(Simulation sim, Extractor extractor, Unit unit)
    {
        if (unit.Position != extractor.At) return false;
        if (unit.OwnerId != extractor.OwnerId) return false;
        if (Sim.Core.Groups.GroupRules.UnderCommand(sim.World, unit)) return false;
        if (unit.IsEmbarked) return false;
        if (unit.Activity != Activity.Idle) return false;
        if (extractor.Workers.Count >= extractor.Spec.WorkerCap) return false;
        // M8: training-age gate — extractor workers are role-tied assignments
        // (the role bonus affects rate). Children can't be worker-assigned;
        // they can still haul to the camp.
        if (!Sim.Core.Population.Population.CanTrain(unit, sim.Now, sim.World.PopulationConfig))
            return false;
        if (!unit.TrySetActivity(Activity.Working, extractor.At)) return false;

        extractor.Workers.Add(unit.Id);
        ReHomeNearWork(sim, unit, extractor.At);
        return true;
    }

    public static bool TryAssignBuilder(Simulation sim, ConstructionSite site, Unit unit)
    {
        if (unit.Position != site.At) return false;
        if (unit.OwnerId != site.OwnerId) return false;
        if (Sim.Core.Groups.GroupRules.UnderCommand(sim.World, unit)) return false;
        if (unit.IsEmbarked) return false;
        if (unit.Role != UnitRole.Builder) return false;
        if (unit.Activity != Activity.Idle) return false;
        if (!Sim.Core.Population.Population.CanTrain(unit, sim.Now, sim.World.PopulationConfig))
            return false;
        if (!unit.TrySetActivity(Activity.Building, site.At)) return false;

        ReHomeNearWork(sim, unit, site.At);
        return true;
    }

    // M19 — auto-assignment trigger 2 (home follows work): the worker re-homes
    // to the nearest house with a free bed near the workplace; none in radius
    // → home stays. Their CURRENT home qualifies even when full (they already
    // hold one of its beds). A starving house never qualifies: the worker
    // keeps their old home rather than joining a famine.
    private static void ReHomeNearWork(Simulation sim, Unit unit, TileCoord workplace)
    {
        if (Sim.Core.Population.Population.NearestHouseWithBed(sim.World, unit.OwnerId, workplace,
                Sim.Core.Food.FoodConsumptionConstants.HomeAssignRadius, sim.Now, unit.Home)
            is { } bed)
            Sim.Core.Population.Population.SetHome(sim, unit, bed.At);
    }

    // Arm an extractor that has become runnable (worked, buffer not full, not
    // already armed). Idempotent; safe to call after every assignment.
    public static void ArmIfNewlyRunnable(Simulation sim, Extractor extractor)
    {
        if (extractor.TickArmed) return;
        if (extractor.Workers.Count == 0) return;
        if (extractor.BufferFull()) return;
        extractor.ArmIfDormant(sim);
    }

    // Take `unit` off its current job and update the structure's roster.
    // Safe on an unassigned unit (no-op) and on a unit about to be removed
    // from the world — the death path calls it just before removal.
    public static void Release(Simulation sim, Unit unit)
    {
        var prevAssignment = unit.Assignment;
        var prevActivity = unit.Activity;

        // Idle FIRST so the structure-side checks below see the updated
        // builder/worker count.
        unit.TrySetActivity(Activity.Idle);

        if (prevAssignment is not TileCoord at) return;
        if (!sim.World.Structures.TryGetValue(at, out var s)) return;

        switch (prevActivity)
        {
            case Activity.Working when s is Extractor ex:
                ex.Workers.Remove(unit.Id);
                // Production goes dormant naturally on the next ProductionTick
                // fire (it sees Workers.Count and decides). No intervention.
                break;
            case Activity.Building when s is ConstructionSite site:
                // If this builder leaving drops the site below requirement,
                // pause the build. The previously-scheduled BuildCompleteEvent
                // will fence via site.ScheduledCompletion when it fires.
                if (site.IsActive && site.BuildersPresent(sim.World) < site.RequiredBuilderCount)
                    site.Pause(sim.Now);
                break;
        }
    }
}
