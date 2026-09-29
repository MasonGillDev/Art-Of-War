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
//
// M36 — cargo can be mixed, and the unload can be TARGETED:
//   * Resource = None (the default): empty everything, as above. Each
//     resource is offered to the structure in enum order; the rest drops.
//   * Resource named: unload up to `Amount` of it (0 = all of it). What the
//     structure can't take STAYS ABOARD — a partial unload is a choice of
//     how much to hand over, not a request to dump. With no accepting
//     structure on the tile it drops to the ground pile instead.
public sealed class UnloadCargoIntent : Intent
{
    public int UnitId { get; }
    public Resource Resource { get; }
    public int Amount { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public UnloadCargoIntent(int unitId, Resource resource = Resource.None, int amount = 0)
    {
        UnitId = unitId;
        Resource = resource;
        Amount = amount;
    }

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
        if (unit.CargoAmount <= 0)
            return IntentOutcome.Reject($"unit {UnitId} is not carrying anything");
        if (Amount < 0)
            return IntentOutcome.Reject($"amount {Amount} is negative");
        if (Resource != Resource.None && unit.Cargo.AmountOf(Resource) <= 0)
            return IntentOutcome.Reject($"unit {UnitId} is not carrying {Resource}");

        var tile = unit.Position;

        // Deposit into an own structure on this tile if there is one.
        //
        // M28 — a laden BOAT parked on an own dock's slip empties into that
        // dock (the quay warehouse): boats never stand on a structure tile,
        // and this is the recovery path for a stranded water haul.
        Structure? dest = null;
        if (world.Structures.TryGetValue(tile, out var s) && s.OwnerId == PlayerId)
            dest = s;
        else if (unit.Traversal == Traversal.Water
                 && HaulStops.OwnDockBySlip(world, tile, PlayerId) is { } quay)
            dest = quay;

        if (Resource == Resource.None)
        {
            // Empty everything: whatever the structure can't take (or all of
            // it, with no structure) goes to the ground. Never destroyed.
            foreach (var (r, held) in new List<KeyValuePair<Resource, int>>(unit.Cargo.Items))
            {
                var deposited = dest is null ? 0 : CargoTransfer.DepositInto(sim, dest, r, held);
                CargoTransfer.DropToGround(world, tile, r, held - deposited);
            }
            unit.Cargo.Clear();
        }
        else
        {
            var want = unit.Cargo.AmountOf(Resource);
            if (Amount > 0) want = Math.Min(want, Amount);
            if (dest is null)
            {
                CargoTransfer.DropToGround(world, tile, Resource, want);
                unit.Cargo.Take(Resource, want);
            }
            else
            {
                unit.Cargo.Take(Resource, CargoTransfer.DepositInto(sim, dest, Resource, want));
            }
        }

        unit.BumpEpoch();   // defensive: fence any latent per-unit event (Idle had none)

        return IntentOutcome.Applied;
    }

    public override string Describe() => Resource == Resource.None
        ? $"Unload(unit={UnitId})"
        : $"Unload(unit={UnitId} {(Amount > 0 ? Amount.ToString() : "all")} {Resource})";
}
