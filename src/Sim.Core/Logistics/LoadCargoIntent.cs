namespace Sim.Core.Logistics;

// Instant intent — the mirror of UnloadCargoIntent: fill the unit's
// cargo from whatever sits on its OWN tile (structure first, ground
// pile for the remainder), no destination leg. Born in M16 as the
// bandits' STEALING verb, but a general atom available to every player:
// grab a load now, decide where it goes later.
//
// Source ownership is deliberately NOT checked — same stance as
// HaulIntent (docs/intent-authorization.md, "raiding economy by
// design", pinned by M16): loading from a hostile structure's buffer is
// raiding; whether you can stand there alive is combat's problem, not
// authorization's.
//
// Withdraw semantics mirror HaulPickupEvent exactly, including the
// Phase-D hook: freeing extractor buffer space may re-arm dormant
// production — yes, a bandit robbing your lumber camp puts the
// surviving crew back to work.
//
// Preconditions (resolution time):
//   * Unit exists and is owned by PlayerId.
//   * Unit is Idle, not grouped, not embarked (UnloadCargoIntent's discipline).
//   * Unit has cargo space free. Cargo may be MIXED (M36): a unit carrying
//     ore can also load wood, up to its one total capacity.
//   * Something of `Resource` is actually on the tile.
//
// Amount (M36): the most to load; 0 = as much as fits.
public sealed class LoadCargoIntent : Intent
{
    public int UnitId { get; }
    public Resource Resource { get; }
    public int Amount { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public LoadCargoIntent(int unitId, Resource resource, int amount = 0)
    {
        UnitId = unitId;
        Resource = resource;
        Amount = amount;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (Resource == Resource.None)
            return IntentOutcome.Reject("no resource named");
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (Sim.Core.Groups.GroupRules.UnderCommand(world, unit))
            return IntentOutcome.Reject($"unit {UnitId} is in a group");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        // M41 — nobody loads cargo on an open battlefield (build decision D7): the
        // tile is fought over first; looting starts when the board closes.
        if (world.Battlefields.ContainsKey(unit.Position))
            return IntentOutcome.Reject($"unit {UnitId} is on a battlefield");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        if (Amount < 0)
            return IntentOutcome.Reject($"amount {Amount} is negative");

        var space = unit.CargoCapacity - unit.CargoAmount;
        if (space <= 0)
            return IntentOutcome.Reject($"unit {UnitId} has no cargo space free");
        if (Amount > 0) space = Math.Min(space, Amount);

        var tile = unit.Position;
        world.Structures.TryGetValue(tile, out var source);

        // Structure first (HaulPickupEvent's source order), ground pile for
        // whatever space remains. WithdrawFrom carries the M19 food-home
        // catch-up (this IS the bandit's stealing verb, and a stale check
        // once back-dated famine onset past the grace window) and the
        // Phase-D extractor re-arm.
        var loaded = source is null ? 0 : CargoTransfer.WithdrawFrom(sim, source, Resource, space);
        if (loaded < space
            && world.GroundResources.TryGetValue(tile, out var pile)
            && pile.TryGetValue(Resource, out var groundAmount) && groundAmount > 0)
        {
            var take = Math.Min(space - loaded, groundAmount);
            var remaining = groundAmount - take;
            if (remaining <= 0) pile.Remove(Resource); else pile[Resource] = remaining;
            if (pile.Count == 0) world.GroundResources.Remove(tile);
            loaded += take;
        }

        if (loaded == 0)
            return IntentOutcome.Reject($"nothing to load (no {Resource} on tile)");

        unit.Cargo.Add(Resource, loaded);
        unit.BumpEpoch();   // defensive: fence any latent per-unit event (Idle had none)

        return IntentOutcome.Applied;
    }

    public override string Describe() => Amount > 0
        ? $"Load(unit={UnitId} {Amount} {Resource})"
        : $"Load(unit={UnitId} {Resource})";
}
