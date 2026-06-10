using Sim.Core.World;

namespace Sim.Core.Equipment;

// Instant crafting at a Barracks (docs/equipment-model.md): consume the
// item's CraftCost from the Barracks' own holdings, deposit 1 finished
// item into the same holdings. No unit/smith is required — the pacing
// lives upstream in mining + hauling the inputs.
//
// Preconditions (re-checked at resolution time, fail-clean — ALL inputs
// verified before ANY mutation):
//   * Structure at BarracksTile exists, is a Barracks, owned by PlayerId.
//   * Item has an EquipmentCatalog spec.
//   * Holdings cover every CraftCost entry.
public sealed class CraftEquipmentIntent : Intent
{
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
        if (!world.Structures.TryGetValue(BarracksTile, out var s) || s is not Barracks barracks)
            return IntentOutcome.Reject(
                $"no Barracks at {BarracksTile.X},{BarracksTile.Y}");
        if (barracks.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"Barracks at {BarracksTile.X},{BarracksTile.Y} not owned by player {PlayerId}");
        if (!EquipmentCatalog.TryGetSpec(Item, out var spec))
            return IntentOutcome.Reject($"{Item} is not a craftable equipment item");

        // Fail-clean: verify the full bill of materials before touching
        // anything.
        foreach (var (r, n) in spec.CraftCost)
        {
            if (barracks.AmountOf(r) < n)
                return IntentOutcome.Reject(
                    $"Barracks at {BarracksTile.X},{BarracksTile.Y} lacks {r} " +
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
