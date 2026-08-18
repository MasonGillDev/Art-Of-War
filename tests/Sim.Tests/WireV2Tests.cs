using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Vision;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Wire;

namespace Sim.Tests;

// The v2 wire (Sim.Server/Wire/WireV2.cs) — the contract the production client
// speaks. v1 re-sends every known tile's {x,y,biome,elevation} on every poll; v2
// splits that into a static genesis payload fetched once plus a per-tick view
// carrying only what a tick can change (fog as RLE runs + biome drift overrides).
//
// The load-bearing property is EQUIVALENCE: genesis + fog runs + overrides must
// reconstruct exactly the tile picture v1 spells out longhand. If that ever breaks,
// the production client renders a world the sim did not describe.
public class WireV2Tests
{
    private static (Simulation sim, ViewProjector projector, WorldBuild build) MakeWorld(int size = 64)
    {
        var opts = new ServerOptions { MapWidth = size, MapHeight = size, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        return (sim, new ViewProjector(build), build);
    }

    // Expand the parallel run arrays back into a per-tile state grid, the way the
    // client's KnownWorld does.
    private static int[] ExpandFog(ViewV2Dto v, int w, int h)
    {
        Assert.Equal(v.FogRunState.Length, v.FogRunLength.Length);
        var fog = new int[w * h];
        var i = 0;
        for (var r = 0; r < v.FogRunState.Length; r++)
            for (var e = i + v.FogRunLength[r]; i < e; i++)
                fog[i] = v.FogRunState[r];
        Assert.Equal(w * h, i);   // runs must cover the grid exactly, no more, no less
        return fog;
    }

    // Genesis biome grid overlaid with the view's drift overrides = what the player
    // believes each tile is. -1 where the player knows nothing.
    private static int[] BelievedBiome(WorldDto world, ViewV2Dto v, int[] fog)
    {
        var biome = new int[world.Biome.Length];
        for (var t = 0; t < biome.Length; t++)
            biome[t] = fog[t] == FogState.Unknown ? -1 : world.Biome[t];
        foreach (var o in v.BiomeOverrides)
            biome[o.Y * world.Width + o.X] = o.Biome;
        return biome;
    }

    [Fact]
    public void World_MatchesTheGeneratedMap()
    {
        var (_, projector, build) = MakeWorld();
        var w = projector.BuildWorldDto();

        Assert.Equal(2, w.WireVersion);
        Assert.Equal(build.Map.Width, w.Width);
        Assert.Equal(build.Map.Height, w.Height);
        Assert.Equal(build.Map.Seed, w.MapSeed);
        Assert.Equal(Sim.Core.Time.Day, w.TicksPerDay);
        Assert.Equal(w.Width * w.Height, w.Elevation.Length);
        Assert.Equal(w.Width * w.Height, w.Biome.Length);

        // Row-major layout is the contract the client indexes with (y * width + x).
        for (var y = 0; y < w.Height; y++)
            for (var x = 0; x < w.Width; x++)
            {
                Assert.Equal(build.Elevation[x, y], w.Elevation[y * w.Width + x]);
                Assert.Equal((int)build.Map.Grid[x, y], w.Biome[y * w.Width + x]);
            }
    }

    [Fact]
    public void FogRunsAndOverrides_ReconstructExactlyWhatV1Describes()
    {
        var (sim, projector, _) = MakeWorld();
        sim.Run(Sim.Core.Time.Day);   // let the world move: units walk, vision shifts

        var now = sim.Now;
        var v1 = projector.Project(sim, now, playerId: 0, reveal: false);
        var v2 = projector.ProjectV2(sim, now, playerId: 0, reveal: false);

        var world = projector.BuildWorldDto();
        var fog = ExpandFog(v2, world.Width, world.Height);
        var biome = BelievedBiome(world, v2, fog);

        var v1Live = v1.Visible.ToDictionary(t => (t.X, t.Y), t => (t.Biome, t.Elevation));
        var v1Rem = v1.Remembered.ToDictionary(t => (t.X, t.Y), t => (t.Biome, t.Elevation));

        // The player must know something, or this test proves nothing.
        Assert.NotEmpty(v1Live);

        for (var y = 0; y < world.Height; y++)
            for (var x = 0; x < world.Width; x++)
            {
                var i = y * world.Width + x;
                switch (fog[i])
                {
                    case FogState.Live:
                        Assert.True(v1Live.ContainsKey((x, y)), $"({x},{y}) v2 Live, v1 does not see it");
                        Assert.Equal(v1Live[(x, y)].Biome, biome[i]);
                        Assert.Equal(v1Live[(x, y)].Elevation, world.Elevation[i]);
                        break;
                    case FogState.Remembered:
                        Assert.True(v1Rem.ContainsKey((x, y)), $"({x},{y}) v2 Remembered, v1 does not");
                        Assert.Equal(v1Rem[(x, y)].Biome, biome[i]);
                        Assert.Equal(v1Rem[(x, y)].Elevation, world.Elevation[i]);
                        break;
                    default:
                        Assert.False(v1Live.ContainsKey((x, y)), $"({x},{y}) v2 Unknown but v1 visible");
                        Assert.False(v1Rem.ContainsKey((x, y)), $"({x},{y}) v2 Unknown but v1 remembered");
                        break;
                }
            }

        // ...and nothing v1 knows about is missing from v2.
        foreach (var (x, y) in v1Live.Keys)
            Assert.Equal(FogState.Live, fog[y * world.Width + x]);
        foreach (var (x, y) in v1Rem.Keys)
            Assert.Equal(FogState.Remembered, fog[y * world.Width + x]);
    }

    [Fact]
    public void Reveal_IsOneLiveRunCoveringTheWholeMap()
    {
        var (sim, projector, _) = MakeWorld();
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);

        Assert.Single(v2.FogRunState);
        Assert.Equal(FogState.Live, v2.FogRunState[0]);
        Assert.Equal(64 * 64, v2.FogRunLength[0]);
    }

    [Fact]
    public void BiomeOverride_EmittedWhenABelievedTileDriftsFromGenesis()
    {
        var (sim, projector, build) = MakeWorld();
        var world = sim.World;
        var cfg = world.BiomeDegradationConfig;
        var now = sim.Now;

        // Find a VISIBLE tile that sits on the degradation ladder — only ladder
        // biomes can drift, so a Water/Mountain tile would prove nothing.
        var visible = View.BuildPlayerView(world, 0, now).Visible;
        TileCoord? target = null;
        foreach (var t in visible)
        {
            var genesis = build.Map.Grid[t.X, t.Y];
            if (genesis is Biome.Forest or Biome.Grassland) { target = t; break; }
        }
        Assert.True(target.HasValue, "no visible ladder tile to degrade — fixture assumption broke");
        var tile = target.Value;
        var genesisBiome = (int)build.Map.Grid[tile.X, tile.Y];

        // Baseline: no drift anywhere yet, so no overrides at all.
        Assert.Empty(projector.ProjectV2(sim, now, 0, reveal: false).BiomeOverrides);

        // Drive the tile's fertility far below the desert threshold. Config-derived,
        // not a magic number: the fertility space has been rescaled before and will be
        // again. LastUpdateTick = now so no catch-up drift is applied on read.
        world.Fertility[tile] = new Fertility(
            deviation: cfg.DesertThreshold - cfg.ForestBaseline - cfg.GrasslandBaseline,
            lastUpdateTick: now);

        var drifted = (int)BiomeDegradation.BiomeAt(world, tile, now, cfg);
        Assert.NotEqual(genesisBiome, drifted);   // the fixture actually moved it

        var v2 = projector.ProjectV2(sim, now, 0, reveal: false);
        var o = Assert.Single(v2.BiomeOverrides);
        Assert.Equal(tile.X, o.X);
        Assert.Equal(tile.Y, o.Y);
        Assert.Equal(drifted, o.Biome);

        // Genesis is untouched — drift lives on the view, never on the static payload.
        Assert.Equal(genesisBiome, projector.BuildWorldDto().Biome[tile.Y * world.Grid.Width + tile.X]);
    }

    [Fact]
    public void UnitHealth_IsOwnOnly()
    {
        var (sim, projector, _) = MakeWorld();
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);

        Assert.NotEmpty(v2.Units);
        foreach (var u in v2.Units)
        {
            if (u.OwnerId == 0) Assert.True(u.Health > 0, $"own unit {u.Id} has no health on the wire");
            else Assert.Equal(-1, u.Health);   // enemy health is private, same rule as Power
        }
    }
}
