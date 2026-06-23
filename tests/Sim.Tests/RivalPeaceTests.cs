using Sim.Core.Diplomacy;
using Sim.Core.World;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Tests;

// M25 Phase 5 — war termination (docs/m25-rival-spec.md). Wars must be able to
// END: a Warlord presses to conquest (verified in RivalCampaignTests), while
// limited postures sue for peace and any non-Warlord takes an olive branch. The
// loop only closes if someone proposes AND someone accepts — both pinned here.
public class RivalPeaceTests
{
    private const long Now = 5000;
    private static readonly AiConfig Warlord = new() { Personality = AiPersonality.Warlord };
    private static readonly AiConfig Opportunist = new() { Personality = AiPersonality.Opportunist };

    private static RelationshipDto Rel(int lo, int hi, RelationshipState st) =>
        new() { LoId = lo, HiId = hi, State = (int)st };

    // Viewer 0, fed, factions {0,1}, plus whatever diplomacy the test sets.
    private static ViewDto Base() => new()
    {
        PlayerId = 0, Width = 200, Height = 200, Tick = Now,
        Population = 20, CastleFood = 2000, FoodRunwayTicks = 100_000_000,
        Structures = new[] { new StructDto { X = 10, Y = 10, Kind = (int)StructureKind.Castle, OwnerId = 0 } },
        Factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } },
    };

    private static ProposalDto PeaceOffer(int from, int to, int id = 7) => new()
    {
        Id = id, ProposerId = from, TargetId = to,
        DesiredState = (int)RelationshipState.Neutral, ExpiryTick = Now + 100_000,
    };

    // A Homesteader dragged into a war takes the olive branch.
    [Fact]
    public void Homesteader_AcceptsPeace()
    {
        var view = Base();
        view.Relationships = new[] { Rel(0, 1, RelationshipState.Enemy) };
        view.IncomingProposals = new[] { PeaceOffer(from: 1, to: 0) };
        var intents = new RivalRung().Perceive(ThinkContext.Build(view, new AiConfig(), new AiMemory(), Now));

        var resp = Assert.Single(intents.OfType<RespondToProposalIntent>());
        Assert.Equal(7, resp.ProposalId);
        Assert.True(resp.Accept);
    }

    // A Warlord refuses every offer — it plays for the kill.
    [Fact]
    public void Warlord_RefusesPeace()
    {
        var view = Base();
        view.Relationships = new[] { Rel(0, 1, RelationshipState.Enemy) };
        view.IncomingProposals = new[] { PeaceOffer(from: 1, to: 0) };
        var intents = new RivalRung().Perceive(ThinkContext.Build(view, Warlord, new AiMemory(), Now));

        Assert.Empty(intents.OfType<RespondToProposalIntent>());
    }

    // A limited posture sues for peace once the grievance is settled — and only
    // once (the proposal flag throttles a non-deduping intent).
    [Fact]
    public void Opportunist_SuesForPeace_WhenGrievanceSettled()
    {
        var view = Base();   // at war, but nothing of the enemy's in sight = grievance settled
        view.Relationships = new[] { Rel(0, 1, RelationshipState.Enemy) };
        var mem = new AiMemory { CampaignTarget = 1, CampaignReason = "encroachment", CampaignObjective = null };
        var ctx = ThinkContext.Build(view, Opportunist, mem, Now);

        var first = new RivalRung().Perceive(ctx);
        var prop = Assert.Single(first.OfType<ProposeRelationshipIntent>());
        Assert.Equal(1, prop.TargetId);
        Assert.Equal(RelationshipState.Neutral, prop.DesiredState);
        Assert.Equal(1, mem.PeaceProposedTo);

        // Same state next think → no second proposal (it's already on the table).
        Assert.Empty(new RivalRung().Perceive(ctx).OfType<ProposeRelationshipIntent>());
    }

    // Accepting/Making peace ends the campaign: once the pair is no longer at
    // war, Bookkeep stands the army down.
    [Fact]
    public void Campaign_Ends_WhenPeaceIsMade()
    {
        var view = Base();   // relationship back to Neutral (peace accepted), no pending war
        var mem = new AiMemory { CampaignTarget = 1, CampaignReason = "encroachment", PeaceProposedTo = 1 };
        new RivalRung().Perceive(ThinkContext.Build(view, Warlord, mem, Now));

        Assert.Null(mem.CampaignTarget);
        Assert.Null(mem.PeaceProposedTo);
    }
}
