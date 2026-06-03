using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M3 Phase D: BuildPlayerView correctly tiers visible/explored/unexplored
// and the always-see-own-entities rule holds.
public class PlayerViewTests
{
    private static GameWorld TwoPlayerWorld()
    {
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
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
                new FactionStartSpec
                {
                    OwnerId = 1,
                    CastlePosition = new TileCoord(25, 25),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(99, new TileCoord(25, 25), UnitRole.Builder, OwnerId: 1),
                    },
                },
            },
        };
        return Genesis.Build(spec);
    }

    [Fact]
    public void UnexploredTile_AbsentFromAllSets()
    {
        var world = TwoPlayerWorld();
        var view = View.BuildPlayerView(world, playerId: 0);
        var farAway = new TileCoord(28, 28); // never seen by player 0
        Assert.DoesNotContain(farAway, view.Visible);
        Assert.DoesNotContain(farAway, view.Explored);
        Assert.False(view.RememberedTerrain.ContainsKey(farAway));
    }

    [Fact]
    public void ExploredNotVisible_HasRememberedTerrain_NoOtherActivity()
    {
        // Walk player 0's builder away from the castle, then back. Tiles
        // along the walk became explored, but the unit's no longer there
        // — so they're no longer currently visible.
        var world = TwoPlayerWorld();
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(15, 5)));
        sim.Run();
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(5, 5)));
        sim.Run();

        var view = View.BuildPlayerView(world, 0);
        var walked = new TileCoord(12, 5); // along the path
        Assert.Contains(walked, view.Explored);
        Assert.DoesNotContain(walked, view.Visible);
        Assert.True(view.RememberedTerrain.ContainsKey(walked));
        Assert.Equal(world.Grid.BiomeAt(walked), view.RememberedTerrain[walked]);
    }

    [Fact]
    public void Visible_HasNoRememberedTerrainEntry()
    {
        var world = TwoPlayerWorld();
        var view = View.BuildPlayerView(world, 0);
        var atCastle = new TileCoord(5, 5);
        Assert.Contains(atCastle, view.Visible);
        Assert.Contains(atCastle, view.Explored);
        // Currently-visible tiles get current state via raw world access,
        // not via RememberedTerrain.
        Assert.False(view.RememberedTerrain.ContainsKey(atCastle));
    }

    [Fact]
    public void OwnEntities_AlwaysVisible_EvenOutsideCurrentVision()
    {
        // Move player 0's builder out beyond ANY of player 0's vision sources
        // (no scouts, no towers, just the castle at radius 5). The unit at
        // (25, 25) is far outside the castle's disc, but is owned by player 0
        // — must still show in their view.
        var world = TwoPlayerWorld();
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(20, 5)));
        sim.Run();

        var view = View.BuildPlayerView(world, 0);
        // Tile (20,5) is outside the castle's radius (>5 tiles away) AND
        // outside the unit's own contribution since the unit's *radius*
        // already covers it. So unit IS in visible AND in visibleUnits.
        Assert.Contains(view.VisibleUnits, u => u.Id == 1);

        // Now move them somewhere where ONLY their own radius contributes.
        // (And nothing else owned). Their own contribution still keeps them
        // visible from the player's perspective.
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(29, 0)));
        sim.Run();
        view = View.BuildPlayerView(world, 0);
        Assert.Contains(view.VisibleUnits, u => u.Id == 1 && u.Position == new TileCoord(29, 0));
    }

    [Fact]
    public void OtherPlayerEntities_HiddenWhenNotInVisible()
    {
        // Player 1's castle + unit are at (25,25) — outside player 0's
        // vision sources. Player 0's view must NOT include them.
        var world = TwoPlayerWorld();
        var view = View.BuildPlayerView(world, 0);
        Assert.DoesNotContain(view.VisibleStructures, s => s.OwnerId == 1);
        Assert.DoesNotContain(view.VisibleUnits, u => u.OwnerId == 1);
    }

    [Fact]
    public void OtherPlayerEntity_BecomesVisible_OnceWalkedToward()
    {
        // Walk player 0's builder to within sight of player 1's castle.
        var world = TwoPlayerWorld();
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(24, 25)));
        sim.Run();

        var view = View.BuildPlayerView(world, 0);
        // Player 1's castle at (25,25) is 1 tile from the builder's
        // position — within the builder's radius (3) → visible.
        Assert.Contains(view.VisibleStructures,
            s => s.OwnerId == 1 && s.At == new TileCoord(25, 25));
        Assert.Contains(view.VisibleUnits, u => u.OwnerId == 1 && u.Id == 99);
    }

    [Fact]
    public void EmptyPlayer_SeesNothing()
    {
        // Player 1 has entities but a player who owns nothing should get
        // an empty view.
        var world = TwoPlayerWorld();
        world.Players[2] = new Player(2); // player 2 owns nothing
        var view = View.BuildPlayerView(world, 2);
        Assert.Empty(view.Visible);
        Assert.Empty(view.Explored);
        Assert.Empty(view.RememberedTerrain);
        Assert.Empty(view.VisibleUnits);
        Assert.Empty(view.VisibleStructures);
    }
}
