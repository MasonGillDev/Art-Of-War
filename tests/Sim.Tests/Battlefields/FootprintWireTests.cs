using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Fortifications;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Wire;

namespace Sim.Tests.Battlefields;

// Footprints on the wire and in placement (docs/structure-footprints.md):
// every structure sends its facing and resolved layout; genesis sends each
// kind's pattern for the build preview; a build order carries a facing.
public class FootprintWireTests
{
    private static (Simulation sim, ViewProjector projector) MakeWorld()
    {
        var opts = new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 11);
        return (sim, new ViewProjector(build));
    }

    private static int Index(int x, int y) => y * 4 + x;

    [Fact]
    public void Genesis_CarriesEachDrawnKindsPattern_FacingNorth()
    {
        var (_, projector) = MakeWorld();
        var world = projector.BuildWorldDto();
        var house = world.Footprints.Single(f => f.Kind == (int)StructureKind.House).Footprint;
        foreach (var (x, y) in new[] { (1, 0), (2, 0), (2, 3) })
            Assert.Equal((int)SubtileKind.Blocked, house.Kinds[Index(x, y)]);
        Assert.Equal(16, house.Kinds.Length);
        var castle = world.Footprints.Single(f => f.Kind == (int)StructureKind.Castle).Footprint;
        Assert.Equal(11, castle.Kinds.Count(k => k == (int)SubtileKind.Wall));
        var camp = world.Footprints.Single(f => f.Kind == (int)StructureKind.BanditCamp).Footprint;
        Assert.Equal(Footprints.CampTowerReach, camp.Reach[Index(3, 3)]);
        Assert.Contains(world.Footprints, f => f.Kind == (int)StructureKind.Idol);
    }

    [Fact]
    public void TheView_CarriesEachStructuresFacingAndLayout()
    {
        var (sim, projector) = MakeWorld();
        var castle = sim.World.Structures.Values.OfType<Castle>().First(c => c.OwnerId == 0);
        var view = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        var dto = view.Structures.Single(s => s.X == castle.At.X && s.Y == castle.At.Y);
        Assert.Equal((int)castle.Facing, dto.Facing);
        Assert.True(dto.HasFootprint);
        Assert.Equal(11, dto.Footprint.Kinds.Count(k => k == (int)SubtileKind.Wall));
    }

    private static GameWorld GridWorld()
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60, Model: CombatModel.Grid),
            FactionStarts = new[] { new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(10, 10) } },
        });
        return w;
    }

    [Fact]
    public void ABuildOrdersFacing_IsKeptByTheSite_AndByTheFinishedBuilding()
    {
        var w = GridWorld();
        var sim = new Simulation(w, seed: 1);
        var at = new TileCoord(4, 4);
        Assert.True(new PlaceSiteIntent(at, StructureKind.House, facing: 1) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var site = (ConstructionSite)w.Structures[at];
        Assert.Equal(Heading.East, site.Facing);
        Construction.Complete(sim, site);
        Assert.Equal(Heading.East, w.Structures[at].Facing);
    }

    [Fact]
    public void ABuildOrderWithNoFacing_FacesNorth_AndAWallFacesAwayFromTheCastle()
    {
        var w = GridWorld();
        var sim = new Simulation(w, seed: 1);
        Assert.True(new PlaceSiteIntent(new TileCoord(4, 4), StructureKind.House) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(Heading.North, w.Structures[new TileCoord(4, 4)].Facing);

        var line = new List<TileCoord> { new(14, 9), new(14, 10), new(14, 11) };   // east of the castle
        Assert.True(new PlaceWallIntent(line) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.All(line, t => Assert.Equal(Heading.East, w.Structures[t].Facing));
        Assert.Equal(Heading.North, Footprints.DefaultFacing(w, StructureKind.Wall, 0, new TileCoord(10, 5)));
        Assert.Equal(Heading.West, Footprints.DefaultFacing(w, StructureKind.Tower, 0, new TileCoord(6, 11)));
    }

    [Fact]
    public void AFacingOutOfRange_IsRefused()
    {
        var sim = new Simulation(GridWorld(), seed: 1);
        Assert.True(new PlaceSiteIntent(new TileCoord(4, 4), StructureKind.House, facing: 4) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }
}
