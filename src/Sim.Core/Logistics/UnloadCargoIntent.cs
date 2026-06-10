namespace Sim.Core.Logistics;

// Instant intent — empties a unit's carried cargo. "Smart" target: if the unit stands
// on a structure it owns that can take the resource, deposit there (capacity-/need-
// limited); whatever doesn't fit — or the whole load, if there's no accepting structure
// — drops to a re-haulable ground pile on the tile. The unit is always left empty, so
// cargo is never silently destroyed.
//
// Pairs with HaulIntent's "must start empty" reject: a laden unit can't re-haul, so this
// is how the player clears a load (e.g. one stranded by redirecting a haul mid-trip).
//
// Preconditions (re-checked at resolution time):
//   * Unit exists and is owned by PlayerId.
//   * Unit is carrying cargo (CargoAmount > 0).
//   * Unit is Idle (retask before unloading — same discipline as TrainUnitIntent).
//   * Unit is not in a group / embarked.
public sealed class UnloadCargoIntent : Intent
{
    public int UnitId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public UnloadCargoIntent(int unitId) { UnitId = unitId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.GroupId is not null)
            return IntentOutcome.Reject($"unit {UnitId} is in a group");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        if (unit.CargoAmount <= 0 || unit.CargoResource == Resource.None)
            return IntentOutcome.Reject($"unit {UnitId} is not carrying anything");

        var tile = unit.Position;
        var resource = unit.CargoResource;
        var amount = unit.CargoAmount;

        // Deposit into an own structure on this tile if there is one; the remainder
        // (capacity/need overflow, or the whole load when there's no structure) goes
        // to the ground.
        var deposited = 0;
        if (world.Structures.TryGetValue(tile, out var s) && s.OwnerId == PlayerId)
            deposited = CargoTransfer.DepositInto(sim, s, resource, amount);

        var leftover = amount - deposited;
        if (leftover > 0)
            CargoTransfer.DropToGround(world, tile, resource, leftover);

        unit.CargoAmount = 0;
        unit.CargoResource = Resource.None;
        unit.BumpEpoch();   // defensive: fence any latent per-unit event (Idle had none)

        return IntentOutcome.Applied;
    }

    public override string Describe() => $"Unload(unit={UnitId})";
}
