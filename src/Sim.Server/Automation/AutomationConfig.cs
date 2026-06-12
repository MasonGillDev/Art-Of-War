namespace Sim.Server.Automation;

// M18 — driver-side knobs (the Core-side caps live in
// Sim.Core.Automation.AutomationConstants). Same shape as BanditConfig:
// host-constructed, immutable, no world serialization — the driver's brain
// is ephemeral; only its submitted intents are durable.
public sealed class AutomationConfig
{
    public bool Enabled { get; init; } = true;

    // Self-gate inside the clock loop: at most one evaluation pass per
    // player per this many sim ticks.
    public long ThinkPeriodTicks { get; init; } = 60;

    // Consecutive no-progress thinks on one step before the driver
    // auto-disables the order (CursorOp.Disable) and the player gets a
    // notice. The structural anti-wedge rule.
    public int MaxStepRetries { get; init; } = 8;
}
