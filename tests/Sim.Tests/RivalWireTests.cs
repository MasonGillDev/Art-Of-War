using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Server;
using Sim.Server.Wire;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M25 Phase 0 — the public diplomatic state reaches the wire (docs/m25-rival-spec.md).
// The AI Rival learns hostility through the SAME ViewDto a human client renders;
// these pins prove the channel is public (identical for every viewer), live
// (a declared war shows up, then flips to Enemy), and pure (projecting never
// touches the sim hash).
public class RivalWireTests
{
    private static (Simulation sim, ViewProjector projector) Make(int aiPlayers = 1)
    {
        var opts = new ServerOptions
        {
            MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = aiPlayers,
        };
        var build = WorldFactory.Build(opts);
        return (new Simulation(build.Spec, seed: 0xA117), new ViewProjector(build));
    }

    private static RelationshipDto? Rel(ViewDto v, int a, int b)
    {
        var (lo, hi) = a < b ? (a, b) : (b, a);
        return v.Relationships.FirstOrDefault(r => r.LoId == lo && r.HiId == hi);
    }

    // Diplomatic state is public knowledge — both players' views carry the same
    // factions / relationships / pending wars (no per-viewer diplomatic fog).
    [Fact]
    public void Diplomacy_IsPublic_BothViewersSeeIdenticalState()
    {
        var (sim, projector) = Make();
        sim.SubmitIntent(at: 10, new DeclareWarIntent(0, 1));
        sim.Run(until: 20);

        var v0 = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var v1 = projector.Project(sim, sim.Now, playerId: 1, reveal: false);

        Assert.Equal(
            v0.Factions.Select(f => f.Id).OrderBy(i => i),
            v1.Factions.Select(f => f.Id).OrderBy(i => i));
        // Both factions present (0 = human slot, 1 = AI), bandit faction absent.
        Assert.Contains(0, v0.Factions.Select(f => f.Id));
        Assert.Contains(1, v0.Factions.Select(f => f.Id));
        Assert.DoesNotContain(Sim.Core.Bandits.BanditConstants.OwnerId, v0.Factions.Select(f => f.Id));

        // The pending war is visible to BOTH the aggressor and the target.
        Assert.NotNull(Rel(v0, 0, 1));
        Assert.NotNull(Rel(v1, 0, 1));
        Assert.Single(v0.PendingWars);
        Assert.Single(v1.PendingWars);
        Assert.Equal(v0.PendingWars[0].EffectiveTick, v1.PendingWars[0].EffectiveTick);
    }

    // A declaration shows up as a pending war during the telegraph window, then
    // the wire reports Enemy once the WarBecomesEffectiveEvent fires.
    [Fact]
    public void DeclaredWar_ShowsPending_ThenFlipsToEnemy()
    {
        var (sim, projector) = Make();
        var delay = sim.World.Diplomacy.Config.Delay;
        sim.SubmitIntent(at: 10, new DeclareWarIntent(0, 1));

        // Mid-telegraph: Neutral state, but a pending war with a future effective tick.
        sim.Run(until: 10 + delay - 1);
        var pending = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        Assert.Equal((int)RelationshipState.Neutral, Rel(pending, 0, 1)!.State);
        Assert.Equal(10 + delay, Rel(pending, 0, 1)!.PendingEffectiveTick);
        Assert.Single(pending.PendingWars);
        Assert.Equal(10 + delay, pending.PendingWars[0].EffectiveTick);

        // Effective: Enemy, no pending war left.
        sim.Run(until: 10 + delay);
        var atWar = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        Assert.Equal((int)RelationshipState.Enemy, Rel(atWar, 0, 1)!.State);
        Assert.Equal(-1, Rel(atWar, 0, 1)!.PendingEffectiveTick);
        Assert.Empty(atWar.PendingWars);
    }

    // The whole projection — diplomacy included — is a pure read: projecting any
    // number of times never perturbs the sim hash (the §2.2 wall).
    [Fact]
    public void DiplomacyProjection_IsPureRead_NoMutation()
    {
        var (sim, projector) = Make();
        sim.SubmitIntent(at: 10, new DeclareWarIntent(0, 1));
        sim.Run(until: 30);

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            projector.Project(sim, sim.Now, playerId: 0, reveal: false);
            projector.Project(sim, sim.Now, playerId: 1, reveal: true);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
