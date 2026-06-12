using Sim.Core.World;

namespace Sim.Core.Food;

// M13 Phase D, generalized per-home in M19 — kills one RESIDENT of this
// food home per StarvationDeathInterval while its famine is active,
// oldest first (ascending BornTick, ties by ascending Id). A frontier
// house starves its own household even when the castle larder is full
// (the harsh-doctrine lock); the castle starves the mobile class.
// First fire is scheduled by FoodConsumption.CatchUp at FamineStartTick
// + StarvationStartDelay; each subsequent fire reschedules itself.
//
// Fencing:
//   - (At, Seq) must match the home's (NextStarvationDeathTick,
//     NextStarvationDeathSeq). If a debt-clearing deposit cleared the
//     anchor (famine ended) or a population-shift re-issued one, the
//     old event no-ops.
//   - FoodDebt must still be positive (famine active). A full
//     repayment cleared this plus the anchor; a defensive
//     belt-and-suspenders check guards against unusual reschedule paths.
//
// Removal piggybacks on CombatRules.OnUnitDeath (the M7 single death
// pipeline), which drops cargo, group-cleanup, clears in-flight, and
// calls Population.OnUnitRemoved — which decrements PopulationCount,
// catches up the victim's home at the OLD rate (during famine that
// GROWS FoodDebt: the victim ate, on credit, right up to their death),
// frees their bed, and runs breeding stop-on-removal.
public sealed class StarvationDeathEvent : ScheduledEvent
{
    public TileCoord HomeAt { get; }

    public StarvationDeathEvent(TileCoord homeAt) { HomeAt = homeAt; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(HomeAt, out var s) || s is not IFoodHome home)
        {
            Outcome = IntentOutcome.Reject($"no food home at {HomeAt}");
            return;
        }
        if (home.NextStarvationDeathTick != At || home.NextStarvationDeathSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale starvation death at {HomeAt} " +
                $"(stored=({home.NextStarvationDeathTick},{home.NextStarvationDeathSeq}), " +
                $"event=({At},{Seq}))");
            return;
        }
        if (home.FamineStartTick is null || home.FoodDebt <= 0)
        {
            Outcome = IntentOutcome.Reject("famine no longer active");
            FoodConsumption.ClearStarvationDeathAnchor(home);
            return;
        }

        // Anchor matches; we're the live event. Clear it before mutating
        // so OnUnitRemoved → OnRateOrFoodChanged (called via the death
        // pipeline) sees a clean slate.
        home.NextStarvationDeathTick = null;
        home.NextStarvationDeathSeq = null;

        var victim = FindOldestResident(world, home);
        if (victim is null)
        {
            // No residents left to kill. Deaths stop (nothing reschedules)
            // but the famine does NOT end: the debt is still owed, and
            // FamineStartTick stays set so the FoodDebt > 0 ⇔ famine
            // invariant holds. With the rate at zero the debt is frozen;
            // only a repaying deposit closes it out.
            return;
        }

        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, victim);

        // If that was the home's last resident, stop the cadence — same
        // reasoning as the no-victim branch: debt outlives the dead.
        // (Counts were decremented by OnUnitRemoved, just run via
        // OnUnitDeath.)
        if (FoodConsumption.ResidentsOf(world, home) == 0)
        {
            return;
        }

        // More mouths still hungry — schedule the next death.
        FoodConsumption.ScheduleNextStarvationDeath(
            home, sim, fireAt: sim.Now + FoodConsumptionConstants.StarvationDeathInterval);
    }

    // O(units) scan for the home's oldest resident — residency resolved
    // through FoodConsumption.HomeOf so the castle inherits everyone
    // whose house is gone. Acceptable at current scale; if populations
    // grow an order of magnitude, replace with a per-home index sorted
    // by (BornTick, Id) — the M2 "iterate to find" scaling concern.
    private static Unit? FindOldestResident(GameWorld world, IFoodHome home)
    {
        Unit? oldest = null;
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId != home.OwnerId) continue;
            // M12 — boats are vehicles, not citizens; never starvation
            // victims and never the oldest-resident target.
            if (u.Role == UnitRole.Boat) continue;
            if (FoodConsumption.HomeOf(world, u) is not { } uh || uh.At != home.At) continue;
            if (oldest is null
                || u.BornTick < oldest.BornTick
                || (u.BornTick == oldest.BornTick && u.Id < oldest.Id))
            {
                oldest = u;
            }
        }
        return oldest;
    }

    public override string Describe() => $"StarvationDeath(@ {HomeAt.X},{HomeAt.Y})";
}
