namespace Sim.Core.Persistence;

// M4 Phase A: rebuild the in-flight event queue from pure state.
//
// Called by Snapshot.Restore after the state is loaded but before the sim
// runs. Iterates entities in canonical order, reads each in-flight process's
// next-event anchor (next-tick + stored Seq), and schedules the matching
// event with its original Seq via Simulation.ScheduleWithSeq.
//
// The Seq preservation is what keeps same-tick ordering intact across
// recovery — the M1 Phase F fairness contract (who wins contention by
// submission order) must survive crash+restart.
//
// PURE-READ from sim state's perspective: this reads the world; it never
// changes any entity field. Its only mutation is enqueuing events.
public static class RegenerateQueue
{
    public static void From(Simulation sim)
    {
        var world = sim.World;

        // Units in id order (matches snapshot canonical order). Each unit can
        // contribute at most one queued event — its NextArrivalTick.
        foreach (var (id, unit) in world.Units)
        {
            RegenerateUnitMoveAnchor(sim, unit);
        }

        // Structures in (y, x) order. Each contributes at most one queued
        // event based on its kind.
        var structures = world.Structures.Values
            .OrderBy(s => s.At.Y).ThenBy(s => s.At.X)
            .ToList();
        foreach (var s in structures)
        {
            switch (s)
            {
                case Extractor ex:        RegenerateExtractorAnchor(sim, ex); break;
                case ConstructionSite cs: RegenerateConstructionAnchor(sim, cs); break;
                // Storage / Tower: no in-flight event of their own.
            }
        }
    }

    private static void RegenerateUnitMoveAnchor(Simulation sim, Unit unit)
    {
        if (unit.NextArrivalTick is not { } at) return;
        if (unit.NextArrivalSeq is not { } seq) return;
        if (unit.PathRemaining is null || unit.PathRemaining.Count == 0) return;
        if (unit.PathFinalDest is not { } dest) return;

        var to = unit.PathRemaining[0];
        var ev = new MoveArrivalEvent(unit.Id, to, dest, unit.AssignmentEpoch);
        sim.ScheduleWithSeq(at, seq, ev);
    }

    private static void RegenerateExtractorAnchor(Simulation sim, Extractor ex)
    {
        if (!ex.TickArmed) return;
        if (ex.NextProductionTickSeq is not { } seq) return;
        var at = ex.LastProductionTick + ex.Spec.ProductionPeriodTicks;
        sim.ScheduleWithSeq(at, seq, new ProductionTickEvent(ex.At));
    }

    private static void RegenerateConstructionAnchor(Simulation sim, ConstructionSite site)
    {
        if (site.ScheduledCompletion is not { } at) return;
        if (site.BuildCompleteSeq is not { } seq) return;
        sim.ScheduleWithSeq(at, seq, new BuildCompleteEvent(site.At));
    }
}
