namespace Sim.Core.Logistics;

// Shared cargo-transfer primitives used by BOTH HaulDepositEvent and the explicit
// UnloadCargoIntent. Centralising keeps the M13 Castle-food catch-up / famine handling
// and the construction-site start hook in ONE place, so the haul-deposit and manual-
// unload paths stay byte-for-byte consistent.
public static class CargoTransfer
{
    // Deposit up to `amount` of `resource` into `dest`; returns how much was actually
    // accepted (capacity- or need-limited). Handles the Castle food catch-up + famine
    // re-evaluation (M13) and the construction-site start-on-conditions-met hook.
    // Touches only the destination structure — never the carrying unit's cargo (the
    // caller owns that side).
    public static int DepositInto(Simulation sim, Structure dest, Resource resource, int amount)
    {
        if (amount <= 0 || resource == Resource.None) return 0;

        // M13 — depositing Food into a Castle: catch consumption up FIRST so the
        // pre-deposit Holdings is correct, then deposit, then re-evaluate the famine.
        var foodCastle = (dest is Castle c0 && resource == Resource.Food) ? c0 : null;
        if (foodCastle is not null)
            Sim.Core.Food.FoodConsumption.CatchUp(foodCastle, sim, sim.Now);

        var deposited = dest switch
        {
            StorageStructure ss => ss.Deposit(resource, amount),
            ConstructionSite c  => c.Deposit(resource, amount),
            _ => 0,
        };

        // M13 Phase C — a deposit that lifts Holdings[Food] above 0 ends an active
        // famine; always re-evaluate the predicted next dry-out. The in-flight
        // StarvationDeathEvent is intentionally left to fence itself (see
        // HaulDepositEvent's note) so a trickle of deposits can't reset the clock.
        if (foodCastle is not null)
        {
            if (foodCastle.FamineStartTick.HasValue && foodCastle.AmountOf(Resource.Food) > 0)
                foodCastle.FamineStartTick = null;
            Sim.Core.Food.FoodConsumption.OnRateOrFoodChanged(foodCastle, sim);
        }

        if (dest is ConstructionSite site && !site.IsActive && site.ConditionsMet(sim.World))
            site.StartOrResume(sim);

        return deposited;
    }

    // Drop loose cargo onto a tile's ground pile — the same capture-economy pile a
    // dying laden unit leaves (see CombatRules.OnUnitDeath). Re-haulable.
    public static void DropToGround(GameWorld world, TileCoord tile, Resource resource, int amount)
    {
        if (amount <= 0 || resource == Resource.None) return;
        if (!world.GroundResources.TryGetValue(tile, out var pile))
        {
            pile = new SortedDictionary<Resource, int>();
            world.GroundResources[tile] = pile;
        }
        pile.TryGetValue(resource, out var existing);
        pile[resource] = existing + amount;
    }
}
