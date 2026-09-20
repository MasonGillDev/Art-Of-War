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

    // How many bodies one PULL haul line may have in flight at once. A line
    // borrows another hand only while every hand it holds is mid-trip and it
    // is still short — so throughput scales with distance (a long haul earns
    // more hands) but never past this. It holds no IDLE hands beyond the one
    // it is dispatching this think: idle surplus is returned to the pool so
    // the next order in the pass can borrow it. Found in play: a castle line
    // holding four haulers while the house line beside it read "no free
    // hauler in reach" with two of them standing idle, claimed.
    //
    // Sized by the keystone lab: at 3 the fat colony's granary artery could
    // not keep up (5 births, worse than the unstaffed colony); at 6 it
    // carries the load (15 births, colony alive at day 160). A per-order
    // "up to N haulers" field is the proper home for this; until then it is
    // a global ceiling, and the idle-surplus release above is the real fix.
    public int MaxPulledHands { get; init; } = 6;
}
