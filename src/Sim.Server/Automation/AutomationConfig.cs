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

    // Substrate (docs/automation-substrate.md): consecutive FRUITLESS
    // FIRINGS before an order auto-disables. Distinct from MaxStepRetries
    // because the substrate has no steps — an order either fires, waits
    // (free, never counted), or fires fruitlessly (counted).
    //
    // Sized for the async pace: at one think per game-hour, 24 fruitless
    // firings is a full game-day of an order trying and failing before it
    // gives up and says so. Long enough to ride out a transient (a crew
    // walking home laden, a source briefly empty); short enough that a
    // genuinely broken line — crew killed on the border — is reported
    // within a day rather than churning silently for a season.
    public int RetryBudget { get; init; } = 24;
}
