using Sim.Core.Intents;

namespace Sim.Core.Diplomacy;

// M6 Phase D — accept or decline an open proposal. Only the addressee
// (proposal.TargetId) may respond.
//
// Rejected if:
//   * proposal missing (already responded to, expired-and-cleaned, etc.);
//   * ResponderId != proposal.TargetId (wrong addressee);
//   * sim.Now > proposal.ExpiryTick (lazy expiry — proposal is invalid;
//     it's also removed from state on this path so subsequent calls don't
//     waste cycles checking it).
//
// On accept: the relationship's state flips to proposal.DesiredState
// IMMEDIATELY (no delay — both consented). If there was a pending hostile
// transition between the same pair, it's CLEARED — the consensual peace
// overrides the in-flight telegraph, and any queued WarBecomesEffectiveEvent
// will no-op via its fence when it eventually fires. The proposal is
// removed.
//
// On decline: the proposal is simply removed; no state change.
public sealed class RespondToProposalIntent : Intent
{
    public int ResponderId { get; }
    public int ProposalId { get; }
    public bool Accept { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public RespondToProposalIntent(int responderId, int proposalId, bool accept)
    {
        ResponderId = responderId;
        ProposalId = proposalId;
        Accept = accept;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var d = sim.World.Diplomacy;
        if (!d.Proposals.TryGetValue(ProposalId, out var proposal))
            return IntentOutcome.Reject($"proposal {ProposalId} not found");
        if (proposal.TargetId != ResponderId)
            return IntentOutcome.Reject(
                $"responder {ResponderId} is not the addressee of proposal {ProposalId} " +
                $"(target={proposal.TargetId})");

        if (sim.Now > proposal.ExpiryTick)
        {
            // Lazy expiry — clean up on touch so the next response isn't
            // delayed by a stale row.
            d.RemoveProposal(ProposalId);
            return IntentOutcome.Reject(
                $"proposal {ProposalId} expired at tick {proposal.ExpiryTick} (now={sim.Now})");
        }

        if (!Accept)
        {
            d.RemoveProposal(ProposalId);
            return IntentOutcome.Applied;
        }

        // Accept path: transition takes effect immediately. Both sides
        // consented; there's no "caught off guard" risk to telegraph against.
        var pair = FactionPair.Of(proposal.ProposerId, proposal.TargetId);
        d.SetState(pair, proposal.DesiredState);
        // Consensual peace overrides any in-flight hostile transition.
        if (d.Relationships.TryGetValue(pair, out var rel) && rel.HasPendingWar)
            d.ClearPending(pair);
        d.RemoveProposal(ProposalId);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"RespondToProposal(responder={ResponderId} proposal={ProposalId} accept={Accept})";
}
