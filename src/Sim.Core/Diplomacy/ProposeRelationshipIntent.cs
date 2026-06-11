using Sim.Core.Intents;

namespace Sim.Core.Diplomacy;

// M6 Phase D — bilateral handshake for peace and alliance. Aggression is
// unilateral (DeclareWarIntent); peace and ally are NOT — both sides must
// consent. This intent records the proposal; RespondToProposalIntent
// accepts or declines.
//
// Rejected if:
//   * proposer == target;
//   * either faction missing from world.Players;
//   * DesiredState == Enemy (aggression goes through DeclareWarIntent, not
//     this path);
//   * the pair is already in the desired state (no-op, nothing to negotiate).
//
// On success: a Proposal with a fresh id is added to Diplomacy.Proposals,
// with ExpiryTick = now + Config.ProposalExpiryTicks. Expiry is passive —
// no event fires; the proposal is simply invalid once now > ExpiryTick
// (RespondToProposalIntent enforces this on the response path).
public sealed class ProposeRelationshipIntent : Intent
{
    public int ProposerId { get; }
    public int TargetId { get; }
    public RelationshipState DesiredState { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ProposeRelationshipIntent(int proposerId, int targetId, RelationshipState desiredState)
    {
        ProposerId = proposerId;
        TargetId = targetId;
        DesiredState = desiredState;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (ProposerId == TargetId)
            return IntentOutcome.Reject($"proposer {ProposerId} cannot propose to itself");
        // M16 — no peace with bandits, in either direction. (Their Player
        // row passes the existence checks, so the reject must be explicit.
        // RespondToProposalIntent needs no guard: a proposal naming bandits
        // can never exist.)
        if (ProposerId == Bandits.BanditConstants.OwnerId || TargetId == Bandits.BanditConstants.OwnerId)
            return IntentOutcome.Reject("the bandit faction is outside diplomacy (always hostile)");
        if (!sim.World.Players.ContainsKey(ProposerId))
            return IntentOutcome.Reject($"proposer {ProposerId} is not a registered faction");
        if (!sim.World.Players.ContainsKey(TargetId))
            return IntentOutcome.Reject($"target {TargetId} is not a registered faction");
        if (DesiredState == RelationshipState.Enemy)
            return IntentOutcome.Reject(
                "DesiredState=Enemy must go through DeclareWarIntent (unilateral, telegraphed)");

        var d = sim.World.Diplomacy;
        var pair = FactionPair.Of(ProposerId, TargetId);
        var currentState = d.RelationshipBetween(ProposerId, TargetId);
        var hasPendingWar = d.Relationships.TryGetValue(pair, out var existing) && existing.HasPendingWar;

        // No-op transition: already at the desired state AND no pending hostile
        // transition that this proposal would override. (Proposing Neutral
        // while state is already Neutral but a war is pending IS meaningful —
        // accepting it cancels the pending war.)
        if (currentState == DesiredState && !hasPendingWar)
            return IntentOutcome.Reject(
                $"factions {ProposerId} and {TargetId} are already in state {DesiredState}");

        var p = new Proposal
        {
            Id = d.NextProposalId,
            ProposerId = ProposerId,
            TargetId = TargetId,
            DesiredState = DesiredState,
            ExpiryTick = sim.Now + d.Config.ProposalExpiryTicks,
        };
        d.AddProposal(p);
        d.NextProposalId++;
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"ProposeRelationship(proposer={ProposerId} target={TargetId} desired={DesiredState})";
}
