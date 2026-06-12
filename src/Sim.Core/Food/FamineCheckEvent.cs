using Sim.Core.World;

namespace Sim.Core.Food;

// M13 Phase C, generalized per-home in M19 — predicted-next-dry-out
// check. Scheduled by FoodConsumption.OnRateOrFoodChanged at the meal
// boundary where the home's food is forecast to run out under the
// current resident rate. Fires there; runs CatchUp (which sets
// FamineStartTick); re-evaluates the schedule (a no-op when famine is
// now active).
//
// Fencing: any earlier rate change (population, home move) or food
// deposit will have re-run OnRateOrFoodChanged, which overwrites
// NextFamineCheckTick / NextFamineCheckSeq with a fresh anchor. On
// fire, the old anchor pair no longer matches (At, Seq) and the event
// no-ops cleanly.
public sealed class FamineCheckEvent : ScheduledEvent
{
    public TileCoord HomeAt { get; }

    public FamineCheckEvent(TileCoord homeAt) { HomeAt = homeAt; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(HomeAt, out var s) || s is not IFoodHome home)
        {
            Outcome = IntentOutcome.Reject($"no food home at {HomeAt}");
            return;
        }
        if (home.NextFamineCheckTick != At || home.NextFamineCheckSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale famine check at {HomeAt} " +
                $"(stored=({home.NextFamineCheckTick},{home.NextFamineCheckSeq}), " +
                $"event=({At},{Seq}))");
            return;
        }

        // Anchor matches — we are the live event. Clear the anchor BEFORE
        // running CatchUp + OnRateOrFoodChanged so the reschedule sees a
        // clean slate.
        home.NextFamineCheckTick = null;
        home.NextFamineCheckSeq = null;

        FoodConsumption.CatchUp(home, sim, sim.Now);
        FoodConsumption.OnRateOrFoodChanged(home, sim);
    }

    public override string Describe() => $"FamineCheck(@ {HomeAt.X},{HomeAt.Y})";
}
