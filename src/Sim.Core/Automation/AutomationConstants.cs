namespace Sim.Core.Automation;

// Automation balance knobs (docs/automation-substrate.md). Constants, not
// world-serialized config: they're enforced only at SetOrderIntent
// resolution time, so retuning them never invalidates existing snapshots —
// an order that was legal when set stays in the world even if the cap is
// later lowered. Same precedent as RoadConstants. Tests derive from these
// values; never hard-code them (see the biome-degrade-period lesson).
public static class AutomationConstants
{
    // Max stops in one Routine circuit. Bounds snapshot size and driver
    // work per order.
    public const int MaxStepsPerOrder = 16;

    // Max named crew one order may claim. Bounds the claim-exclusivity scan.
    public const int MaxClaimedUnitsPerOrder = 16;
}
