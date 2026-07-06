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
    // War telegraph = 2 game-days (user-retuned 2026-07-06 with M25's Rival:
    // the constant had drifted to a full game-month against this comment's
    // original 6-hour intent, and a month of warning made live wars feel
    // like they never came). Two days is enough to muster and reposition —
    // the M25 Rival mobilizes its offense budget inside exactly this window
    // — while keeping a declaration a THREAT, not a diary entry.
    public DiplomacyConfig() : this(Delay: 2 * Time.Day, ProposalExpiryTicks: 2 * Time.Week) { }
}
