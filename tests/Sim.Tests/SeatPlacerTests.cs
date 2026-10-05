using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server;

namespace Sim.Tests;

// Fair start placement (docs/fair-start-placement.md): the mainland is shared out evenly
// between the kingdoms. The old placement scanned rows from the top around a centre start,
// so every AI landed north of the human and some kingdoms had a sliver of the land.
public class SeatPlacerTests
{
    [Theory]
    [InlineData(3301431, 6)]
    [InlineData(7, 6)]
    [InlineData(3301431, 16)]
    public void EveryKingdom_GetsAFairShareOfTheMainland(int seed, int kingdoms)
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 252, MapHeight = 252, MapSeed = seed, AiPlayers = kingdoms - 1 });
        var castles = build.Spec.FactionStarts.Select(f => f.CastlePosition).ToList();
        Assert.Equal(kingdoms, castles.Count);

        // Each mainland tile to its nearest castle, straight line.
        var main = SeatPlacer.Mainland(build.Map);
        var share = new int[castles.Count];
        var total = 0;
        for (var y = 0; y < build.Map.Height; y++)
            for (var x = 0; x < build.Map.Width; x++)
            {
                if (!main[x, y]) continue;
                total++;
                var best = 0;
                for (var k = 1; k < castles.Count; k++)
                    if (D2(castles[k], x, y) < D2(castles[best], x, y)) best = k;
                share[best]++;
            }
        var mean = total / castles.Count;
        Assert.All(share, s => Assert.True(s * 2 >= mean, $"a kingdom holds {s} of a {mean} average ({string.Join(", ", share)})"));
    }

    [Fact]
    public void ACastle_StartsInAMeadow_WithWood_AndRoom()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 252, MapHeight = 252, MapSeed = 424242, AiPlayers = 5 });
        var main = SeatPlacer.Mainland(build.Map);
        var castles = build.Spec.FactionStarts.Select(f => f.CastlePosition).ToList();
        for (var i = 0; i < castles.Count; i++)
        {
            var others = castles.Where((_, j) => j != i).ToList();
            Assert.True(SeatPlacer.Viable(build.Map, main, castles[i].X, castles[i].Y, others), $"castle {i} at {castles[i]}");
        }
    }

    [Fact]
    public void OneKingdom_KeepsTheGeneratorsCentreStart()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 128, MapHeight = 128, MapSeed = 7, AiPlayers = 0 });
        Assert.Equal(build.Map.Start, build.Spec.FactionStarts.Single().CastlePosition);
    }

    private static long D2(TileCoord c, int x, int y) => (long)(c.X - x) * (c.X - x) + (long)(c.Y - y) * (c.Y - y);
}
