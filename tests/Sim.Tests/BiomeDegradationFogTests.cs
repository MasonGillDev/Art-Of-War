using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M9 Phase E — fog × biome integration. The remembered-terrain map
// (M3) now records LAST-SEEN biome rather than worldgen biome. A tile that
// degrades behind the fog still shows its last-seen value until a re-scout
// refreshes it.
public class BiomeDegradationFogTests
{
    private static readonly BiomeDegradationConfig Cfg = new();

    [Fact]
    public void Reveal_RecordsLastSeenBiome_AtCallTime()
    {
        // A Forest tile, no degradation. Reveal at t=0 → remembered = Forest.
        // Pre-place a Fertility entry that pushes the tile to Grassland. New
        // reveal at later tick → remembered updates to Grassland.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var tile = new TileCoord(4, 4);

        Sight.Reveal(world, 0, tile, r: 0 + 1, now: 0);
        Assert.Equal(Biome.Forest, world.RememberedBiome[0][tile]);

        // Force fertility deviation into the Grassland band.
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);

        Sight.Reveal(world, 0, tile, r: 1, now: 100);
        Assert.Equal(Biome.Grassland, world.RememberedBiome[0][tile]);
    }

    [Fact]
    public void RememberedBiome_Stale_BehindFog()
    {
        // Scout Forest, leave, degrade it (no further reveal), the player
        // view still shows Forest.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 1);
        var tile = new TileCoord(4, 4);

        // Player 0 has no vision sources by default. Hand-call Reveal at t=0
        // to simulate a scout passing through (radius 1).
        Sight.Reveal(world, 0, tile, r: 1, now: 0);
        Assert.Equal(Biome.Forest, world.RememberedBiome[0][tile]);

        // Now degrade the tile behind the player's back (no further reveal).
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, sim.Now, Cfg));

        // Player view: the tile is in Explored, NOT in Visible (no current
        // vision source) → it appears in RememberedTerrain showing the last-
        // seen value (Forest), NOT the current value (Grassland).
        var view = View.BuildPlayerView(world, 0, sim.Now);
        Assert.DoesNotContain(tile, view.Visible);
        Assert.Contains(tile, view.Explored);
        Assert.Equal(Biome.Forest, view.RememberedTerrain[tile]);
    }

    [Fact]
    public void CurrentlyVisible_ShowsCurrentBiome_NotRememberedBiome()
    {
        // A tile currently in vision must NOT appear in RememberedTerrain
        // (the view tier rule: visible tiles fall through to live world data,
        // not the cached remembered value).
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 1);
        var tile = new TileCoord(4, 4);

        // Place a Castle owned by player 0 so the tile is in vision.
        var castle = new Castle(tile) { OwnerId = 0 };
        world.AddStructure(castle);
        Sight.Reveal(world, 0, tile, Sight.RadiusFor(StructureKind.Castle), now: 0);

        // Degrade — would normally shift the player's view if we used last-seen.
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);

        var view = View.BuildPlayerView(world, 0, sim.Now);
        // Tile is currently visible → BiomeAt shows Grassland, but the view
        // doesn't put it in RememberedTerrain (visible tiles fall through to
        // live data).
        Assert.Contains(tile, view.Visible);
        Assert.DoesNotContain(tile, (System.Collections.Generic.IDictionary<TileCoord, Biome>)view.RememberedTerrain);
    }

    [Fact]
    public void ReScout_RefreshesRememberedBiome_ToCurrent()
    {
        // Scout, leave, degrade, scout AGAIN → remembered biome refreshes
        // from Forest to Grassland.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var tile = new TileCoord(4, 4);

        Sight.Reveal(world, 0, tile, r: 1, now: 0);
        Assert.Equal(Biome.Forest, world.RememberedBiome[0][tile]);

        // Degrade behind the fog (no reveal in between).
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);

        // Re-scout at t=200: reveal updates remembered to current biome.
        Sight.Reveal(world, 0, tile, r: 1, now: 200);
        Assert.Equal(Biome.Grassland, world.RememberedBiome[0][tile]);
    }

    [Fact]
    public void View_DoesNotLeak_CurrentBiome_OnNonVisibleTiles()
    {
        // After degrading a tile behind the fog, the player view's
        // RememberedTerrain must NOT contain the current biome — only the
        // last-seen one. This is the privacy contract: no info-leak through
        // remembered terrain.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 1);
        var tile = new TileCoord(4, 4);

        Sight.Reveal(world, 0, tile, r: 1, now: 0);
        world.Fertility[tile] = new Fertility(deviation: -90, lastUpdateTick: 0);  // Desert
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, sim.Now, Cfg));

        var view = View.BuildPlayerView(world, 0, sim.Now);
        // Anti-leak: view shows last-seen (Forest), NOT current (Desert).
        Assert.Equal(Biome.Forest, view.RememberedTerrain[tile]);
        Assert.NotEqual(Biome.Desert, view.RememberedTerrain[tile]);
    }

    [Fact]
    public void View_IsPureRead_AfterDegradation()
    {
        // The view-pure-read invariant from M3 still holds with the M9
        // additions: BuildPlayerView called many times in a row must not
        // change the sim hash.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 1);
        var tile = new TileCoord(4, 4);

        Sight.Reveal(world, 0, tile, r: 1, now: 0);
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);

        var hashBefore = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++) View.BuildPlayerView(world, 0, sim.Now);
        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void RememberedBiome_SnapshotRoundTrips()
    {
        // The new per-player per-tile remembered-biome map must round-trip
        // through Snapshot. Otherwise post-recovery views would lose their
        // stale-memory and reveal the current biome incorrectly.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        Sight.Reveal(world, 0, new TileCoord(4, 4), r: 1, now: 0);
        Sight.Reveal(world, 1, new TileCoord(2, 2), r: 1, now: 0);
        // Mutate one tile's remembered biome (simulating a scout that saw it
        // pre-degradation, then degradation behind the fog).
        world.RememberedBiome[0][new TileCoord(4, 4)] = Biome.Grassland;

        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);
        var bytes = Snapshot.Serialize(sim);
        var sim2 = Snapshot.Restore(bytes, seed: 1);

        Assert.Equal(hashBefore, Snapshot.Hash(sim2));
        Assert.Equal(Biome.Grassland, sim2.World.RememberedBiome[0][new TileCoord(4, 4)]);
        Assert.Equal(Biome.Forest, sim2.World.RememberedBiome[1][new TileCoord(2, 2)]);
    }
}
