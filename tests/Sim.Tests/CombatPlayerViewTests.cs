using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M7 Phase F: PlayerView surfaces combat within fog.
//   * Own unit Health is visible.
//   * Combat on a tile inside the viewer's Visible set surfaces in
//     OngoingCombats.
//   * Combat on an explored-but-not-visible tile does NOT surface.
public class CombatPlayerViewTests
{
    [Fact]
    public void OwnUnitHealth_VisibleInOwnView()
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(5, 5),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(5, 5), UnitRole.Builder),
                    },
                },
            },
        };
        var world = Genesis.Build(spec);
        world.Units[1].Health = 7;
        var view = View.BuildPlayerView(world, playerId: 0);
        var u = view.VisibleUnits.First(v => v.Id == 1);
        Assert.Equal(7, u.Health);
    }

    [Fact]
    public void Combat_OnVisibleTile_SurfacesInOngoingCombats()
    {
        var tile = new TileCoord(5, 5); // inside faction 0's castle vision (radius 5).
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(5, 5) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(25, 25) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);

        // Hand-place enemies on a tile in faction 0's vision.
        world.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        world.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        var sim = new Simulation(world, seed: 0xF06);
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);

        var view = View.BuildPlayerView(world, playerId: 0);
        Assert.Single(view.OngoingCombats);
        Assert.Equal(tile, view.OngoingCombats[0].Tile);
    }

    [Fact]
    public void Combat_OnFoggedTile_HiddenFromView()
    {
        // Faction 0 has no eyes on (25, 25) — combat there doesn't show.
        var spec = new GenesisSpec
        {
            Width = 40, Height = 40,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(20, 20) },
                new FactionStartSpec { OwnerId = 2, CastlePosition = new TileCoord(35, 35) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(1, 2), RelationshipState.Enemy);

        var farTile = new TileCoord(30, 30); // out of faction 0's range.
        world.AddUnit(new Unit(100, farTile) { Role = UnitRole.Builder, OwnerId = 1 });
        world.AddUnit(new Unit(200, farTile) { Role = UnitRole.Builder, OwnerId = 2 });
        var sim = new Simulation(world, seed: 0xF06);
        CombatTrigger.MaybeBeginCombatOnTile(sim, farTile);

        var view0 = View.BuildPlayerView(world, playerId: 0);
        Assert.Empty(view0.OngoingCombats);

        // Belligerents see it.
        var view1 = View.BuildPlayerView(world, playerId: 1);
        var view2 = View.BuildPlayerView(world, playerId: 2);
        Assert.NotEmpty(view1.OngoingCombats);
        Assert.NotEmpty(view2.OngoingCombats);
    }
}
