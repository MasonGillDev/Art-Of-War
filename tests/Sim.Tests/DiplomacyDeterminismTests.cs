using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M6 Phase E: full diplomacy scenario twin-run. Declare war → effective →
// propose peace → accept → back to neutral. Identical hashes across runs.
public class DiplomacyDeterminismTests
{
    private const long Delay = 50;
    private const long Expiry = 200;

    private static Simulation MakeSim()
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
    public void FullScenario_TwinRunMatches()
    {
        Simulation Run()
        {
            var sim = MakeSim();
            sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
            // Let the war become effective.
            sim.Run(until: 10 + Delay + 5);
            // Now propose peace from the target side, declarer accepts.
            sim.SubmitIntent(sim.Now, new ProposeRelationshipIntent(1, 0, RelationshipState.Neutral));
            sim.Run(until: sim.Now);
            var proposalId = sim.World.Diplomacy.Proposals.Keys.First();
            sim.SubmitIntent(sim.Now, new RespondToProposalIntent(0, proposalId, accept: true));
            sim.Run(until: sim.Now + 100);
            return sim;
        }

        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    [Fact]
    public void FullScenario_RelationshipEndsAtNeutral()
    {
        var sim = MakeSim();
        sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
        sim.Run(until: 10 + Delay + 5);
        Assert.Equal(RelationshipState.Enemy, sim.World.Diplomacy.RelationshipBetween(0, 1));

        sim.SubmitIntent(sim.Now, new ProposeRelationshipIntent(1, 0, RelationshipState.Neutral));
        sim.Run(until: sim.Now);
        var proposalId = sim.World.Diplomacy.Proposals.Keys.First();
        sim.SubmitIntent(sim.Now, new RespondToProposalIntent(0, proposalId, accept: true));
        sim.Run(until: sim.Now);

        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.Empty(sim.World.Diplomacy.Proposals);
    }
}
