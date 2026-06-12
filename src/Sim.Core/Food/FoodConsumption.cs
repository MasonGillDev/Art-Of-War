using Sim.Core.World;

namespace Sim.Core.Food;

// M13 — food consumption; M19 Phase 2 — generalized from "the Castle"
// to ANY FOOD HOME (IFoodHome: Castle + House). Citizens eat from the
// home their Unit.Home names (null = the castle); the famine-DEBT model
// (docs/food-consumption.md, Update 2026-06-11) runs per home: the
// consumption clock never stops, demand the larder can't cover accrues
// in FoodDebt, the effective level (Holdings − FoodDebt) goes negative,
// deposits pay the debt before restocking, and famine — and the death
// cadence among THAT home's residents — ends only at debt zero.
//
// MUTATION POINT (single, per home): Holdings[Food] is reduced ONLY by
// FoodConsumption.CatchUp; FoodDebt grows only here and shrinks only in
// CargoTransfer.DepositInto. LastFoodConsumedTick, FamineStartTick,
// NextFamineCheckTick/Seq are mutated only here, in CargoTransfer's
// debt-payment branch, and in the matching FamineCheckEvent.Apply (which
// clears its own anchor before delegating back).
//
// PURE-READ WALL: CurrentLevel reads state and never writes — see the
// 100×-no-mutation pin in FoodConsumptionTests.
//
// LAZY-CATCH-UP PATTERN (architecture §2.5, M9): the per-period rate
// (ResidentsOf(home) × FoodPerCitizenPerPeriod) is invariant between
// rate-changing events. Catch-up is called at every such event
// (population add/remove, HOME CHANGE — the M19 addition, food deposit,
// FamineCheckEvent fire) so the rate over the elapsed interval is
// constant by construction. Time advances by completed periods only —
// the remainder carries within the constant-rate segment.
//
// SCHEDULING (split from math): OnRateOrFoodChanged is the only site
// that schedules FamineCheckEvent. CatchUp does NOT schedule — it only
// mutates state. Every caller pattern is "CatchUp → mutate → re-evaluate":
//   - Population.OnUnitAdded: CatchUp(castle), world.AddUnit, OnRateOrFoodChanged.
//   - Population.OnUnitRemoved: CatchUp(the unit's home), decrement +
//     free the bed, OnRateOrFoodChanged.
//   - Population.SetHome: CatchUp BOTH sinks, move the mouth, re-evaluate both.
//   - CargoTransfer.DepositInto: CatchUp, Deposit (debt first), re-evaluate.
//   - FamineCheckEvent.Apply: clear anchor, CatchUp, OnRateOrFoodChanged.
public static class FoodConsumption
{
    // Period-quantised effective food level — pure read. Computes what
    // CatchUp would leave (Holdings − FoodDebt − pending consumption)
    // without mutating any state. NEGATIVE during famine: the magnitude
    // is exactly the deposit needed to stop the deaths. Used by views.
    public static int CurrentLevel(IFoodHome home, Simulation sim, long now) =>
        CurrentLevel(home, sim.World, now);

    public static int CurrentLevel(IFoodHome home, GameWorld world, long now)
    {
        var consumed = ConsumptionUpTo(home, world, now);
        var available = home.AmountOf(Resource.Food);
        return available - home.FoodDebt - consumed;
    }

    // Period-quantised catch-up. Subtracts consumed food from Holdings;
    // any demand the larder can't cover accrues in FoodDebt instead of
    // being forgiven. LastFoodConsumedTick always advances by completed
    // periods (carry-remainder) — the clock never freezes for famine.
    // Idempotent within a tick (second call sees zero elapsed periods).
    public static void CatchUp(IFoodHome home, Simulation sim, long now)
    {
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var elapsed = now - home.LastFoodConsumedTick;
        if (elapsed <= 0) return;

        var periods = elapsed / period;          // integer floor
        if (periods <= 0) return;                // mid-period; nothing to do

        var rate = ResidentsOf(sim.World, home)
            * FoodConsumptionConstants.FoodPerCitizenPerPeriod;

        // No mouths to feed. Anchor advances; no consumption; debt frozen.
        if (rate <= 0)
        {
            home.LastFoodConsumedTick += periods * period;
            return;
        }

        var available = home.AmountOf(Resource.Food);
        var wouldConsume = periods * (long)rate;

        if (wouldConsume <= available)
        {
            home.Withdraw(Resource.Food, (int)wouldConsume);
            home.LastFoodConsumedTick += periods * period;
            return;
        }

        // Shortfall: the larder covers part (or none) of the demand and
        // the rest is owed. During famine the hole keeps deepening at
        // the full resident rate — that's the bleeding the player has
        // to stop by paying the debt back to zero (CargoTransfer's food
        // path). Deaths shrink the rate, so the debt is self-limiting.
        var anchorBefore = home.LastFoodConsumedTick;
        var shortfall = wouldConsume - available;
        home.Withdraw(Resource.Food, available);  // takes everything
        home.LastFoodConsumedTick += periods * period;
        home.FoodDebt = (int)Math.Min(int.MaxValue, home.FoodDebt + shortfall);

        // Famine-onset transition (debt was zero, now isn't): mark the
        // boundary of the first meal that couldn't feed everyone and
        // schedule the first starvation death after the grace window.
        // Future deaths reschedule themselves from inside
        // StarvationDeathEvent.Apply; the cadence stops only when the
        // debt is repaid in full (which is also what cleared any prior
        // death anchor — so an unconditional schedule here is safe, and
        // a stale anchor would be fenced by the overwrite anyway).
        if (!home.FamineStartTick.HasValue)
        {
            var fullMeals = available / rate;
            home.FamineStartTick = anchorBefore + (fullMeals + 1) * period;
            ScheduleNextStarvationDeath(home, sim, fireAt: home.FamineStartTick.Value
                + FoodConsumptionConstants.StarvationStartDelay);
        }
    }

    // M13 Phase D — schedule the next StarvationDeathEvent for this
    // home. Single-anchor pattern: any in-flight event is fenced when
    // we overwrite (NextStarvationDeathTick, NextStarvationDeathSeq).
    internal static void ScheduleNextStarvationDeath(
        IFoodHome home, Simulation sim, long fireAt)
    {
        // The onset is computed RETROACTIVELY by the lazy math — a home
        // that starved unobserved past its whole grace window schedules
        // its first death NOW, not in the past. The grace genuinely
        // elapsed; the household just wasn't being watched.
        if (fireAt < sim.Now) fireAt = sim.Now;
        home.NextStarvationDeathTick = fireAt;
        home.NextStarvationDeathSeq = sim.Schedule(
            fireAt, new StarvationDeathEvent(home.At));
    }

    internal static void ClearStarvationDeathAnchor(IFoodHome home)
    {
        home.NextStarvationDeathTick = null;
        home.NextStarvationDeathSeq = null;
    }

    // Re-evaluate the queued FamineCheckEvent against the home's
    // current state. Called by every site that mutates the rate
    // (population add/remove, home change) or the food level (deposit)
    // AFTER its mutation has landed. Always overwrites the anchor; any
    // in-flight FamineCheckEvent fences via mismatch when it fires.
    public static void OnRateOrFoodChanged(IFoodHome home, Simulation sim)
    {
        // Clear the old anchor unconditionally. If we schedule below,
        // we'll write a fresh anchor.
        home.NextFamineCheckTick = null;
        home.NextFamineCheckSeq = null;

        // No mouths → no consumption → no famine.
        var rate = ResidentsOf(sim.World, home)
            * FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        if (rate <= 0) return;

        // Famine already active. The Phase D StarvationDeathEvent owns
        // the active-famine clock — but the cadence can be MISSING
        // (every resident died and the cadence stopped; then a new
        // resident moved in, or food was stolen back out): re-arm it
        // WITHOUT granting fresh grace — the next death comes at the
        // original schedule or one interval from now, whichever is
        // later. (The 2026-06-09 lesson: partial recoveries never
        // reset the cadence.)
        if (home.FamineStartTick.HasValue)
        {
            if (home.FoodDebt > 0 && home.NextStarvationDeathTick is null)
                ScheduleNextStarvationDeath(home, sim, fireAt: Math.Max(
                    home.FamineStartTick.Value + FoodConsumptionConstants.StarvationStartDelay,
                    sim.Now + FoodConsumptionConstants.StarvationDeathInterval));
            return;
        }

        // Schedule the FamineCheckEvent at the predicted next failure.
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var food = home.AmountOf(Resource.Food);
        var fullMeals = food / rate;
        var dryOut = home.LastFoodConsumedTick + (fullMeals + 1) * period;

        // Defensive: dryOut can be in the past if the caller didn't
        // CatchUp first (a programming error). Push forward so the
        // schedule is still well-formed.
        if (dryOut <= sim.Now) dryOut = sim.Now + period;

        home.NextFamineCheckTick = dryOut;
        home.NextFamineCheckSeq = sim.Schedule(dryOut, new FamineCheckEvent(home.At));
    }

    // ---- M19 resident accounting ---------------------------------------------

    // How many mouths eat from this home. Houses store the count
    // (single-mutation via Population.SetHome); the castle's is DERIVED:
    // everyone not housed elsewhere eats at the keep. O(structures) —
    // called only inside rate-changing events; structures are few.
    public static int ResidentsOf(GameWorld world, IFoodHome home) => home switch
    {
        House h => h.ResidentCount,
        Castle c => CastleResidents(world, c.OwnerId),
        _ => 0,
    };

    private static int CastleResidents(GameWorld world, int ownerId)
    {
        var pop = PopulationOf(world, ownerId);
        var housed = 0;
        foreach (var s in world.Structures.Values)
            if (s is House h && h.OwnerId == ownerId) housed += h.ResidentCount;
        return Math.Max(0, pop - housed);
    }

    // The sink a unit's meals come from: their Home house when it still
    // stands and is still theirs, else the owner's castle. Null only
    // for the castle-less (a lost faction, bandits).
    public static IFoodHome? HomeOf(GameWorld world, Unit unit)
    {
        if (unit.Home is { } t
            && world.Structures.TryGetValue(t, out var s)
            && s is House h && h.OwnerId == unit.OwnerId)
            return h;
        return FindCastleFor(world, unit.OwnerId);
    }

    // Pure-read helper — what consumption-amount applies between the
    // home's current anchor and `now`. Returns the demand CatchUp
    // would charge (Holdings first, then FoodDebt) over the completed
    // periods. The clock never stops, famine or not.
    private static int ConsumptionUpTo(IFoodHome home, GameWorld world, long now)
    {
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var elapsed = now - home.LastFoodConsumedTick;
        if (elapsed <= 0) return 0;
        var periods = elapsed / period;
        if (periods <= 0) return 0;
        var rate = ResidentsOf(world, home)
            * FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        if (rate <= 0) return 0;
        return (int)(periods * (long)rate);
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
