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
        return (int)(ageTicks / config.TicksPerYear);
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

    // M8 Phase E — called from CombatRules.OnUnitDeath and
    // DeathByAgeEvent.Apply after the unit is removed from world.Units.
    // If the removed unit was a breeding parent, clear the house's
    // Occupation (which fences the queued BirthEvent via anchor mismatch),
    // and free the surviving parent. Food is NOT refunded — the gestation
    // period consumed it, raids should bite.
    //
    // Combat code and Aging code never name Breeding; this helper is the
    // one place the cross-feature coupling lives.
    public static void OnUnitRemoved(Simulation sim, Unit unit)
    {
        var world = sim.World;
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
