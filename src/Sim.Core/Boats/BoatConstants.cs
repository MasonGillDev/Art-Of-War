namespace Sim.Core.Boats;

// M12 — boat tuning knobs. Per-hull values live here; per-dock cadence
// lives on the Dock's StructureSpec (ProductionPeriodTicks).
public static class BoatConstants
{
    // Passenger cap for the launch hull. Tunable.
    public const int DefaultPassengerCap = 4;

    // Cargo capacity per boat. Boats are bigger carriers than
    // ground haulers (CargoCapacity defaults at 1) — they let you
    // move bulk over water.
    public const int DefaultCargoCapacity = 100;
}
