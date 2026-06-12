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
//   * Unit is empty, or already carrying THIS resource with capacity free.
//   * Something of `Resource` is actually on the tile.
public sealed class LoadCargoIntent : Intent
{
    public int UnitId { get; }
    public Resource Resource { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public LoadCargoIntent(int unitId, Resource resource)
    {
        UnitId = unitId;
        Resource = resource;
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
        if (unit.GroupId is not null)
            return IntentOutcome.Reject($"unit {UnitId} is in a group");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        if (unit.CargoAmount > 0 && unit.CargoResource != Resource)
            return IntentOutcome.Reject(
                $"unit {UnitId} already carries {unit.CargoResource} (unload first)");

        var space = unit.CargoCapacity - unit.CargoAmount;
        if (space <= 0)
            return IntentOutcome.Reject($"unit {UnitId} has no cargo space free");

        var tile = unit.Position;
        world.Structures.TryGetValue(tile, out var source);

        // M19 — stealing FOOD from a FOOD HOME (this IS the bandit verb)
        // shifts its dry-out: catch up first so the theft can't take food
        // the lazy clock already ate, re-evaluate after so the famine
        // prediction tracks the robbed level. Without this a stale check
        // back-dated famine onset past the grace window on small house
        // caches (the schedule-in-the-past crash the bandit lab caught).
        var foodHome = source is Sim.Core.Food.IFoodHome fh && Resource == Resource.Food
            ? fh : null;
        if (foodHome is not null)
            Sim.Core.Food.FoodConsumption.CatchUp(foodHome, sim, sim.Now);

        // Structure first (HaulPickupEvent's source order), ground pile
        // for whatever space remains.
        var loaded = 0;
        switch (source)
        {
            case StorageStructure ss:
                loaded += ss.Withdraw(Resource, space);
                break;
            case Extractor ex when ex.Spec.OutputResource == Resource && ex.Buffer > 0:
                loaded += Math.Min(space, ex.Buffer);
                ex.Buffer -= loaded;
                // Phase-D hook: freeing buffer space may re-arm dormant production.
                ex.ArmIfDormant(sim);
                break;
        }
        if (foodHome is not null && loaded > 0)
            Sim.Core.Food.FoodConsumption.OnRateOrFoodChanged(foodHome, sim);
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

        unit.CargoResource = Resource;
        unit.CargoAmount += loaded;
        unit.BumpEpoch();   // defensive: fence any latent per-unit event (Idle had none)

        return IntentOutcome.Applied;
    }

    public override string Describe() => $"Load(unit={UnitId} {Resource})";
}
