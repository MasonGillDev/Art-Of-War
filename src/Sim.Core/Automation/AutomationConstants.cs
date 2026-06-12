namespace Sim.Core.Automation;

// M18 — automation balance knobs. Constants (not world-serialized config):
// they're enforced only at SetStandingOrderIntent resolution time, so
// retuning them never invalidates existing snapshots — an order that was
// legal when set stays in the world even if the cap is later lowered.
// Same precedent as RoadConstants. Tests derive from these values; never
// hard-code them (see the biome-degrade-period lesson).
public static class AutomationConstants
{
    // Max standing orders a single player may have installed at once.
    // Future unlock-structure tiers raise this; the engine just enforces it.
    public const int MaxOrdersPerPlayer = 16;

    // Max steps in one order. Bounds snapshot size and driver work per order.
    public const int MaxStepsPerOrder = 16;

    // Max units one order may claim. Bounds the claim-exclusivity scan.
    public const int MaxClaimedUnitsPerOrder = 16;
}
