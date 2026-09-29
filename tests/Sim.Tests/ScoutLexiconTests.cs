using Sim.Core.Caches;
using Sim.Core.World;
using Sim.Server.Scouting;

namespace Sim.Tests;

// 2026-09-23 regression: a scout that saw a cache (owner -2) crashed the host in
// the report compiler, which indexed the house-name table with a negative id.
public class ScoutLexiconTests
{
    [Theory]
    [InlineData(CacheConstants.OwnerId)]
    [InlineData(-3)]   // rubble
    public void TheWorldsOwnSentinels_AreNobodys(int ownerId) =>
        Assert.Equal("no one's", Lexicon.FactionName(ownerId));

    [Fact]
    public void Secrets_AreDescribedFaintly()
    {
        Assert.Equal("something glinting", Lexicon.StructureNoun(StructureKind.Cache));
        Assert.Equal("figure of stone", Lexicon.StructureNoun(StructureKind.Idol));
    }
}
