namespace Sim.Core.Boats;

// M12 — boat tuning knobs. Per-hull values live here; per-dock cadence
// lives on the Dock's StructureSpec (ProductionPeriodTicks). Cargo
// capacity now lives in UnitCargoCatalog (BoatCapacity) since
// CargoCapacity is derived from UnitRole.
public static class BoatConstants
{
    // Passenger cap for the launch hull. Tunable.
    public const int DefaultPassengerCap = 4;
}
