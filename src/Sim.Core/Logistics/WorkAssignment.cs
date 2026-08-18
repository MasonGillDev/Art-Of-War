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
