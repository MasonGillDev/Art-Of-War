using Sim.Core.Engine;
using Sim.Core.Rivers;
using Sim.Core.Wilderness;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Wilderness bands (docs/wilderness-bands.md): every tile's walking time from the nearest
// starting castle, measured at genesis and frozen; the bands cut the reachable land by share.
public class WildernessTests
{
    // A 20 × 3 grassland strip. Water fills column 15, so columns 16–19 are an island no
    // foot reaches. Castles at the given tiles.
    private static GameWorld Strip(IReadOnlyDictionary<TileCoord, RiverEdge>? rivers = null, params TileCoord[] castles)
    {
        var biomes = new Dictionary<TileCoord, Biome>();
        for (var y = 0; y < 3; y++) biomes[new TileCoord(15, y)] = Biome.Water;
        return Genesis.Build(new GenesisSpec
        {
            Width = 20, Height = 3,
            Biomes = biomes,
            Rivers = rivers ?? new Dictionary<TileCoord, RiverEdge>(),
            FactionStarts = castles.Select((c, i) => new FactionStartSpec { OwnerId = i, CastlePosition = c }).ToList(),
        });
    }

    private static readonly int Grass = Sim.Core.World.Biomes.MoveCost(Biome.Grassland);

    [Fact]
    public void Minutes_AreTheWalkFromTheNearestCastle()
    {
        var f = Strip(null, new TileCoord(0, 1), new TileCoord(14, 1)).Wilderness;

        Assert.Equal(0, f.MinutesAt(new TileCoord(0, 1)));
        Assert.Equal(0, f.MinutesAt(new TileCoord(14, 1)));
        Assert.Equal(3 * Grass, f.MinutesAt(new TileCoord(3, 1)));
        Assert.Equal(4 * Grass, f.MinutesAt(new TileCoord(10, 1)));   // nearer the east castle
        Assert.Equal(8 * Grass, f.MinutesAt(new TileCoord(7, 0)));    // 7 along, 1 up
        Assert.Equal(WildBand.Settled, f.BandAt(new TileCoord(0, 1)));
    }

    [Fact]
    public void Water_HasNoBand_AndLandBeyondReach_IsDeep()
    {
        var f = Strip(null, new TileCoord(0, 1)).Wilderness;

        Assert.Equal(WildBand.None, f.BandAt(new TileCoord(15, 1)));
        Assert.Null(f.MinutesAt(new TileCoord(15, 1)));
        Assert.Equal(WildBand.Deep, f.BandAt(new TileCoord(17, 1)));
        Assert.Null(f.MinutesAt(new TileCoord(17, 1)));
        Assert.Equal(WildBand.None, f.BandAt(new TileCoord(40, 1)));   // off the map
    }

    [Fact]
    public void ARiver_CostsWhatItCostsToWalk()
    {
        // A river between columns 5 and 6, the whole height of the strip.
        var rivers = new Dictionary<TileCoord, RiverEdge>();
        for (var y = 0; y < 3; y++)
        {
            rivers[new TileCoord(5, y)] = RiverEdge.East;
            rivers[new TileCoord(6, y)] = RiverEdge.West;
        }
        var f = Strip(rivers, new TileCoord(0, 1)).Wilderness;

        Assert.Equal(5 * Grass, f.MinutesAt(new TileCoord(5, 1)));
        Assert.Equal(6 * Grass + RiverConstants.CrossingCost, f.MinutesAt(new TileCoord(6, 1)));
    }

    // Genesis always has a castle; a world built by hand (most unit tests) has no field.
    [Fact]
    public void NoCastle_NoBands()
    {
        var world = new GameWorld(new TileGrid(20, 3));
        Assert.True(world.Wilderness.IsEmpty);
        var f = WildernessField.Compute(world, Array.Empty<TileCoord>(), new WildernessConfig());

        Assert.True(f.IsEmpty);
        Assert.Equal(WildBand.None, f.BandAt(new TileCoord(3, 1)));
        Assert.Null(f.MinutesAt(new TileCoord(3, 1)));
    }

    // On a generated world the bands hold their shares of the reachable land (ties at a
    // cut-off can tip a few tiles either way), rise with distance, and start at the castles.
    [Fact]
    public void Bands_CutTheReachableLand_ByShare_AndRiseWithDistance()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 128, MapHeight = 128, MapSeed = 7, AiPlayers = 2 });
        var world = Genesis.Build(build.Spec);
        var f = world.Wilderness;

        var count = new Dictionary<WildBand, int>();
        var reachable = 0;
        var bandFloor = new Dictionary<WildBand, int>();
        for (var y = 0; y < world.Grid.Height; y++)
            for (var x = 0; x < world.Grid.Width; x++)
            {
                var t = new TileCoord(x, y);
                if (f.MinutesAt(t) is not { } m) continue;
                reachable++;
                var b = f.BandAt(t);
                count[b] = count.GetValueOrDefault(b) + 1;
                bandFloor[b] = Math.Min(bandFloor.GetValueOrDefault(b, int.MaxValue), m);
            }

        int Pct(WildBand b) => 100 * count.GetValueOrDefault(b) / reachable;
        Assert.InRange(Pct(WildBand.Settled), 37, 43);
        Assert.InRange(Pct(WildBand.Frontier), 27, 33);
        Assert.InRange(Pct(WildBand.Wild), 17, 23);
        Assert.InRange(Pct(WildBand.Deep), 7, 13);
        Assert.True(bandFloor[WildBand.Settled] < bandFloor[WildBand.Frontier]);
        Assert.True(bandFloor[WildBand.Frontier] < bandFloor[WildBand.Wild]);
        Assert.True(bandFloor[WildBand.Wild] < bandFloor[WildBand.Deep]);
        foreach (var fs in build.Spec.FactionStarts)
        {
            Assert.Equal(0, f.MinutesAt(fs.CastlePosition));
            Assert.Equal(WildBand.Settled, f.BandAt(fs.CastlePosition));
        }
    }

    // Frozen: the field is part of the world's state, so a restore brings back exactly what
    // genesis measured (the hash covers it), and a later change to the land doesn't move it.
    [Fact]
    public void TheField_SurvivesASnapshot_AndNeverMoves()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 3, AiPlayers = 1 });
        var sim = new Simulation(build.Spec, seed: 0xA117);
        sim.Run(until: sim.Now + 500);
        var before = sim.World.Wilderness;

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xA117);
        var after = restored.World.Wilderness;

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal((before.FrontierFrom, before.WildFrom, before.DeepFrom), (after.FrontierFrom, after.WildFrom, after.DeepFrom));
        for (var y = 0; y < 96; y++)
            for (var x = 0; x < 96; x++)
            {
                var t = new TileCoord(x, y);
                Assert.Equal(before.MinutesAt(t), after.MinutesAt(t));
                Assert.Equal(before.BandAt(t), after.BandAt(t));
            }

        // Land changes (a canal floods a tile); the measure taken at genesis stands.
        var castle = build.Spec.FactionStarts[0].CastlePosition;
        var near = new TileCoord(castle.X + 2, castle.Y);
        var band = sim.World.Wilderness.BandAt(near);
        sim.World.Grid.SetBiome(near, Biome.Water);
        Assert.Equal(band, sim.World.Wilderness.BandAt(near));
    }
}
