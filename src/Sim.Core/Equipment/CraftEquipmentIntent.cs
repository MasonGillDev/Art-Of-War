using Sim.Core.World;

namespace Sim.Core.Equipment;

// Instant crafting (docs/equipment-model.md): consume the item's CraftCost from
// the crafting building's own holdings, deposit 1 finished item back into the
// same holdings. No unit or smith is required — the pacing lives upstream, in
// mining and hauling the inputs.
//
// NOT GOAL-SHAPED, and it does not need to be: the intent already names its
// target tile and no body travels, so there is no appointment to delete. It is
// an order to a BUILDING (docs/goal-shaped-intents.md, the sweep's audit).
//
// WHICH building comes from the catalog's CraftedAt, not from a type test here.
// This used to read `is not Barracks`, which made the crafter a property of the
// intent rather than of the item — so a Workshop would have needed a second
// intent, and the Cart (a hauler's tool) was forged in a barracks with no way
// to say otherwise.
//
// Preconditions (re-checked at resolution time, fail-clean — ALL inputs
// verified before ANY mutation):
//   * Item has an EquipmentCatalog spec.
//   * Structure at CraftTile exists, is that item's CraftedAt kind, and is
//     owned by PlayerId.
//   * Its holdings cover every CraftCost entry.
public sealed class CraftEquipmentIntent : Intent
{
    // Named BarracksTile when the Barracks was the only forge. Kept verbatim:
    // it is a durable wire name in the intent log, and renaming it would break
    // every replay ever recorded for a purely cosmetic gain.
    public TileCoord BarracksTile { get; }
    public Resource Item { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public CraftEquipmentIntent(TileCoord barracksTile, Resource item)
    {
        BarracksTile = barracksTile;
        Item = item;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        // The ITEM decides which building forges it, so the spec is read first.
        if (!EquipmentCatalog.TryGetSpec(Item, out var spec))
            return IntentOutcome.Reject($"{Item} is not a craftable equipment item");

        if (!world.Structures.TryGetValue(BarracksTile, out var s) || s.Kind != spec.CraftedAt)
            return IntentOutcome.Reject(
                $"no {spec.CraftedAt} at {BarracksTile.X},{BarracksTile.Y} to craft {Item}");
        if (s is not StorageStructure barracks)
            return IntentOutcome.Reject(
                $"{spec.CraftedAt} at {BarracksTile.X},{BarracksTile.Y} holds nothing to craft from");
        if (barracks.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"{spec.CraftedAt} at {BarracksTile.X},{BarracksTile.Y} not owned by player {PlayerId}");

        // Fail-clean: verify the full bill of materials before touching
        // anything.
        foreach (var (r, n) in spec.CraftCost)
        {
            if (barracks.AmountOf(r) < n)
                return IntentOutcome.Reject(
                    $"{spec.CraftedAt} at {BarracksTile.X},{BarracksTile.Y} lacks {r} " +
                    $"({barracks.AmountOf(r)}/{n}) to craft {Item}");
        }

        foreach (var (r, n) in spec.CraftCost)
            barracks.Withdraw(r, n);

        // The withdrawals just freed at least the cost's volume (>= 1 for
        // every catalog row), so the deposit cannot be capacity-blocked.
        // If it ever is, the catalog grew a zero-cost item — fail loudly.
        if (barracks.Deposit(Item, 1) != 1)
            throw new InvalidOperationException(
                $"Craft deposit of {Item} failed at {BarracksTile.X},{BarracksTile.Y} — " +
                $"capacity should have been freed by the input withdrawal.");

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"Craft({Item} @ {BarracksTile.X},{BarracksTile.Y})";
}
