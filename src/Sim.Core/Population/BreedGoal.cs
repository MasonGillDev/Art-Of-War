using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Population;

// M30 — breeding as the precondition stress-test (docs/goal-shaped-intents.md
// §"Breeding: the precondition stress-test").
//
// Pre-M30 BeginBreedingIntent demanded five things be simultaneously true at
// the instant the player clicked: both parents standing on the house tile,
// both Idle, both fertile, and the food already delivered. Four of those are
// mechanical steps, so the intent was really an exam on the player's ability
// to sequence and time their own errands.
//
// Goal-shaped, the only question the player answers is "these two, that
// house". The pair walks from wherever they are, the first to arrive WAITS,
// and CONCEPTION FIRES THE MOMENT FOOD CLEARS THE THRESHOLD.
//
// Two rejected alternatives, recorded because both are tempting:
//   * fail-on-arrival — reborn appointment: the player would have to predict
//     future food levels before firing.
//   * conceive-on-credit — breaks M8's charge-at-start economics and invents
//     a debt state for something that already has one (famine).
//
// THE WAIT IS THE PRICE, and it is visible: a waiting pair sits in
// Activity.Waiting at the house, not working, not hauling. The player chose
// that trade.
public static class BreedGoal
{
    // Called when a parent finishes walking to the house. Parks them and tries
    // to conceive — the other half may already be standing here.
    public static void OnArrival(Simulation sim, Unit unit, House house)
    {
        if (unit.Goal is not { } goal) return;

        if (house.Occupation is not null)
        {
            // Someone else got this house between firing and arriving.
            GoalRules.Dissolve(sim, unit, "the house is occupied");
            return;
        }
        if (house.PendingBreed is not { } pending || !pending.ContainsParent(unit.Id))
        {
            GoalRules.Dissolve(sim, unit, "no longer expected here");
            return;
        }
        if (!Population.CanBreed(unit, sim.Now, sim.World.PopulationConfig))
        {
            // Aged out during the walk. Impossible-forever, so it dissolves
            // rather than stalling — and the partner is released with it.
            GoalRules.Dissolve(sim, unit, "no longer fertile");
            return;
        }

        // Park. Waiting is a NON-Idle activity, so from here no other intent
        // can quietly retask this body out from under the goal.
        unit.TrySetActivity(Activity.Waiting, goal.TargetTile);

        TryConceive(sim, house);
    }

    // The single conception site. Called from three places, all of them
    // state-CHANGE events rather than polls: a parent arriving, food landing
    // in the house, and the expiry sweep's last look.
    public static void TryConceive(Simulation sim, House house)
    {
        if (house.Occupation is not null) return;
        if (house.PendingBreed is not { } pending) return;

        var world = sim.World;
        if (!world.Units.TryGetValue(pending.ParentAId, out var a)) return;
        if (!world.Units.TryGetValue(pending.ParentBId, out var b)) return;

        // Both bodies must actually be here and waiting on this house.
        if (a.Activity != Activity.Waiting || b.Activity != Activity.Waiting) return;
        if (a.Position != house.At || b.Position != house.At) return;

        var cfg = world.PopulationConfig;
        // THE FERTILITY GATE EVALUATES HERE — at conception, not at
        // intent-firing. M8 said "checked once at breeding start"; M30
        // redefines start as conception, which is the only reading that
        // survives a wait of arbitrary length.
        if (!Population.CanBreed(a, sim.Now, cfg) || !Population.CanBreed(b, sim.Now, cfg)) return;

        // THE PRECONDITION. Not a rejection — the pair simply keeps waiting,
        // and the next delivery into this house tries again.
        if (house.AmountOf(Resource.Food) < cfg.BirthFoodCost) return;

        // Commit. From here it is the pre-M30 breeding cycle, unchanged.
        house.Withdraw(Resource.Food, cfg.BirthFoodCost);
        house.PendingBreed = null;
        a.Goal = null;
        b.Goal = null;
        // Waiting → Working is not a legal direct hop (no non-Idle to non-Idle
        // transitions); via Idle, exactly as an unassign-then-assign would go.
        a.TrySetActivity(Activity.Idle);
        b.TrySetActivity(Activity.Idle);
        a.TrySetActivity(Activity.Working, house.At);
        b.TrySetActivity(Activity.Working, house.At);

        var birthTick = sim.Now + cfg.GestationTicks;
        var seq = sim.Schedule(birthTick, new BirthEvent(house.At));
        house.Occupation = new BreedingOccupation
        {
            ParentAId = a.Id,
            ParentBId = b.Id,
            BirthTick = birthTick,
            BirthSeq = seq,
        };
    }

    // Food arrived somewhere. If that somewhere is a house with a pair
    // waiting in it, this is the moment they have been waiting for.
    // Event-driven, riding the delivery path that already exists — there is
    // no breeding poll anywhere in the sim.
    public static void OnHouseSupplied(Simulation sim, House house) => TryConceive(sim, house);

    // The tick at which this pair can no longer conceive: the EARLIER of the
    // two parents' fertility deadlines. A goal that can never complete must
    // never stall forever, and food that never arrives would otherwise leave
    // the pair standing there for the rest of their lives.
    public static long? FertilityDeadline(GameWorld world, Unit a, Unit b)
    {
        var cfg = world.PopulationConfig;
        var da = a.BornTick + (long)(cfg.MaxFertileAge + 1) * cfg.TicksPerYear;
        var db = b.BornTick + (long)(cfg.MaxFertileAge + 1) * cfg.TicksPerYear;
        return Math.Min(da, db);
    }
}
