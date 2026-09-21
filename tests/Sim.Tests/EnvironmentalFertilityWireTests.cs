using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Wire;

namespace Sim.Tests;

// M35 Phase E — the environmental baseline on the wire and in the brain
// (docs/environmental-fertility.md decision 5):
//   * WorldDto.Baseline is the sim's own per-tile baseline at genesis;
//   * v1 TileDto.Baseline (what the brains read) is the live baseline;
//   * StructDto.ClaimBaseline is own-only and parallel to ClaimFertility;
//   * ViewV2Dto.BaselineOverrides carries only tiles a canal has moved;
//   * ThinkContext prefers the richer pocket, and at equal baselines picks
//     exactly the nearest one it always did.
public class EnvironmentalFertilityWireTests
{
    private static readonly BiomeDegradationConfig Graded = new BiomeDegradationConfig() with
    {
        WaterFertilityBonus = 1500, DryEdgePenalty = 300, ForestDepthBonusPerRing = 400,
    };

    private static (Simulation sim, ViewProjector projector) MakeWorld(BiomeDegradationConfig? cfg = null, int size = 64)
    {
        var opts = new ServerOptions { MapWidth = size, MapHeight = size, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var spec = cfg is { } c ? build.Spec with { BiomeDegradation = c } : build.Spec;
        var sim = new Simulation(spec, seed: 0xA117);
        return (sim, new ViewProjector(build));
    }

    [Fact]
    public void Genesis_Baseline_IsTheSimsOwnPerTileBaseline()
    {
        var (sim, projector) = MakeWorld(Graded);
        var cfg = sim.World.BiomeDegradationConfig;
        var world = projector.BuildWorldDto(sim.World.PopulationConfig, sim.World.RoyaltyConfig, cfg);

        Assert.Equal(world.Width * world.Height, world.Baseline.Length);
        var differs = 0;
        for (var y = 0; y < world.Height; y++)
            for (var x = 0; x < world.Width; x++)
            {
                var t = new TileCoord(x, y);
                var expected = BiomeDegradation.BaselineFertility(sim.World, t, cfg);
                Assert.Equal(expected, world.Baseline[y * world.Width + x]);
                if (expected != BiomeDegradation.BaselineFertility(sim.World.Grid.BiomeAt(t), cfg)) differs++;
            }
        Assert.True(differs > 0, "a graded config must move some tile off its band baseline");

        // The band baselines ride the fertility rules block so the client can grade
        // a claim against the band its structure works.
        Assert.Equal(cfg.GrasslandBaseline, world.Fertility.GrasslandBaseline);
        Assert.Equal(cfg.ForestBaseline, world.Fertility.ForestBaseline);
        Assert.True(world.Fertility.GrasslandBaseline > world.Fertility.DesertThreshold);
        Assert.True(world.Fertility.ForestBaseline > world.Fertility.ForestThreshold);

        // The parameterless overload ships the defaults' (flat) baselines, not zeros.
        var flat = projector.BuildWorldDto();
        Assert.Equal(world.Baseline.Length, flat.Baseline.Length);
        Assert.Contains(flat.Baseline, b => b > 0);
    }

    [Fact]
    public void V1Tiles_CarryTheLiveBaseline()
    {
        var (sim, projector) = MakeWorld(Graded);
        var cfg = sim.World.BiomeDegradationConfig;
        var view = projector.Project(sim, sim.Now, playerId: 0, reveal: true);
        Assert.NotEmpty(view.Visible);
        foreach (var t in view.Visible)
            Assert.Equal(BiomeDegradation.BaselineFertility(sim.World, new TileCoord(t.X, t.Y), cfg), t.Baseline);

        var fogged = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        foreach (var t in fogged.Visible.Concat(fogged.Remembered))
            Assert.Equal(BiomeDegradation.BaselineFertility(sim.World, new TileCoord(t.X, t.Y), cfg), t.Baseline);
    }

    [Fact]
    public void V2_BaselineOverrides_EmptyAtGenesis_ThenOnlyTheLiftedTiles()
    {
        var (sim, projector) = MakeWorld(Graded);
        var cfg = sim.World.BiomeDegradationConfig;
        var v = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        Assert.Empty(v.BaselineOverrides);

        // Flood a dry land tile by hand (what a canal completion does to the
        // grid) and re-project: exactly the tiles whose baseline moved are
        // listed, with the live value.
        TileCoord? dry = null;
        for (var y = 4; y < sim.World.Grid.Height - 4 && dry is null; y++)
            for (var x = 4; x < sim.World.Grid.Width - 4; x++)
            {
                var t = new TileCoord(x, y);
                if (sim.World.Grid.BiomeAt(t) == Biome.Grassland
                    && WaterProximity.DistanceToWater(sim.World, t, cfg.WaterFertilityRadius + 2) > cfg.WaterFertilityRadius + 2)
                { dry = t; break; }
            }
        Assert.NotNull(dry);
        var genesis = projector.BuildWorldDto(sim.World.PopulationConfig, sim.World.RoyaltyConfig, cfg);
        sim.World.Grid.SetBiome(dry.Value, Biome.Water);

        var after = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        Assert.NotEmpty(after.BaselineOverrides);
        var listed = new HashSet<(int, int)>();
        foreach (var o in after.BaselineOverrides)
        {
            var t = new TileCoord(o.X, o.Y);
            Assert.Equal(BiomeDegradation.BaselineFertility(sim.World, t, cfg), o.Baseline);
            Assert.NotEqual(genesis.Baseline[o.Y * genesis.Width + o.X], o.Baseline);
            listed.Add((o.X, o.Y));
        }
        for (var y = 0; y < genesis.Height; y++)
            for (var x = 0; x < genesis.Width; x++)
            {
                var live = BiomeDegradation.BaselineFertility(sim.World, new TileCoord(x, y), cfg);
                Assert.Equal(live != genesis.Baseline[y * genesis.Width + x], listed.Contains((x, y)));
            }
    }

    [Fact]
    public void ClaimBaseline_IsOwnOnly_AndParallelToClaimFertility()
    {
        var (sim, projector) = MakeWorld(Graded);
        var cfg = sim.World.BiomeDegradationConfig;
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        // First grassland tile that can host a full farm claim.
        TileCoord? site = null;
        for (var y = 3; y < sim.World.Grid.Height - 3 && site is null; y++)
            for (var x = 3; x < sim.World.Grid.Width - 3; x++)
            {
                var t = new TileCoord(x, y);
                if (sim.World.Grid.BiomeAt(t) != Biome.Grassland || sim.World.Structures.ContainsKey(t)) continue;
                if (Claims.AutoSelect(sim.World, t, spec, sim.Now) is not null) { site = t; break; }
            }
        Assert.NotNull(site);
        var farm = (Extractor)sim.World.AddStructure(new Extractor(StructureKind.Farm, site.Value) { OwnerId = 0 });
        var u = sim.World.AddUnit(new Unit(9001, farm.At) { Role = UnitRole.Farmer, OwnerId = 0 });
        farm.Workers.Add(u.Id);
        farm.ArmIfDormant(sim);
        Assert.Equal(spec.ClaimCount, farm.ClaimTiles.Count);

        var own = projector.Project(sim, sim.Now, playerId: 0, reveal: true)
            .Structures.Single(s => s.X == farm.At.X && s.Y == farm.At.Y);
        Assert.Equal(own.ClaimFertility.Length, own.ClaimBaseline.Length);
        Assert.Equal(own.ClaimX.Length, own.ClaimBaseline.Length);
        for (var i = 0; i < own.ClaimBaseline.Length; i++)
            Assert.Equal(
                BiomeDegradation.BaselineFertility(sim.World, new TileCoord(own.ClaimX[i], own.ClaimY[i]), cfg),
                own.ClaimBaseline[i]);

        var foreign = projector.Project(sim, sim.Now, playerId: 1, reveal: true)
            .Structures.Single(s => s.X == farm.At.X && s.Y == farm.At.Y);
        Assert.Empty(foreign.ClaimFertility);
        Assert.Empty(foreign.ClaimBaseline);
    }

    // ---- the brain ----

    private static ViewDto GrasslandView(int size, Func<int, int, int> baselineAt)
    {
        var tiles = new List<TileDto>();
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                tiles.Add(new TileDto { X = x, Y = y, Biome = (int)Biome.Grassland, Baseline = baselineAt(x, y) });
        var c = size / 2;
        return new ViewDto
        {
            PlayerId = 1, Width = size, Height = size,
            Visible = tiles.ToArray(),
            Structures = new[]
            {
                new StructDto { Kind = (int)StructureKind.Castle, OwnerId = 1, X = c, Y = c },
            },
        };
    }

    [Fact]
    public void Brain_EqualBaselines_PicksTheNearestPocket_AsBefore()
    {
        var view = GrasslandView(24, (_, _) => 5000);
        var ctx = ThinkContext.Build(view, new AiConfig(), new AiMemory(), now: 0);
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        var pick = ctx.NearestPocketTile(Biome.Grassland, 10, spec.ClaimCount, spec.ClaimRange);
        // Ring 1, first in (y, x) scan order: the tile diagonally up-left of the keep.
        Assert.Equal(new TileCoord(11, 11), pick);
    }

    [Fact]
    public void Brain_PrefersTheRicherPocket_OverTheNearer()
    {
        // A "river valley" of rich land far to the east of the keep.
        var view = GrasslandView(24, (x, _) => x >= 19 ? 6500 : 5000);
        var ctx = ThinkContext.Build(view, new AiConfig(), new AiMemory(), now: 0);
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        var pick = ctx.NearestPocketTile(Biome.Grassland, 10, spec.ClaimCount, spec.ClaimRange);
        Assert.NotNull(pick);
        Assert.True(pick.Value.X >= 19, $"expected a valley site, got {pick.Value.X},{pick.Value.Y}");
    }
}
