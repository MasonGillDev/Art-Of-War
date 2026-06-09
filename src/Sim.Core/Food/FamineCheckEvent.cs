using Sim.Core.World;

namespace Sim.Core.Food;

// M13 Phase C — predicted-next-dry-out check. Scheduled by
// FoodConsumption.OnRateOrFoodChanged at the meal boundary where the
// castle's food is forecast to run out under the current rate. Fires
// there; runs CatchUp (which sets Castle.FamineStartTick); re-evaluates
// the schedule (which is a no-op when famine is now active).
//
// Fencing: any earlier population-change or food-deposit will have
// re-run OnRateOrFoodChanged, which overwrites NextFamineCheckTick /
// NextFamineCheckSeq with a fresh anchor. On fire, the old anchor
// pair no longer matches (At, Seq) and the event no-ops cleanly.
public sealed class FamineCheckEvent : ScheduledEvent
{
    public TileCoord CastleAt { get; }

    public FamineCheckEvent(TileCoord castleAt) { CastleAt = castleAt; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(CastleAt, out var s) || s is not Castle castle)
        {
            Outcome = IntentOutcome.Reject($"no Castle at {CastleAt}");
            return;
        }
        if (castle.NextFamineCheckTick != At || castle.NextFamineCheckSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale famine check at {CastleAt} " +
                $"(stored=({castle.NextFamineCheckTick},{castle.NextFamineCheckSeq}), " +
                $"event=({At},{Seq}))");
            return;
        }

        // Anchor matches — we are the live event. Clear the anchor BEFORE
        // running CatchUp + OnRateOrFoodChanged so the reschedule sees a
        // clean slate.
        castle.NextFamineCheckTick = null;
        castle.NextFamineCheckSeq = null;

        FoodConsumption.CatchUp(castle, sim, sim.Now);
        FoodConsumption.OnRateOrFoodChanged(castle, sim);
    }

    public override string Describe() => $"FamineCheck(@ {CastleAt.X},{CastleAt.Y})";
}
