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
        // contribute at most one queued event — its next walk step.
        foreach (var (id, unit) in world.Units)
        {
            // M43 — a unit on a walk owes one step event.
            if (unit.SubtileRouteTick is { } routeAt && unit.SubtileRouteSeq is { } routeSeq)
                sim.ScheduleWithSeq(routeAt, routeSeq, new Sim.Core.Battlefields.SubtileRouteStepEvent(unit.Id));
            // M44 — a Miner digging at a survey slope owes his report.
            if (unit.Survey is { CompleteTick: { } surveyAt, CompleteSeq: { } surveySeq })
                sim.ScheduleWithSeq(surveyAt, surveySeq, new Sim.Core.Mining.SurveyCompleteEvent(unit.Id));
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

        // Groups have no events of their own (M43): a group moves as its members' walks.

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

        // M41: open battlefields with a turn queued (a suspended board has
        // none; an order or an arrival wakes it). World.Battlefields iterates
        // in canonical (y, x) order.
        foreach (var bf in world.Battlefields.Values)
        {
            if (bf.Suspended) continue;
            sim.ScheduleWithSeq(bf.NextTurnTick, bf.NextTurnSeq,
                new Sim.Core.Battlefields.BattlefieldTurnEvent(bf.Tile));
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
            if (h.Occupation is { } occ)
            {
                sim.ScheduleWithSeq(occ.BirthTick, occ.BirthSeq,
                    new Sim.Core.Population.BirthEvent(h.At));
                continue;
            }

            // M30 (v27): a house RESERVED by a pair that hasn't conceived yet
            // owes one GoalExpiryEvent -- the deadline that stops a pair
            // waiting on food that never comes from waiting forever. Rebuilt
            // at its original Seq, same discipline as the birth above; without
            // it the restored pair would stall for life, which is precisely
            // the failure the event exists to prevent.
            if (h.PendingBreed is { } pending && pending.ExpirySeq != 0)
                sim.ScheduleWithSeq(pending.ExpiryTick, pending.ExpirySeq,
                    new Sim.Core.Population.GoalExpiryEvent(
                        h.At, pending.ParentAId, pending.ParentBId));
        }

        // M12: Dock boat-production anchors.
        var docks = world.Structures.Values.OfType<Dock>()
            .OrderBy(d => d.At.Y).ThenBy(d => d.At.X)
            .ToList();
        foreach (var dock in docks)
        {
            if (!dock.ProductionArmed) continue;
            if (dock.NextProductionTickSeq is not { } seq) continue;
            var fireAt = dock.LastProductionTick
                + Sim.Core.World.StructureCatalog.Spec(Sim.Core.World.StructureKind.Dock).ProductionPeriodTicks;
            sim.ScheduleWithSeq(fireAt, seq,
                new Sim.Core.Boats.BoatProductionTickEvent(dock.At));
        }

        // M13/M19: food-home famine-check and starvation-death anchors —
        // every IFoodHome (Castle + House) contributes at most two queued
        // events, restored at their original (Tick, Seq). Iterated in
        // canonical (y, x) order.
        var homes = world.Structures.Values.OfType<Sim.Core.Food.IFoodHome>()
            .OrderBy(h => h.At.Y).ThenBy(h => h.At.X)
            .ToList();
        foreach (var home in homes)
        {
            if (home.NextFamineCheckTick is { } famAt
                && home.NextFamineCheckSeq is { } famSeq)
            {
                sim.ScheduleWithSeq(famAt, famSeq,
                    new Sim.Core.Food.FamineCheckEvent(home.At));
            }
            if (home.NextStarvationDeathTick is { } starvAt
                && home.NextStarvationDeathSeq is { } starvSeq)
            {
                sim.ScheduleWithSeq(starvAt, starvSeq,
                    new Sim.Core.Food.StarvationDeathEvent(home.At));
            }
        }

        // M39: bandit camps' daily ticks, in (y, x) order.
        foreach (var camp in world.Structures.Values.OfType<BanditCamp>().OrderBy(c => c.At.Y).ThenBy(c => c.At.X))
            sim.ScheduleWithSeq(camp.NextTickAt, camp.NextTickSeq, new Sim.Core.Bandits.CampTickEvent(camp.At));

        // M38: live idol vision grants' expiries, in id order.
        foreach (var (_, g) in world.VisionGrants)
            sim.ScheduleWithSeq(g.EndsTick, g.EndSeq, new Sim.Core.Scouting.VisionGrantExpiryEvent(g.GrantId));

        // M37: pending omens' countdowns, in id order.
        foreach (var (_, omen) in world.Omens)
        {
            if (omen.State != Sim.Core.Progression.OmenState.Pending) continue;   // Seq 0 is a real Seq
            sim.ScheduleWithSeq(omen.DueTick, omen.DueSeq,
                new Sim.Core.Progression.OmenDueEvent(omen.OmenId));
        }

        // Two-act pacing: the landing, while it is still to come.
        if (world.LandingSeq is { } landingSeq)
            sim.ScheduleWithSeq(world.LandingConfig.Tick, landingSeq, new Sim.Core.Landing.LandingEvent());
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
