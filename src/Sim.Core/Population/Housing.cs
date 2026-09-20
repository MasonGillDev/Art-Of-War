using Sim.Core.World;

namespace Sim.Core.Population;

// Housing buffs (docs/housing-buffs.md). A SETTLED unit — one whose home is a
// House of its own faction whose pantry is not in debt — works harder and
// fights harder. A castle-homed unit is an overflow mouth sleeping in the
// yard and gets nothing; a resident of a starving house also gets nothing,
// and NO penalty (user decision, 2026-09-19): famine already kills, it does
// not need to weaken first.
//
// Balance knobs, not world-serialized config: same precedent as
// RoadConstants / FoodConsumptionConstants. Tests derive from these.
public static class HousingConstants
{
    // Flat power added to a settled unit's EffectivePower (the world-aware
    // overload, beside the king's aura — a fact about the unit's home, not
    // about the body).
    public const int SettledPowerBonus = 1;

    // Extra output per production period for each settled worker at an
    // extractor. Additive, not a multiplier: per-worker rates are 1 or 2, so
    // any fraction would floor to nothing. +1 on a matching-role worker is
    // +50%, on a mismatched one +100% — housing is meant to be the single
    // biggest thing a player can do for a field short of staffing it right.
    public const int SettledWorkBonusPerWorker = 1;
}

public static class Housing
{
    // PURE READ. Settled = homed at a living own House whose effective food
    // level is not negative. FoodConsumption.CurrentLevel is period-quantised
    // and never depends on whether the lazy catch-up has run, so this answer
    // is identical on every replay and every restore.
    public static bool IsSettled(GameWorld world, Unit unit, long now)
    {
        if (unit.Home is not { } tile) return false;
        if (!world.Structures.TryGetValue(tile, out var s) || s is not House house) return false;
        if (house.OwnerId != unit.OwnerId) return false;
        return Sim.Core.Food.FoodConsumption.CurrentLevel(house, world, now) >= 0;
    }

    public static int SettledPowerBonus(GameWorld world, Unit unit, long now) =>
        IsSettled(world, unit, now) ? HousingConstants.SettledPowerBonus : 0;

    public static int SettledWorkBonus(GameWorld world, Unit unit, long now) =>
        IsSettled(world, unit, now) ? HousingConstants.SettledWorkBonusPerWorker : 0;
}
