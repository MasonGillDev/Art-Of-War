namespace Sim.Core.Diplomacy;

// World-level diplomacy configuration. Set at Genesis time, immutable for
// the world's lifetime, serialized in the snapshot so recovery resumes with
// the producing world's values.
//
// Delay              — Ticks between DeclareWarIntent and the war taking
//                      effect. The aggressor commits; the target sees the
//                      pending war in their PlayerView for this many ticks
//                      before it bites. The telegraph IS the fairness
//                      mechanism (you can't require target consent to be
//                      attacked, so the delay + visibility is what makes
//                      aggression fair).
// ProposalExpiryTicks — How long a peace/ally proposal stays live before
//                      becoming invalid. Lazy expiry: a proposal whose
//                      ExpiryTick has passed simply rejects responses; no
//                      event fires for the expiry itself.
public readonly record struct DiplomacyConfig(long Delay, long ProposalExpiryTicks)
{
    // Sensible defaults; tests and the host can override at world-build time.
    // War telegraph = 6 game-hours, matching the "effective in 6 hours" line
    // from the original design doc — a half-day window in which the target
    // can see the pending war in their PlayerView before it bites.
    // ProposalExpiryTicks left at the legacy ~3.3-hour value, pending a
    // dedicated tuning pass.
    public DiplomacyConfig() : this(Delay: 1 * Time.Month, ProposalExpiryTicks: 2 * Time.Week) { }
}
