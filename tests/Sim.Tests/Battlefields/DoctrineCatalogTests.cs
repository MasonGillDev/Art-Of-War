using Sim.Core.Battlefields;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;

namespace Sim.Tests.Battlefields;

// docs/battle-sandbox.md Phase 0 — the doctrine catalogue on the wire, and the
// reveal switch split into "the map" and "the other side's orders".
public class DoctrineCatalogTests
{
    private const int Blue = 0, Red = 1;

    [Fact]
    public void Genesis_CarriesTheDoctrineCatalogue_InOrder()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 1 });
        var world = new ViewProjector(build).BuildWorldDto();
        Assert.Equal(DoctrineCatalog.All.Select(d => (int)d.Behaviour), world.Doctrines.Select(d => d.Id));
        Assert.Equal(DoctrineCatalog.All.Select(d => d.Name), world.Doctrines.Select(d => d.Name));
        Assert.All(world.Doctrines, d => Assert.False(string.IsNullOrWhiteSpace(d.Description)));
        Assert.False(world.Doctrines.Single(d => d.Id == (int)DoctrineBehaviour.Withdraw).TakesWithdrawBelow);
    }

    [Fact]
    public void EveryDoctrineBehaviour_HasACatalogueEntry()
    {
        foreach (var b in Enum.GetValues<DoctrineBehaviour>())
            Assert.True(DoctrineCatalog.TryGet(b, out _), $"{b} is missing from DoctrineCatalog");
    }

    [Fact]
    public void SettingAnUnknownDoctrine_IsRejected_AndAKnownOneApplies()
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 11, Height = 11,
            FactionStarts = new[] { new FactionStartSpec { OwnerId = Blue, CastlePosition = new TileCoord(1, 1) } },
        });
        w.AddUnit(new Unit(1, new TileCoord(5, 5)) { Role = UnitRole.Soldier, OwnerId = Blue });
        var sim = new Simulation(w, seed: 1);
        var bad = new SetBattleDoctrineIntent(1, 200) { PlayerId = Blue }.Resolve(sim);
        Assert.False(bad.IsApplied);
        var good = new SetBattleDoctrineIntent(1, (byte)DoctrineBehaviour.Advance) { PlayerId = Blue }.Resolve(sim);
        Assert.True(good.IsApplied);
        Assert.Equal(DoctrineBehaviour.Advance, w.Units[1].Doctrine!.Value.Behaviour);
    }

    // A board with one Blue and one Red soldier, projected for Blue.
    private static (Simulation sim, ViewProjector projector) OpenBoard()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 1 });
        var sim = new Simulation(build.Spec, seed: 3);
        var world = sim.World;
        world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        var castle = world.Structures.Values.OfType<Castle>().First(c => c.OwnerId == Blue).At;
        // Two neighbouring open tiles near Blue's castle.
        var (field, beside) = Neighbours(world, castle);
        world.AddUnit(new Unit(9001, field) { Role = UnitRole.Soldier, OwnerId = Red });
        world.AddUnit(new Unit(9002, beside) { Role = UnitRole.Soldier, OwnerId = Blue });
        sim.SubmitIntent(sim.Now, new MoveIntent(9002, field) { PlayerId = Blue });
        sim.SubmitIntent(sim.Now, new SetBattleDoctrineIntent(9001, (byte)DoctrineBehaviour.Advance) { PlayerId = Red });
        sim.Run(until: sim.Now + 3 * Sim.Core.Time.Hour);
        Assert.NotEmpty(world.Battlefields);
        return (sim, new ViewProjector(build));
    }

    private static (TileCoord, TileCoord) Neighbours(GameWorld world, TileCoord near)
    {
        bool Open(TileCoord t) => world.Grid.InBounds(t) && !world.Structures.ContainsKey(t)
            && Biomes.MoveCost(world.Grid.BiomeAt(t)) < Biomes.Impassable;
        for (var r = 2; r < 12; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    var a = new TileCoord(near.X + dx, near.Y + dy);
                    var b = new TileCoord(a.X + 1, a.Y);
                    if (Open(a) && Open(b)) return (a, b);
                }
        throw new InvalidOperationException("no open pair near the castle");
    }

    [Fact]
    public void RevealingTheMap_ShowsTheBoard_ButNotTheOtherSidesDoctrine()
    {
        var (sim, projector) = OpenBoard();
        var board = Assert.Single(projector.ProjectV2(sim, sim.Now, Blue, reveal: true).Battlefields);
        var red = board.Units.Single(u => u.OwnerId == Red);
        Assert.False(red.Mine);
        Assert.Equal(0, red.Doctrine);            // blank: Advance is Red's secret
        Assert.Equal(0, red.OrderKind);
    }

    [Fact]
    public void RevealingOrders_ShowsTheOtherSidesDoctrine()
    {
        var (sim, projector) = OpenBoard();
        var board = Assert.Single(projector.ProjectV2(sim, sim.Now, Blue, reveal: true, revealOrders: true).Battlefields);
        var red = board.Units.Single(u => u.OwnerId == Red);
        Assert.Equal((int)DoctrineBehaviour.Advance, red.Doctrine);
    }
}
