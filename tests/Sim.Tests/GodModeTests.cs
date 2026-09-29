using Sim.Core.Canals;
using Sim.Core.Engine;
using Sim.Core.Fortifications;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// God mode (docs/god-mode.md) — a flagged player's placements complete on the
// spot through Construction.Complete, the ONE completion path shared with
// BuildCompleteEvent. Pins:
//   * ordinary players still get a pending site; god players get the structure;
//   * validation is unchanged (a god cannot build where nobody can);
//   * walls stand per segment, canals flood on placement;
//   * the flag survives a snapshot round-trip and the twin-run hash holds.
public class GodModeTests
{
    private static Simulation MakeSim(int size = 8)
    {
        var world = new GameWorld(new TileGrid(size, size, Biome.Grassland));
        world.Players[0] = new Player(0) { GodMode = true };
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 1);
    }

    [Fact]
    public void GodPlayer_PlaceSite_StandsInstantly_NoSiteNoCost()
    {
        var sim = MakeSim();
        var at = new TileCoord(3, 3);

        var outcome = new PlaceSiteIntent(at, StructureKind.Stockpile) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied);
        var built = Assert.IsType<Stockpile>(sim.World.Structures[at]);
        Assert.Equal(0, built.OwnerId);
        Assert.DoesNotContain(sim.World.Structures.Values, s => s is ConstructionSite);
    }

    [Fact]
    public void OrdinaryPlayer_PlaceSite_StillLeavesPendingSite()
    {
        var sim = MakeSim();
        var at = new TileCoord(3, 3);

        Assert.True(new PlaceSiteIntent(at, StructureKind.Stockpile) { PlayerId = 1 }.Resolve(sim).IsApplied);

        var site = Assert.IsType<ConstructionSite>(sim.World.Structures[at]);
        Assert.False(site.ConditionsMet(sim.World));
    }

    [Fact]
    public void GodPlayer_ValidationUnchanged_BiomeRejects()
    {
        var sim = MakeSim();
        // A Farm needs Grassland; a LumberCamp needs Forest — god or not.
        var outcome = new PlaceSiteIntent(new TileCoord(3, 3), StructureKind.LumberCamp) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsRejected);
        Assert.Empty(sim.World.Structures);
    }

    [Fact]
    public void GodPlayer_ClaimingKind_TransfersClaimToBuiltExtractor()
    {
        var sim = MakeSim();
        var at = new TileCoord(4, 4);
        Assert.True(new PlaceSiteIntent(at, StructureKind.Farm) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var farm = Assert.IsType<Extractor>(sim.World.Structures[at]);
        Assert.Equal(StructureCatalog.Spec(StructureKind.Farm).ClaimCount, farm.ClaimTiles.Count);
    }

    [Fact]
    public void GodPlayer_Wall_EverySegmentStands()
    {
        var sim = MakeSim();
        var path = new List<TileCoord> { new(2, 2), new(3, 2), new(4, 2) };

        Assert.True(new PlaceWallIntent(path) { PlayerId = 0 }.Resolve(sim).IsApplied);

        foreach (var t in path) Assert.IsType<Wall>(sim.World.Structures[t]);
    }

    [Fact]
    public void GodPlayer_Canal_FloodsOnPlacement()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var path = new List<TileCoord> { new(1, 5), new(2, 5) };

        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsApplied);

        foreach (var t in path)
        {
            Assert.Equal(Biome.Water, sim.World.Grid.BiomeAt(t));
            Assert.False(sim.World.Structures.ContainsKey(t));
        }
    }

    [Fact]
    public void GodMode_SurvivesSnapshotRoundTrip()
    {
        var sim = MakeSim();
        Assert.True(new PlaceSiteIntent(new TileCoord(3, 3), StructureKind.House) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.True(restored.World.Players[0].GodMode);
        Assert.False(restored.World.Players[1].GodMode);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        // And the restored god still builds on the spot.
        Assert.True(new PlaceSiteIntent(new TileCoord(5, 5), StructureKind.House) { PlayerId = 0 }.Resolve(restored).IsApplied);
        Assert.IsType<House>(restored.World.Structures[new TileCoord(5, 5)]);
    }

    [Fact]
    public void GodMode_TwinRun_HashesMatch()
    {
        static Simulation Run()
        {
            var sim = MakeSim();
            sim.SubmitIntent(1, new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Stockpile) { PlayerId = 0 });
            sim.SubmitIntent(2, new PlaceWallIntent(new List<TileCoord> { new(5, 1), new(5, 2) }) { PlayerId = 0 });
            sim.SubmitIntent(3, new PlaceSiteIntent(new TileCoord(6, 6), StructureKind.House) { PlayerId = 1 });
            sim.Run();
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    [Fact]
    public void Genesis_CarriesGodModeFromFactionSpec()
    {
        var spec = new GenesisSpec
        {
            Width = 6, Height = 6,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(1, 1), GodMode = true },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(4, 4) },
            },
        };
        var world = Genesis.Build(spec);
        Assert.True(world.Players[0].GodMode);
        Assert.False(world.Players[1].GodMode);
    }
}
