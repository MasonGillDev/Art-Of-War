using Sim.Server.Ai;

namespace Sim.Tests;

// M25 Phase 1 — AI personalities (docs/m25-rival-spec.md). Each faction is born
// with a posture seeded from (worldSeed, ownerId): deterministic so twin-runs
// reproduce the same cast, varied so a multi-faction world isn't monolithic, and
// Homesteader-by-default so the M17 brain and the whole balance lab are unchanged.
public class RivalPersonalityTests
{
    [Fact]
    public void AssignPersonality_IsDeterministic()
    {
        // Same (seed, ownerId) → same posture, every time. This is what lets a
        // twin-run / replay / recovered server re-derive the identical cast.
        for (var owner = 1; owner <= 8; owner++)
            Assert.Equal(
                RivalDoctrine.AssignPersonality(0xA117, owner),
                RivalDoctrine.AssignPersonality(0xA117, owner));
    }

    [Fact]
    public void AssignPersonality_VariesAcrossFactions()
    {
        // Over a spread of faction ids under one seed, all three postures show
        // up — the seeded mix is the whole point (a world of clones would be a
        // bug). Uniform-over-3 across 50 ids makes a missing posture a ~1e-8
        // event, so this is a real pin, not a flaky coin-flip.
        var seen = new HashSet<AiPersonality>();
        for (var owner = 1; owner <= 50; owner++)
            seen.Add(RivalDoctrine.AssignPersonality(0xBEEF, owner));
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void AssignPersonality_DiffersBySeed()
    {
        // The cast turns over with the world seed — a different match isn't the
        // same factions in the same roles. (Compared across the same id range.)
        var castA = Enumerable.Range(1, 12)
            .Select(o => RivalDoctrine.AssignPersonality(1, o)).ToList();
        var castB = Enumerable.Range(1, 12)
            .Select(o => RivalDoctrine.AssignPersonality(2, o)).ToList();
        Assert.NotEqual(castA, castB);
    }

    [Fact]
    public void Default_IsHomesteader_AndPeaceful()
    {
        // The default keeps the M17 brain and the lab unchanged.
        Assert.Equal(AiPersonality.Homesteader, new AiConfig().Personality);
        Assert.False(RivalDoctrine.IsWarCapable(AiPersonality.Homesteader));
        Assert.True(RivalDoctrine.IsWarCapable(AiPersonality.Opportunist));
        Assert.True(RivalDoctrine.IsWarCapable(AiPersonality.Warlord));
    }
}
