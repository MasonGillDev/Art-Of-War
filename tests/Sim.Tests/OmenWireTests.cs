using System.Text.Json;
using Sim.Core.Engine;
using Sim.Core.Progression;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Wire;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M37 Phase D1 (docs/progression.md): omens reach their owner and nobody else;
// a rumour tells its bearing but never its tile; an ended omen reports its
// outcome for a while and then drops off; nothing about milestones or counters
// is on the wire; projecting never touches the world.
public class OmenWireTests
{
    private static (Simulation sim, ViewProjector projector) Match()
    {
        var build = WorldFactory.Build(new ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = true });
        return (new Simulation(build.Spec, seed: 0x3A1E), new ViewProjector(build));
    }

    private static readonly ProgressKey Soldiers = ProgressKey.Trained(UnitRole.Soldier);

    [Fact]
    public void ARaid_ReachesItsOwner_Only_WithItsCountdown()
    {
        var (sim, projector) = Match();
        var cfg = sim.World.ProgressionConfig;
        Progression.Bump(sim, 0, Soldiers, cfg.ReprisalTrained);
        var raid = sim.World.Omens.Values.Single(o => o.Kind == OmenKind.Raid);

        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var theirs = projector.Project(sim, sim.Now, playerId: 1, reveal: true);

        var row = Assert.Single(mine.Omens);
        Assert.Equal((int)OmenKind.Raid, row.Kind);
        Assert.Equal((int)OmenState.Pending, row.State);
        Assert.Equal((int)raid.From, row.From);
        Assert.Equal(raid.Size, row.Size);
        Assert.Equal(raid.DueTick, row.DueTick);
        Assert.Equal(raid.DueTick - sim.Now, row.TicksLeft);
        Assert.Equal((raid.Target.X, raid.Target.Y), (row.TargetX, row.TargetY));
        Assert.Empty(theirs.Omens);
    }

    [Fact]
    public void ARumour_TellsItsBearing_AndASearchArea_NeverItsTile()
    {
        var (sim, projector) = Match();
        sim.World.RestoreProgressionConfig(sim.World.ProgressionConfig with { FarHorizonsTiles = 1 });
        Progression.Check(sim, 0);
        var rumour = sim.World.Omens.Values.Single(o => o.Kind == OmenKind.Rumour);

        var row = Assert.Single(projector.Project(sim, sim.Now, playerId: 0, reveal: false).Omens);
        Assert.Equal((int)rumour.From, row.From);
        Assert.Equal((-1, -1), (row.TargetX, row.TargetY));

        // The circle holds the ruin, and its centre is not the ruin.
        Assert.Equal(sim.World.ProgressionConfig.RumourAreaRadius, row.AreaRadius);
        var dx = rumour.Target.X - row.AreaX;
        var dy = rumour.Target.Y - row.AreaY;
        Assert.True(dx * dx + dy * dy <= row.AreaRadius * row.AreaRadius, "the ruin must lie inside the circle");
        Assert.NotEqual((rumour.Target.X, rumour.Target.Y), (row.AreaX, row.AreaY));
    }

    [Fact]
    public void AnEndedOmen_ReportsItsOutcome_ThenDropsOff()
    {
        var (sim, projector) = Match();
        var cfg = sim.World.ProgressionConfig;
        Progression.Bump(sim, 0, Soldiers, cfg.ReprisalTrained);
        var raid = sim.World.Omens.Values.Single(o => o.Kind == OmenKind.Raid);
        sim.Run(until: raid.DueTick);
        foreach (var id in raid.PartyIds.ToList())
            Sim.Core.Combat.CombatRules.OnUnitDeath(sim, sim.World.Units[id]);

        var just = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        Assert.Contains(just.Omens, o => o.Id == raid.OmenId && o.State == (int)OmenState.Repelled);

        var later = projector.Project(sim, sim.Now + OmenDto.RecentTicks + 1, playerId: 0, reveal: false);
        Assert.DoesNotContain(later.Omens, o => o.Id == raid.OmenId);
    }

    [Fact]
    public void NoMilestoneOrCounter_EverReachesTheWire()
    {
        var (sim, projector) = Match();
        Progression.Bump(sim, 0, Soldiers, sim.World.ProgressionConfig.ReprisalTrained);

        // (BuildProgress, a construction site's percent, is the one legitimate
        // "progress" on the wire.)
        var json = JsonSerializer.Serialize(projector.Project(sim, sim.Now, playerId: 0, reveal: false))
            .Replace("\"BuildProgress\"", "");
        Assert.DoesNotContain("milestone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("progress", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reprisal", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectingOmens_IsAPureRead()
    {
        var (sim, projector) = Match();
        Progression.Bump(sim, 0, Soldiers, sim.World.ProgressionConfig.ReprisalTrained);

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++) projector.Project(sim, sim.Now + i, playerId: 0, reveal: false);
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
