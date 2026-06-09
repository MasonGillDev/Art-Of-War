using Sim.Core.World;

namespace Sim.Core.Food;

// M13 — castle food consumption.
//
// MUTATION POINT (single): Castle.Holdings[Food] is reduced ONLY by
// FoodConsumption.CatchUp. Castle.LastFoodConsumedTick, FamineStartTick,
// NextFamineCheckTick/Seq are mutated only here and in the matching
// FamineCheckEvent.Apply (which clears its own anchor before delegating
// back). Deposits go through StorageStructure.Deposit (the normal path);
// withdrawals for any other reason are out-of-scope.
//
// PURE-READ WALL: CurrentLevel reads state and never writes — see the
// 100×-no-mutation pin in FoodConsumptionTests.
//
// LAZY-CATCH-UP PATTERN (architecture §2.5, M9): the per-period rate
// (PopulationCount × FoodPerCitizenPerPeriod) is invariant between
// rate-changing events. Catch-up is called at every such event
// (population add/remove, food deposit, FamineCheckEvent fire) so the
// rate over the elapsed interval is constant by construction. Time
// advances by completed periods only — the remainder carries within the
// constant-rate segment, except across a famine boundary where we
// anchor LastFoodConsumedTick to the failure tick (the architecture
// §2.5 spatial-extension discipline).
//
// SCHEDULING (split from math): OnRateOrFoodChanged is the only site
// that schedules FamineCheckEvent. CatchUp does NOT schedule — it only
// mutates state. Every caller pattern is "CatchUp → mutate → re-evaluate":
//   - Population.OnUnitAdded: CatchUp(now), world.AddUnit, OnRateOrFoodChanged.
//   - Population.OnUnitRemoved: CatchUp(now), decrement, OnRateOrFoodChanged.
//   - HaulDepositEvent.Apply: CatchUp(now), Deposit, clear famine if
//     applicable, OnRateOrFoodChanged.
//   - FamineCheckEvent.Apply: clear anchor, CatchUp(now), OnRateOrFoodChanged.
public static class FoodConsumption
{
    // Period-quantised current food level — pure read. Computes what
    // CatchUp would leave without mutating any state. Used by views.
    public static int CurrentLevel(Castle castle, Simulation sim, long now)
    {
        var consumed = ConsumptionUpTo(castle, sim, now);
        var available = castle.AmountOf(Resource.Food);
        return Math.Max(0, available - consumed);
    }

    // Period-quantised catch-up. Subtracts consumed food from Holdings.
    // Advances LastFoodConsumedTick by completed periods (carry-remainder)
    // when food is sufficient; advances to the exact failure-boundary tick
    // when food runs out and sets FamineStartTick to that same tick.
    // Idempotent within a tick (second call sees zero elapsed periods).
    public static void CatchUp(Castle castle, Simulation sim, long now)
    {
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var elapsed = now - castle.LastFoodConsumedTick;
        if (elapsed <= 0) return;

        var periods = elapsed / period;          // integer floor
        if (periods <= 0) return;                // mid-period; nothing to do

        var pop = PopulationOf(sim.World, castle.OwnerId);
        var rate = pop * FoodConsumptionConstants.FoodPerCitizenPerPeriod;

        // Already in famine: just advance the anchor. No consumption
        // (food is 0), FamineStartTick unchanged.
        if (castle.FamineStartTick.HasValue)
        {
            castle.LastFoodConsumedTick += periods * period;
            return;
        }

        // No mouths to feed. Anchor advances; no consumption; no famine.
        if (rate <= 0)
        {
            castle.LastFoodConsumedTick += periods * period;
            return;
        }

        var available = castle.AmountOf(Resource.Food);
        var wouldConsume = periods * rate;

        if (wouldConsume <= available)
        {
            castle.Withdraw(Resource.Food, (int)wouldConsume);
            castle.LastFoodConsumedTick += periods * period;
            return;
        }

        // Famine triggered within this catch-up window. The last
        // successful full meal sits at boundary `fullMeals × period`
        // from the anchor; the meal at `(fullMeals + 1) × period` is
        // where Food first failed to feed everyone. Consume what
        // remains in the larder (the partial-meal scrap is taken too)
        // and mark famine at the failure boundary.
        var fullMeals = available / rate;
        var failureBoundary = castle.LastFoodConsumedTick + (fullMeals + 1) * period;

        castle.Withdraw(Resource.Food, available);  // takes everything
        castle.FamineStartTick = failureBoundary;
        castle.LastFoodConsumedTick = failureBoundary;

        // M13 Phase D — schedule the first starvation death. Only on
        // the famine-trigger transition (not while already in famine);
        // this branch IS that transition. Future deaths reschedule
        // themselves from inside StarvationDeathEvent.Apply.
        //
        // Carry-over guard: if an anchor from a PREVIOUS famine is still
        // in flight (the player deposited food, cleared the famine, then
        // ran out again before the original death fired), leave that
        // original schedule intact rather than granting a fresh
        // StarvationStartDelay grace window. Closes the trickle-deposit
        // exploit. See docs/food-consumption.md (Update 2026-06-09).
        if (!castle.NextStarvationDeathTick.HasValue)
        {
            ScheduleNextStarvationDeath(castle, sim, fireAt: failureBoundary
                + FoodConsumptionConstants.StarvationStartDelay);
        }
    }

    // M13 Phase D — schedule the next StarvationDeathEvent for this
    // castle. Single-anchor pattern: any in-flight event is fenced when
    // we overwrite (NextStarvationDeathTick, NextStarvationDeathSeq).
    internal static void ScheduleNextStarvationDeath(
        Castle castle, Simulation sim, long fireAt)
    {
        castle.NextStarvationDeathTick = fireAt;
        castle.NextStarvationDeathSeq = sim.Schedule(
            fireAt, new StarvationDeathEvent(castle.At));
    }

    internal static void ClearStarvationDeathAnchor(Castle castle)
    {
        castle.NextStarvationDeathTick = null;
        castle.NextStarvationDeathSeq = null;
    }

    // Re-evaluate the queued FamineCheckEvent against the castle's
    // current state. Called by every site that mutates the rate
    // (population add/remove) or the food level (deposit) AFTER its
    // mutation has landed. Always overwrites the anchor; any in-flight
    // FamineCheckEvent fences via mismatch when it eventually fires.
    public static void OnRateOrFoodChanged(Castle castle, Simulation sim)
    {
        // Clear the old anchor unconditionally. If we schedule below,
        // we'll write a fresh anchor.
        castle.NextFamineCheckTick = null;
        castle.NextFamineCheckSeq = null;

        // No mouths → no consumption → no famine.
        var pop = PopulationOf(sim.World, castle.OwnerId);
        var rate = pop * FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        if (rate <= 0) return;

        // Famine already active. The Phase D StarvationDeathEvent
        // owns the active-famine clock; we have nothing to schedule here.
        if (castle.FamineStartTick.HasValue) return;

        // Schedule the FamineCheckEvent at the predicted next failure.
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var food = castle.AmountOf(Resource.Food);
        var fullMeals = food / rate;
        var dryOut = castle.LastFoodConsumedTick + (fullMeals + 1) * period;

        // Defensive: dryOut can be in the past if the caller didn't
        // CatchUp first (a programming error). Push forward so the
        // schedule is still well-formed.
        if (dryOut <= sim.Now) dryOut = sim.Now + period;

        castle.NextFamineCheckTick = dryOut;
        castle.NextFamineCheckSeq = sim.Schedule(dryOut, new FamineCheckEvent(castle.At));
    }

    // Pure-read helper — what consumption-amount applies between the
    // castle's current anchor and `now`. Returns the value CatchUp
    // would subtract from Holdings. Famine-aware: a castle already in
    // famine has Food = 0 and no further consumption.
    private static int ConsumptionUpTo(Castle castle, Simulation sim, long now)
    {
        if (castle.FamineStartTick.HasValue) return 0;
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var elapsed = now - castle.LastFoodConsumedTick;
        if (elapsed <= 0) return 0;
        var periods = elapsed / period;
        if (periods <= 0) return 0;
        var pop = PopulationOf(sim.World, castle.OwnerId);
        var rate = pop * FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        if (rate <= 0) return 0;
        return (int)(periods * rate);
    }

    private static int PopulationOf(GameWorld world, int ownerId) =>
        world.Players.TryGetValue(ownerId, out var p) ? p.PopulationCount : 0;

    // Lookup the canonical castle for a player. Today each player has
    // exactly one Castle (planted at genesis); a future capture mechanic
    // may produce multi-castle owners — see docs/food-consumption.md
    // ("multi-castle imperfection" deferred). Iterates structures in
    // canonical (y, x) order so a multi-castle scenario picks deterministically.
    public static Castle? FindCastleFor(GameWorld world, int ownerId)
    {
        Castle? best = null;
        TileCoord bestAt = default;
        foreach (var s in world.Structures.Values)
        {
            if (s is not Castle c) continue;
            if (c.OwnerId != ownerId) continue;
            if (best is null || LessYThenX(c.At, bestAt))
            {
                best = c;
                bestAt = c.At;
            }
        }
        return best;
    }

    private static bool LessYThenX(TileCoord a, TileCoord b) =>
        a.Y < b.Y || (a.Y == b.Y && a.X < b.X);
}
