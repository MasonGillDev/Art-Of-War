using Sim.Core;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Landing;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Two-act pacing (docs/two-act-pacing.md): the landing is a tick in the world's
// genesis config. These pin what the SIM knows about it — when it is, the
// prelude's truce, and that it survives a snapshot. The pace itself is the
// host's (PaceScheduleTests).
public class LandingTests
{
    private const long Landing = 1_000;

    private static Simulation MakeSim(LandingConfig landing) =>
        new(Genesis.Build(new GenesisSpec
        {
            Width = 30, Height = 30,
            Diplomacy = new DiplomacyConfig(Delay: 50, ProposalExpiryTicks: 200),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(25, 25) },
            },
            Landing = landing,
        }), seed: 0x1A4D);

    // Every declaration the sim resolved, in order, with its outcome.
    private static List<IntentOutcome> Declarations(Simulation sim) =>
        sim.ResolvedLog.OfType<IntentEvent>()
            .Where(e => e.Intent is DeclareWarIntent)
            .Select(e => e.Outcome)
            .ToList();

    [Fact]
    public void ADefaultWorld_HasOneAct()
    {
        var sim = new Simulation(Genesis.Build(new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[] { new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) } },
        }), seed: 1);

        Assert.False(sim.World.LandingConfig.Enabled);
        foreach (var t in new long[] { 0, 1, Landing, 10 * Landing })
        {
            Assert.False(LandingRules.HasLanded(sim.World, t));
            Assert.False(LandingRules.TruceHolds(sim.World, t));
        }
    }

    [Fact]
    public void OnDay_CountsWholeGameDays_AndNothingMeansNoLanding()
    {
        Assert.Equal(30L * Time.Day, LandingConfig.OnDay(30).Tick);
        Assert.False(LandingConfig.OnDay(0).Enabled);
        Assert.False(LandingConfig.OnDay(-3).Enabled);
    }

    // The landing tick itself belongs to the second act: the truce ends and the
    // landing has happened ON day X, not a tick later.
    [Fact]
    public void TheLandingTick_IsTheFirstTickOfTheSecondAct()
    {
        var world = MakeSim(new LandingConfig(Landing)).World;

        Assert.True(LandingRules.TruceHolds(world, Landing - 1));
        Assert.False(LandingRules.HasLanded(world, Landing - 1));

        Assert.False(LandingRules.TruceHolds(world, Landing));
        Assert.True(LandingRules.HasLanded(world, Landing));
    }

    [Fact]
    public void WarCannotBeDeclared_UntilTheLanding()
    {
        var sim = MakeSim(new LandingConfig(Landing));
        sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
        sim.SubmitIntent(Landing - 1, new DeclareWarIntent(0, 1));
        sim.SubmitIntent(Landing, new DeclareWarIntent(0, 1));
        sim.Run(until: Landing);

        var outcomes = Declarations(sim);
        Assert.Equal(3, outcomes.Count);
        Assert.True(outcomes[0].IsRejected);
        Assert.Contains("landing", outcomes[0].Reason);
        Assert.True(outcomes[1].IsRejected);
        Assert.False(outcomes[2].IsRejected);
        Assert.True(sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);
    }

    // A one-act world keeps the pre-landing behaviour exactly: nothing to wait for.
    [Fact]
    public void WithoutALanding_WarIsDeclaredAsBefore()
    {
        var sim = MakeSim(default);
        sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
        sim.Run(until: 10);

        Assert.False(Declarations(sim).Single().IsRejected);
    }

    // A malformed declaration still says what is wrong with it, truce or not.
    [Fact]
    public void DuringTheTruce_AMalformedDeclarationKeepsItsOwnReason()
    {
        var sim = MakeSim(new LandingConfig(Landing));
        var outcome = new DeclareWarIntent(0, 0).Resolve(sim);

        Assert.True(outcome.IsRejected);
        Assert.DoesNotContain("landing", outcome.Reason);
    }

    [Fact]
    public void LandingRules_ArePureReads()
    {
        var sim = MakeSim(new LandingConfig(Landing));
        sim.Run(until: 5);
        var before = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            LandingRules.HasLanded(sim.World, i * 37);
            LandingRules.TruceHolds(sim.World, i * 37);
        }

        Assert.Equal(before, Snapshot.Hash(sim));
    }

    [Fact]
    public void TheLanding_SurvivesASnapshot()
    {
        var sim = MakeSim(new LandingConfig(Landing));
        sim.SubmitIntent(10, new DeclareWarIntent(0, 1));   // rejected: consumes a Seq, logs nothing durable
        sim.Run(until: 20);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0x1A4D);

        Assert.Equal(new LandingConfig(Landing), restored.World.LandingConfig);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void TheLandingTick_IsPartOfTheHash()
    {
        // Two worlds differing only in when the landing comes must not hash equal,
        // or a snapshot could silently restore the wrong act.
        Assert.NotEqual(
            Snapshot.Hash(MakeSim(new LandingConfig(Landing))),
            Snapshot.Hash(MakeSim(new LandingConfig(Landing + 1))));
    }

    [Fact]
    public void TheTruce_IsDeterministic()
    {
        string Run()
        {
            var sim = MakeSim(new LandingConfig(Landing));
            sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
            sim.SubmitIntent(Landing, new DeclareWarIntent(1, 0));
            sim.Run(until: Landing + 100);
            return Snapshot.Hash(sim);
        }

        Assert.Equal(Run(), Run());
    }
}
