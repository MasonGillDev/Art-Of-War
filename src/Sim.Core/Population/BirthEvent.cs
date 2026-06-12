using Sim.Core.World;

namespace Sim.Core.Population;

// M8 Phase E — fires at house.Occupation.BirthTick to spawn a role-less
// child + free the parents.
//
// Fencing: the event carries the HouseTile. On Apply, looks up the house,
// checks that Occupation is non-null and (BirthTick, BirthSeq) matches
// (At, Seq). Mismatch (e.g. a parent died and OnUnitRemoved cleared
// Occupation) → no-op.
public sealed class BirthEvent : ScheduledEvent
{
    public TileCoord HouseTile { get; }

    public BirthEvent(TileCoord houseTile) { HouseTile = houseTile; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(HouseTile, out var s) || s is not House house)
        {
            Outcome = IntentOutcome.Reject($"no House at {HouseTile}");
            return;
        }
        if (house.Occupation is not { } occ)
        {
            // Stop-on-removal already cleared this; the queued event is stale.
            Outcome = IntentOutcome.Reject($"House at {HouseTile} not occupied (stop-on-removal)");
            return;
        }
        if (occ.BirthTick != At || occ.BirthSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale birth event at {HouseTile} " +
                $"(occ=({occ.BirthTick},{occ.BirthSeq}), event=({At},{Seq}))");
            return;
        }

        // Spawn the role-less child. Population.OnUnitAdded (M13) catches
        // up the owner's castle BEFORE the AddUnit increments
        // Player.PopulationCount — the rate-changing-event discipline.
        var childId = world.NextUnitId;
        world.NextUnitId++;
        var child = Population.OnUnitAdded(sim, new Unit(childId, HouseTile)
        {
            Role = UnitRole.None,
            OwnerId = house.OwnerId,
            BornTick = sim.Now,
        });
        Population.ScheduleLifespan(sim, child);

        // M19 — auto-assignment trigger 1 (birth): home at the birth
        // house if a bed is free, else the nearest house with one, else
        // the castle (Home stays null). Capacity never blocks the birth
        // itself — a housing shortage makes feeding expensive, it never
        // freezes the population (docs/m19-per-house-food-spec.md).
        var bed = Population.NearestHouseWithBed(world, house.OwnerId, HouseTile,
            Sim.Core.Food.FoodConsumptionConstants.HomeAssignRadius);
        if (bed is not null)
            Population.SetHome(world, child, bed.At);

        // Free both parents (Working -> Idle). The parents might no longer
        // exist (extreme edge case: combat killed both simultaneously and
        // OnUnitRemoved missed somehow — defensive TryGetValue).
        if (world.Units.TryGetValue(occ.ParentAId, out var pa))
            pa.TrySetActivity(Activity.Idle);
        if (world.Units.TryGetValue(occ.ParentBId, out var pb))
            pb.TrySetActivity(Activity.Idle);

        house.Occupation = null;
    }

    public override string Describe() => $"Birth(@ {HouseTile.X},{HouseTile.Y})";
}
