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

        // Groups in id order. Each contributes at most one queued
        // GroupArrivalEvent based on its anchor.
        foreach (var (_, group) in world.Groups)
            RegenerateGroupMoveAnchor(sim, group);

        // M6: relationships with pending hostile transitions. Each contributes
        // at most one queued WarBecomesEffectiveEvent. Iterated in canonical
        // pair-key order (SortedDictionary).
        foreach (var rel in world.Diplomacy.Relationships.Values)
        {
            if (rel.PendingEffectiveTick is not { } tick) continue;
            if (rel.PendingSeq is not { } seq) continue;
            sim.ScheduleWithSeq(tick, seq, new Sim.Core.Diplomacy.WarBecomesEffectiveEvent(rel.Pair));
        }

        // M7: contested tiles with a pending combat round. Each tile in
        // world.CombatStates contributes one queued CombatRoundEvent,
        // restored at its original (NextRoundTick, NextRoundSeq).
        var combatStates = world.CombatStates.Values
            .OrderBy(s => s.Tile.Y).ThenBy(s => s.Tile.X)
            .ToList();
        foreach (var state in combatStates)
        {
            sim.ScheduleWithSeq(state.NextRoundTick, state.NextRoundSeq,
                new Sim.Core.Combat.CombatRoundEvent(state.Tile));
        }

        // M8: pending old-age deaths. Each unit with (DeathTick, DeathSeq)
        // contributes one queued DeathByAgeEvent, restored at its original
        // anchor. Iterated in canonical id order (SortedDictionary).
        foreach (var (_, unit) in world.Units)
        {
            if (unit.DeathTick is not { } tick) continue;
            if (unit.DeathSeq is not { } seq) continue;
            sim.ScheduleWithSeq(tick, seq,
                new Sim.Core.Population.DeathByAgeEvent(unit.Id));
        }

        // M8: pending births. Each House with non-null Occupation
        // contributes one queued BirthEvent, restored at its original
        // (BirthTick, BirthSeq). Iterated in canonical (y, x) order.
        var houses = world.Structures.Values.OfType<House>()
            .OrderBy(h => h.At.Y).ThenBy(h => h.At.X)
            .ToList();
        foreach (var h in houses)
        {
            if (h.Occupation is not { } occ) continue;
            sim.ScheduleWithSeq(occ.BirthTick, occ.BirthSeq,
                new Sim.Core.Population.BirthEvent(h.At));
        }

        // M13: castle famine-check and starvation-death anchors.
        // Each Castle contributes at most two queued events, restored at
        // their original (Tick, Seq). Iterated in canonical (y, x) order.
        var castles = world.Structures.Values.OfType<Castle>()
            .OrderBy(c => c.At.Y).ThenBy(c => c.At.X)
            .ToList();
        foreach (var castle in castles)
        {
            if (castle.NextFamineCheckTick is { } famAt
                && castle.NextFamineCheckSeq is { } famSeq)
            {
                sim.ScheduleWithSeq(famAt, famSeq,
                    new Sim.Core.Food.FamineCheckEvent(castle.At));
            }
            if (castle.NextStarvationDeathTick is { } starvAt
                && castle.NextStarvationDeathSeq is { } starvSeq)
            {
                sim.ScheduleWithSeq(starvAt, starvSeq,
                    new Sim.Core.Food.StarvationDeathEvent(castle.At));
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

    private static void RegenerateGroupMoveAnchor(Simulation sim, Group group)
    {
        if (group.NextArrivalTick is not { } at) return;
        if (group.NextArrivalSeq is not { } seq) return;
        if (group.PathRemaining is null || group.PathRemaining.Count == 0) return;
        if (group.PathFinalDest is not { } dest) return;

        var to = group.PathRemaining[0];
        sim.ScheduleWithSeq(at, seq,
            new GroupArrivalEvent(group.Id, to, dest, group.MovementEpoch));
    }
}
