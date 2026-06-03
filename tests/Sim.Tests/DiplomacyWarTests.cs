using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M6 Phase C: unilateral, telegraphed war declaration.
//   * Effect lands after exactly Delay ticks.
//   * Rejected for self/missing/already-enemy/already-pending.
//   * Twin-run deterministic.
//   * Pending war survives snapshot+restore (the M4 regen pattern for
//     diplomacy).
public class DiplomacyWarTests
{
    private const long Delay = 100;

    private static Simulation MakeTwoFactionSim()
    {
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            Diplomacy = new DiplomacyConfig(Delay: Delay, ProposalExpiryTicks: 200),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(25, 25) },
            },
        };
        return new Simulation(Genesis.Build(spec), seed: 0xD1A);
    }

    [Fact]
    public void DeclareWar_TakesEffectAfterExactDelay()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(at: 10, new DeclareWarIntent(0, 1));

        // Just before effective tick: still Neutral.
        sim.Run(until: 10 + Delay - 1);
        Assert.Equal(RelationshipState.Neutral, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.True(sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);

        // Effective tick: Enemy.
        sim.Run(until: 10 + Delay);
        Assert.Equal(RelationshipState.Enemy, sim.World.Diplomacy.RelationshipBetween(0, 1));
        Assert.False(sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);
        Assert.True(sim.World.Diplomacy.AreHostile(0, 1));
    }

    [Fact]
    public void DeclareWar_RejectsSelfDeclaration()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(0, new DeclareWarIntent(0, 0));
        sim.Run(until: 10);
        Assert.Empty(sim.World.Diplomacy.Relationships);
    }

    [Fact]
    public void DeclareWar_RejectsMissingFaction()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(0, new DeclareWarIntent(0, 99));
        sim.Run(until: 10);
        Assert.Empty(sim.World.Diplomacy.Relationships);
    }

    [Fact]
    public void DeclareWar_RejectsIfAlreadyEnemy()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(0, new DeclareWarIntent(0, 1));
        sim.Run(until: Delay + 1); // war effective now
        Assert.Equal(RelationshipState.Enemy, sim.World.Diplomacy.RelationshipBetween(0, 1));

        // Second declaration should be a no-op (already enemy).
        sim.SubmitIntent(sim.Now, new DeclareWarIntent(1, 0));
        sim.Run(until: sim.Now + Delay + 10);
        Assert.False(sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);
    }

    [Fact]
    public void DeclareWar_RejectsIfPending()
    {
        var sim = MakeTwoFactionSim();
        sim.SubmitIntent(0, new DeclareWarIntent(0, 1));
        sim.Run(until: Delay / 2); // mid-Delay, war still pending
        var seqBeforeRedeclare = sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].PendingSeq;

        sim.SubmitIntent(sim.Now, new DeclareWarIntent(1, 0));
        sim.Run(until: sim.Now + 1);
        // Pending seq unchanged → second declare was rejected.
        Assert.Equal(seqBeforeRedeclare,
            sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].PendingSeq);
    }

    [Fact]
    public void DeclareWar_TwinRunDeterministic()
    {
        Simulation Run()
        {
            var sim = MakeTwoFactionSim();
            sim.SubmitIntent(10, new DeclareWarIntent(0, 1));
            sim.Run(until: Delay + 50);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    [Fact]
    public void DeclareWar_PendingWar_SnapshotRoundTrip()
    {
        // Path A: uninterrupted run from declaration to past effective tick.
        var simA = MakeTwoFactionSim();
        simA.SubmitIntent(10, new DeclareWarIntent(0, 1));
        simA.Run(until: 10 + Delay + 20);
        var hashA = Snapshot.Hash(simA);

        // Path B: declare, run to mid-Delay, snapshot, restore, run to past effective.
        var simB = MakeTwoFactionSim();
        simB.SubmitIntent(10, new DeclareWarIntent(0, 1));
        simB.Run(until: 10 + Delay / 2); // mid-Delay
        Assert.True(simB.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);

        var bytes = Snapshot.Serialize(simB);
        var restored = Snapshot.Restore(bytes, seed: 0xD1A);
        // The pending war's regenerated event must still fire at the right tick.
        Assert.True(restored.World.Diplomacy.Relationships[FactionPair.Of(0, 1)].HasPendingWar);

        restored.Run(until: 10 + Delay + 20);
        Assert.Equal(hashA, Snapshot.Hash(restored));
    }
}
