using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M6 Phase E: diplomatic state surfaces in PlayerView.
//
// Public knowledge (every player sees these identically):
//   * Factions — every registered player.
//   * Relationships — every pair's current state + pending-war anchor.
//   * PendingWars — every pending war in the world.
//
// Per-viewer scoped:
//   * IncomingProposals — only the target sees offers addressed to them.
//     Offers stay private until accepted (accepted = public relationship
//     change).
public class DiplomacyPlayerViewTests
{
    private const long Delay = 100;
    private const long Expiry = 200;

    private static GameWorld MakeThreeFactionWorld()
    {
        return Genesis.Build(new GenesisSpec
        {
            Width = 40, Height = 40,
            Diplomacy = new DiplomacyConfig(Delay: Delay, ProposalExpiryTicks: Expiry),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(20, 2) },
                new FactionStartSpec { OwnerId = 2, CastlePosition = new TileCoord(35, 35) },
            },
        });
    }

    [Fact]
    public void EveryView_ListsEveryFaction()
    {
        var world = MakeThreeFactionWorld();
        for (var pid = 0; pid <= 2; pid++)
        {
            var view = View.BuildPlayerView(world, pid);
            Assert.Equal(3, view.Factions.Count);
            Assert.Contains(view.Factions, f => f.Id == 0);
            Assert.Contains(view.Factions, f => f.Id == 1);
            Assert.Contains(view.Factions, f => f.Id == 2);
        }
    }

    [Fact]
    public void EveryView_ListsEveryRelationship()
    {
        var world = MakeThreeFactionWorld();
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.Diplomacy.SetState(FactionPair.Of(0, 2), RelationshipState.Ally);

        for (var pid = 0; pid <= 2; pid++)
        {
            var view = View.BuildPlayerView(world, pid);
            // Both transitioned pairs are visible to every faction.
            Assert.Contains(view.Relationships,
                r => r.LoId == 0 && r.HiId == 1 && r.State == RelationshipState.Enemy);
            Assert.Contains(view.Relationships,
                r => r.LoId == 0 && r.HiId == 2 && r.State == RelationshipState.Ally);
        }
    }

    [Fact]
    public void PendingWar_VisibleToEveryPlayer()
    {
        // Belligerents see it; third-party non-belligerent also sees it
        // (public knowledge — pin this so future code doesn't silently re-fog).
        var world = MakeThreeFactionWorld();
        var sim = new Simulation(world, seed: 0xA0F);
        sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
        sim.Run(until: 10);

        for (var pid = 0; pid <= 2; pid++)
        {
            var view = View.BuildPlayerView(world, pid);
            Assert.Single(view.PendingWars);
            Assert.Equal(0, view.PendingWars[0].LoId);
            Assert.Equal(1, view.PendingWars[0].HiId);
            Assert.Equal(10 + Delay, view.PendingWars[0].EffectiveTick);
        }
    }

    [Fact]
    public void IncomingProposals_ScopedToTargetOnly()
    {
        var world = MakeThreeFactionWorld();
        var sim = new Simulation(world, seed: 0xA0F);
        // Faction 0 proposes alliance to faction 2.
        sim.SubmitIntent(10, new ProposeRelationshipIntent(0, 2, RelationshipState.Ally));
        sim.Run(until: 10);

        // Target (faction 2) sees it.
        var view2 = View.BuildPlayerView(world, 2);
        Assert.Single(view2.IncomingProposals);
        Assert.Equal(0, view2.IncomingProposals[0].ProposerId);
        Assert.Equal(RelationshipState.Ally, view2.IncomingProposals[0].DesiredState);

        // Proposer (faction 0) does NOT see the proposal as "incoming."
        var view0 = View.BuildPlayerView(world, 0);
        Assert.Empty(view0.IncomingProposals);

        // Third party (faction 1) does NOT see it.
        var view1 = View.BuildPlayerView(world, 1);
        Assert.Empty(view1.IncomingProposals);
    }
}
