using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M6 Phase A: Genesis spawns a configurable number of factions, each with
// their own castle, holdings, and units. All factions are mutually known
// from tick 0 (Players registry has every OwnerId), but fog still hides
// their positions and holdings from each other.
public class MultiFactionGenesisTests
{
    private static GenesisSpec MakeNFactionSpec(int n)
    {
        // Each faction gets a corner-ish start so they don't see each other.
        // Grid is large enough that the 5-radius castle discs don't overlap.
        var starts = new List<FactionStartSpec>();
        for (var i = 0; i < n; i++)
        {
            // Lay factions out along a diagonal stride.
            var pos = new TileCoord(2 + i * 20, 2 + i * 20);
            starts.Add(new FactionStartSpec
            {
                OwnerId = i,
                CastlePosition = pos,
                CastleHoldings = new SortedDictionary<Resource, int>
                {
                    [Resource.Wood] = 10 * (i + 1),
                },
                UnitSpawns = new[]
                {
                    new UnitSpawn(Id: 100 + i, pos, UnitRole.Builder, OwnerId: i),
                },
            });
        }
        return new GenesisSpec
        {
            Width = 100, Height = 100,
            FactionStarts = starts,
        };
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Genesis_WithNFactions_SpawnsNCastlesAndUnits(int n)
    {
        var world = Genesis.Build(MakeNFactionSpec(n));

        Assert.Equal(n, world.Players.Count);
        for (var i = 0; i < n; i++)
        {
            Assert.True(world.Players.ContainsKey(i), $"player {i} missing");
            // Each faction has exactly one castle owned by them.
            var castles = world.Structures.Values.OfType<Castle>().Where(c => c.OwnerId == i).ToList();
            Assert.Single(castles);
            // And exactly one starting unit.
            var units = world.Units.Values.Where(u => u.OwnerId == i).ToList();
            Assert.Single(units);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Genesis_Twin_MatchesAcrossRuns(int n)
    {
        var a = new Simulation(Genesis.Build(MakeNFactionSpec(n)), seed: 0xA0F);
        var b = new Simulation(Genesis.Build(MakeNFactionSpec(n)), seed: 0xA0F);
        a.Run(until: 50);
        b.Run(until: 50);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Genesis_SnapshotRoundTrip_PreservesAllFactions(int n)
    {
        var sim = new Simulation(Genesis.Build(MakeNFactionSpec(n)), seed: 0xA0F);
        sim.Run(until: 20);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xA0F);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(n, restored.World.Players.Count);
    }

    [Fact]
    public void Genesis_FogHidesOtherFactions()
    {
        // Factions 0 and 1 are 20 tiles apart along a diagonal — well outside
        // their 5-radius castle discs. Each player's view should NOT show
        // the other's castle or unit on tick 0.
        var world = Genesis.Build(MakeNFactionSpec(2));
        var view0 = View.BuildPlayerView(world, playerId: 0);
        var view1 = View.BuildPlayerView(world, playerId: 1);

        Assert.DoesNotContain(view0.VisibleUnits, u => u.OwnerId == 1);
        Assert.DoesNotContain(view0.VisibleStructures, s => s.OwnerId == 1);
        Assert.DoesNotContain(view1.VisibleUnits, u => u.OwnerId == 0);
        Assert.DoesNotContain(view1.VisibleStructures, s => s.OwnerId == 0);
    }

    [Fact]
    public void Genesis_RejectsDuplicateOwnerIds()
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(15, 15) },
            },
        };
        Assert.Throws<InvalidOperationException>(() => Genesis.Build(spec));
    }

    [Fact]
    public void Genesis_RejectsEmptyFactionStarts()
    {
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = Array.Empty<FactionStartSpec>(),
        };
        Assert.Throws<InvalidOperationException>(() => Genesis.Build(spec));
    }
}
