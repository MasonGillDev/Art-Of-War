using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M6 Phase D: bilateral handshake for peace and alliance.
//   * Accept transitions immediately (no delay — both consented).
//   * Decline removes the proposal, no state change.
//   * Expiry is passive (lazy check on response).
//   * Only the target may respond; third parties rejected.
//   * Peace overrides a pending war (consensual override clears the
//     hostile anchor).
public class DiplomacyProposalTests
{
    private const long Delay = 100;
    private const long Expiry = 200;

    private static Simulation MakeTwoFactionSim()
    {
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            Diplomacy = new DiplomacyConfig(Delay: Delay, ProposalExpiryTicks: Expiry),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(25, 25) },
            },
        };
        return new Simulation(Genesis.Build(spec), seed: 0xD1A);
    }

    [Fact]
    public void ProposeAccept_TransitionsImmediately()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 1, RelationshipState.Ally));
        sim.Run(until: 10);
        var proposalId = sim.World.Diplomacy.Proposals.Keys.First();

        sim.SubmitIntent(20, new RespondToProposalIntent(1, proposalId, accept: true));
        sim.Run(until: 21);
        Assert.Equal(RelationshipState.Ally, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.Empty(sim.World.Diplomacy.Proposals);
    }

    [Fact]
    public void ProposeDecline_NoStateChange()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 1, RelationshipState.Ally));
        sim.Run(until: 10);
        var proposalId = sim.World.Diplomacy.Proposals.Keys.First();

        sim.SubmitIntent(20, new RespondToProposalIntent(1, proposalId, accept: false));
        sim.Run(until: 21);
        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.Empty(sim.World.Diplomacy.Proposals);
    }

    [Fact]
    public void Propose_ExpiresPassively()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 1, RelationshipState.Ally));
        sim.Run(until: 10);
        var proposalId = sim.World.Diplomacy.Proposals.Keys.First();

        // Accept AFTER the expiry tick → rejected; proposal removed on touch.
        sim.SubmitIntent(10 + Expiry + 1, new RespondToProposalIntent(1, proposalId, accept: true));
        sim.Run(until: 10 + Expiry + 2);
        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.Empty(sim.World.Diplomacy.Proposals);
    }

    [Fact]
    public void WrongResponder_Rejected()
    {
        var sim = new Simulation(Genesis.Build(new GenesisSpec
        {
            Width = 30, Height = 30,
            Diplomacy = new DiplomacyConfig(Delay: Delay, ProposalExpiryTicks: Expiry),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(15, 2) },
                new FactionStartSpec { OwnerId = 2, CastlePosition = new TileCoord(25, 25) },
            },
        }), seed: 0xD1A);

        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 1, RelationshipState.Ally));
        sim.Run(until: 10);
        var proposalId = sim.World.Diplomacy.Proposals.Keys.First();

        // Faction 2 tries to accept faction 1's proposal — must reject.
        sim.SubmitIntent(20, new RespondToProposalIntent(2, proposalId, accept: true));
        sim.Run(until: 21);
        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.NotEmpty(sim.World.Diplomacy.Proposals); // proposal still open
    }

    [Fact]
    public void OneSidedAccept_WithNoMatchingProposal_Rejected()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new RespondToProposalIntent(1, proposalId: 999, accept: true));
        sim.Run(until: 11);
        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
    }

    [Fact]
    public void Propose_RejectsEnemyDesiredState()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 1, RelationshipState.Enemy));
        sim.Run(until: 11);
        // Aggression must go through DeclareWarIntent, not propose/accept.
        Assert.Empty(sim.World.Diplomacy.Proposals);
    }

    [Fact]
    public void PeaceDuringPendingWar_OverridesTheTelegraph()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
        sim.Run(until: 10 + Delay / 4); // mid-Delay
        Assert.True(sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);

        // Propose peace; the target accepts before the war becomes effective.
        sim.SubmitIntent(sim.Now, new ProposeRelationshipIntent(1, 0, RelationshipState.Neutral));
        sim.Run(until: sim.Now);
        var proposalId = sim.World.Diplomacy.Proposals.Keys.First();
        sim.SubmitIntent(sim.Now, new RespondToProposalIntent(0, proposalId, accept: true));
        sim.Run(until: sim.Now);

        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.False(sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);

        // Now run past where the war would have taken effect — the
        // WarBecomesEffectiveEvent must fence and no-op.
        sim.Run(until: 10 + Delay + 10);
        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
    }

    [Fact]
    public void Proposal_SnapshotRoundTrip()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 1, RelationshipState.Ally));
        sim.Run(until: 10);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xD1A);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Single(restored.World.Diplomacy.Proposals);
        var p = restored.World.Diplomacy.Proposals.Values.First();
        Assert.Equal(0, p.ProposerId);
        Assert.Equal(1, p.TargetId);
        Assert.Equal(RelationshipState.Ally, p.DesiredState);
    }
}
