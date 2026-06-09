using Sim.Core.World;

namespace Sim.Core.Food;

// M13 Phase D — kills one citizen per StarvationDeathInterval while
// famine is active on this castle, oldest first (ascending BornTick,
// ties by ascending Id). First fire is scheduled by FoodConsumption.CatchUp
// at FamineStartTick + StarvationStartDelay; each subsequent fire
// reschedules itself.
//
// Fencing:
//   - (At, Seq) must match the castle's (NextStarvationDeathTick,
//     NextStarvationDeathSeq). If a deposit cleared the anchor (famine
//     ended) or a population-shift re-issued one, the old event no-ops.
//   - Castle.FamineStartTick must still be set. A deposit cleared this
//     plus the anchor; a defensive belt-and-suspenders check guards
//     against unusual reschedule paths.
//
// Removal piggybacks on CombatRules.OnUnitDeath (the M7 single death
// pipeline), which drops cargo, group-cleanup, clears in-flight, and
// calls Population.OnUnitRemoved — which decrements PopulationCount,
// catches up food at the OLD rate (a no-op during famine since Holdings
// are 0), and runs breeding stop-on-removal.
public sealed class StarvationDeathEvent : ScheduledEvent
{
    public TileCoord CastleAt { get; }

    public StarvationDeathEvent(TileCoord castleAt) { CastleAt = castleAt; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(CastleAt, out var s) || s is not Castle castle)
        {
            Outcome = IntentOutcome.Reject($"no Castle at {CastleAt}");
            return;
        }
        if (castle.NextStarvationDeathTick != At || castle.NextStarvationDeathSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale starvation death at {CastleAt} " +
                $"(stored=({castle.NextStarvationDeathTick},{castle.NextStarvationDeathSeq}), " +
                $"event=({At},{Seq}))");
            return;
        }
        if (castle.FamineStartTick is null)
        {
            Outcome = IntentOutcome.Reject("famine no longer active");
            FoodConsumption.ClearStarvationDeathAnchor(castle);
            return;
        }

        // Anchor matches; we're the live event. Clear it before mutating
        // so OnUnitRemoved → OnRateOrFoodChanged (called via the death
        // pipeline) sees a clean slate.
        castle.NextStarvationDeathTick = null;
        castle.NextStarvationDeathSeq = null;

        var victim = FindOldestCitizen(world, castle.OwnerId);
        if (victim is null)
        {
            // No citizens left to feed. Famine implicitly ends — the
            // rate is zero so consumption can't continue, and there's
            // nothing to be hungry. Clear FamineStartTick for cleanliness.
            castle.FamineStartTick = null;
            return;
        }

        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, victim);

        // If that was the last citizen, end the famine. (Population
        // counters are decremented by OnUnitRemoved, which we just
        // ran via OnUnitDeath.)
        if (world.Players.TryGetValue(castle.OwnerId, out var p)
            && p.PopulationCount == 0)
        {
            castle.FamineStartTick = null;
            return;
        }

        // More mouths still hungry — schedule the next death.
        FoodConsumption.ScheduleNextStarvationDeath(
            castle, sim, fireAt: sim.Now + FoodConsumptionConstants.StarvationDeathInterval);
    }

    // O(units) scan. Acceptable at current scale; if populations grow
    // an order of magnitude, replace with a per-player index of units
    // sorted by (BornTick, Id) — same shape as the M2 "iterate to find"
    // scaling concern flagged in architecture §4 rule 10.
    private static Unit? FindOldestCitizen(GameWorld world, int ownerId)
    {
        Unit? oldest = null;
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId != ownerId) continue;
            // M12 — boats are vehicles, not citizens; never starvation
            // victims and never the oldest-citizen target.
            if (u.Role == UnitRole.Boat) continue;
            if (oldest is null
                || u.BornTick < oldest.BornTick
                || (u.BornTick == oldest.BornTick && u.Id < oldest.Id))
            {
                oldest = u;
            }
        }
        return oldest;
    }

    public override string Describe() => $"StarvationDeath(@ {CastleAt.X},{CastleAt.Y})";
}
