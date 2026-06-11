using Sim.Core.World;

namespace Sim.Core.Population;

// M8 — pure-read age derivations + the two write paths (ScheduleLifespan
// for new units, OnUnitRemoved for breeding cleanup on parent death).
//
// PURE-READ WALL: AgeYears, CanTrain, CanBreed read state and never
// mutate. Views and intent validation use them freely. ScheduleLifespan
// mutates Unit.DeathTick/DeathSeq and schedules an event — called ONLY
// from the spec-aware Simulation ctor (genesis units) and BirthEvent.Apply
// (children). No third caller; no post-hoc sweep.
public static class Population
{
    // Derived age — computed from BornTick, never stored. This is the
    // M8 invariant: nothing ticks per unit, age is a pure read.
    public static int AgeYears(Unit unit, long now, PopulationConfig config)
    {
        var ageTicks = now - unit.BornTick;
        if (ageTicks < 0) ageTicks = 0;
        // Compute in long and clamp. The adult-by-default sentinel
        // (BornTick = long.MinValue / 2) yields an astronomical year
        // count; a raw (int) cast would keep only the low 32 bits —
        // sign-garbage that flips with every TicksPerYear retune. The
        // clamp makes "ancient" read as int.MaxValue under ANY tuning.
        var years = ageTicks / config.TicksPerYear;
        return years > int.MaxValue ? int.MaxValue : (int)years;
    }

    public static bool CanTrain(Unit unit, long now, PopulationConfig config) =>
        AgeYears(unit, now, config) >= config.MinTrainAge;

    public static bool CanBreed(Unit unit, long now, PopulationConfig config)
    {
        var age = AgeYears(unit, now, config);
        return age >= config.MinFertileAge && age <= config.MaxFertileAge;
    }

    // M8 Phase B — roll a unit's lifespan ONCE from the sim's RNG and
    // schedule its DeathByAgeEvent. Idempotent: if DeathTick is already
    // set (e.g. snapshot restore re-applied), skip. Called from EXACTLY
    // two sites:
    //   1. The spec-aware Simulation ctor, for every genesis unit, in
    //      canonical (faction-id, unit-id) order.
    //   2. BirthEvent.Apply, for each newborn child, inside the
    //      deterministic event stream.
    // No third caller. A post-hoc sweep would consume RNG outside an
    // event and drift silently — see docs/population-model.md.
    public static void ScheduleLifespan(Simulation sim, Unit unit)
    {
        if (unit.DeathTick is not null) return; // already scheduled

        var cfg = sim.World.PopulationConfig;
        var span = cfg.LifespanMaxYears - cfg.LifespanMinYears + 1;
        var rolledYears = cfg.LifespanMinYears + sim.Rng.NextInt(span);
        var deathTick = unit.BornTick + (long)rolledYears * cfg.TicksPerYear;
        // Defensive floor: a Genesis unit configured at an age near
        // LifespanMaxYears could roll a DeathTick at or before sim.Now.
        // Give it at least 1 tick of life so the event has somewhere to
        // schedule.
        if (deathTick <= sim.Now) deathTick = sim.Now + 1;

        unit.DeathTick = deathTick;
        unit.DeathSeq = sim.Schedule(deathTick, new DeathByAgeEvent(unit.Id));
    }

    // M13 — sim-aware wrapper around world.AddUnit. Runs the food
    // consumption catch-up on the owner's castle (using the OLD population
    // rate, before the new unit is added) BEFORE the AddUnit increments
    // PopulationCount. This is the discipline that keeps the lazy field
    // observation-independent: every rate-changing event closes its
    // constant-rate window before the next one opens.
    //
    // Called from EXACTLY two sites:
    //   1. BirthEvent.Apply, for every newborn child.
    //   2. (Future) any runtime add — e.g. mob spawns, M12 boat
    //      passenger debarkation if it turns out to need it.
    // NEVER called from Snapshot.Restore — the restore path uses bare
    // world.AddUnit and the castle's LastFoodConsumedTick is restored
    // from the snapshot.
    // NEVER called from Genesis.Build — sim.Now is 0 at genesis time
    // and a catch-up from anchor 0 over 0 elapsed ticks is a no-op.
    public static Unit OnUnitAdded(Simulation sim, Unit unit)
    {
        var castle = Sim.Core.Food.FoodConsumption.FindCastleFor(sim.World, unit.OwnerId);
        if (castle is not null)
            Sim.Core.Food.FoodConsumption.CatchUp(castle, sim, sim.Now);
        // world.AddUnit increments PopulationCount; the catch-up above
        // already closed the old-rate window.
        var added = sim.World.AddUnit(unit);
        if (castle is not null)
            Sim.Core.Food.FoodConsumption.OnRateOrFoodChanged(castle, sim);
        return added;
    }

    // M8 Phase E + M13 — called from CombatRules.OnUnitDeath and
    // DeathByAgeEvent.Apply after the unit is removed from world.Units.
    // Two responsibilities:
    //   1. (M8) If the removed unit was a breeding parent, clear the
    //      house's Occupation (fencing the queued BirthEvent via anchor
    //      mismatch) and free the surviving parent. Food is NOT refunded —
    //      gestation consumed it, raids should bite.
    //   2. (M13) Catch up food consumption at the OLD rate, then
    //      decrement the owner's PopulationCount. This is the single
    //      decrement site; the audit test
    //      FoodConsumptionTests.PopulationCount_HasOneMutationPoint pins it.
    //
    // NOTE the order: catch-up FIRST (closes the old-rate window),
    // then decrement. CombatRules.OnUnitDeath removes the unit from
    // world.Units BEFORE calling this helper — that's fine, the
    // population count still includes the dying unit at this point.
    //
    // Combat code, Aging code, and (M13) Starvation code never name either
    // Breeding or PopulationCount directly; this helper is where the
    // cross-feature coupling lives.
    public static void OnUnitRemoved(Simulation sim, Unit unit)
    {
        var world = sim.World;

        // M13 — catch up at the OLD rate (population still counts the
        // dying unit), then decrement. Robust to a missing player /
        // missing castle (defensive; lost-castle case post-capture).
        var castle = Sim.Core.Food.FoodConsumption.FindCastleFor(world, unit.OwnerId);
        if (castle is not null)
            Sim.Core.Food.FoodConsumption.CatchUp(castle, sim, sim.Now);

        // M12 — boats don't count toward population (vehicles, not
        // mouths); symmetric to GameWorld.AddUnit's BumpPopulationCount.
        if (unit.Role != UnitRole.Boat
            && world.Players.TryGetValue(unit.OwnerId, out var player))
            player.DecrementPopulation();

        // M13 Phase C — rate dropped; re-evaluate the famine check.
        if (castle is not null)
            Sim.Core.Food.FoodConsumption.OnRateOrFoodChanged(castle, sim);

        // M8 — breeding stop-on-removal.
        // Sparse iteration over Houses; houses are few.
        foreach (var s in world.Structures.Values)
        {
            if (s is not House house) continue;
            if (house.Occupation is not { } occ) continue;
            if (!occ.ContainsParent(unit.Id)) continue;

            // Free the surviving parent (if still alive).
            var otherId = occ.OtherParent(unit.Id);
            if (world.Units.TryGetValue(otherId, out var other))
                other.TrySetActivity(Activity.Idle);
            // Clear occupation → BirthEvent fences on next fire.
            house.Occupation = null;
            // One house at most per parent; we can stop.
            return;
        }
    }

    // M8 Phase D helper — find the House this unit is currently a
    // breeding parent in, or null. Used by BeginBreedingIntent to enforce
    // "no parent already breeding."
    public static House? GetActiveBreedingFor(GameWorld world, int unitId)
    {
        foreach (var s in world.Structures.Values)
        {
            if (s is not House house) continue;
            if (house.Occupation is { } occ && occ.ContainsParent(unitId))
                return house;
        }
        return null;
    }
}
