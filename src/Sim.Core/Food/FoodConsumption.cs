using Sim.Core.World;

namespace Sim.Core.Food;

// M13 — castle food consumption. Reworked 2026-06-11 to the famine-DEBT
// model (docs/food-consumption.md, Update 2026-06-11): the consumption
// clock never stops. Demand the larder can't cover accrues in
// Castle.FoodDebt; the effective food level (Holdings − FoodDebt) goes
// negative; deposits pay the debt before restocking; famine — and the
// death cadence — ends only at debt zero.
//
// MUTATION POINT (single): Castle.Holdings[Food] is reduced ONLY by
// FoodConsumption.CatchUp; FoodDebt grows only here and shrinks only in
// CargoTransfer.DepositInto. Castle.LastFoodConsumedTick, FamineStartTick,
// NextFamineCheckTick/Seq are mutated only here, in CargoTransfer's
// debt-payment branch, and in the matching FamineCheckEvent.Apply (which
// clears its own anchor before delegating back).
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
// constant-rate segment. The debt model removed the old famine special
// case (the anchor used to freeze at the failure boundary); now the
// anchor always advances and the shortfall is owed, which makes the
// catch-up math one uniform branch.
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
    // Period-quantised effective food level — pure read. Computes what
    // CatchUp would leave (Holdings − FoodDebt − pending consumption)
    // without mutating any state. NEGATIVE during famine: the magnitude
    // is exactly the deposit needed to stop the deaths. Used by views.
    public static int CurrentLevel(Castle castle, Simulation sim, long now)
    {
        var consumed = ConsumptionUpTo(castle, sim, now);
        var available = castle.AmountOf(Resource.Food);
        return available - castle.FoodDebt - consumed;
    }

    // Period-quantised catch-up. Subtracts consumed food from Holdings;
    // any demand the larder can't cover accrues in FoodDebt instead of
    // being forgiven. LastFoodConsumedTick always advances by completed
    // periods (carry-remainder) — the clock never freezes for famine.
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

        // No mouths to feed. Anchor advances; no consumption; debt frozen.
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

        // Shortfall: the larder covers part (or none) of the demand and
        // the rest is owed. During famine the hole keeps deepening at
        // the full population rate — that's the bleeding the player has
        // to stop by paying the debt back to zero (CargoTransfer's food
        // path). Deaths shrink the rate, so the debt is self-limiting.
        var anchorBefore = castle.LastFoodConsumedTick;
        var shortfall = wouldConsume - available;
        castle.Withdraw(Resource.Food, available);  // takes everything
        castle.LastFoodConsumedTick += periods * period;
        castle.FoodDebt = (int)Math.Min(int.MaxValue, castle.FoodDebt + shortfall);

        // Famine-onset transition (debt was zero, now isn't): mark the
        // boundary of the first meal that couldn't feed everyone and
        // schedule the first starvation death after the grace window.
        // Future deaths reschedule themselves from inside
        // StarvationDeathEvent.Apply; the cadence stops only when the
        // debt is repaid in full (which is also what cleared any prior
        // death anchor — so an unconditional schedule here is safe, and
        // a stale anchor would be fenced by the overwrite anyway).
        if (!castle.FamineStartTick.HasValue)
        {
            var fullMeals = available / rate;
            castle.FamineStartTick = anchorBefore + (fullMeals + 1) * period;
            ScheduleNextStarvationDeath(castle, sim, fireAt: castle.FamineStartTick.Value
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
    // castle's current anchor and `now`. Returns the demand CatchUp
    // would charge (Holdings first, then FoodDebt) over the completed
    // periods. The clock never stops, famine or not.
    private static int ConsumptionUpTo(Castle castle, Simulation sim, long now)
    {
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
