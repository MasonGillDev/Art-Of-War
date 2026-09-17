namespace Sim.Core.Rivers;

// Tuning constants for rivers. A user balance knob — tests derive from it,
// never hard-code it (the same rule as every other config constant).
public static class RiverConstants
{
    // Flat cost, in game-minutes, added to a Foot hop whose shared edge
    // carries a river. Fording is a fixed chore — find the shallows, wade,
    // dry out — so it is ADDED to terrain, not multiplied, and roads on the
    // far bank do not reduce it (that is what a bridge will be for).
    //
    // 120 = four grassland tiles' worth: A* detours up to ~4 km around a
    // river before it chooses to ford, so rivers shape routes without ever
    // walling anything off (the whole map stays reachable, as with Water).
    public const int CrossingCost = 120;
}
