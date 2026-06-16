using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M22 — the highest terrain (Mountain) is common knowledge: every player sees
// the peaks from the start, making the opening a RACE to the scarcest resource
// band rather than a fog-gated discovery. Revealed as TERRAIN ONLY (in
// RememberedTerrain) — the units / structures / roads ON a mountain stay
// fogged until a player gets real vision there. See docs/high-terrain-visibility.md.
public class HighTerrainVisibilityTests
{
    // A 20x20 world, castle for player 0 at (2,2) (vision radius 5), with the
    // given biome overrides. (18,18) is far outside the castle disc — unexplored.
    private static GameWorld MakeWorld(params (TileCoord at, Biome biome)[] overrides)
    {
        var biomes = new Dictionary<TileCoord, Biome>();
        foreach (var (at, b) in overrides) biomes[at] = b;
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Biomes = biomes,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
            },
        };
        return Genesis.Build(spec);
    }

    [Fact]
    public void Mountain_Unexplored_AppearsInRememberedTerrain_AsTerrainOnly()
    {
        var mountain = new TileCoord(18, 18);
        var world = MakeWorld((mountain, Biome.Mountain));

        var view = View.BuildPlayerView(world, playerId: 0);

        // The far mountain is neither currently visible nor personally explored…
        Assert.DoesNotContain(mountain, view.Visible);
        Assert.DoesNotContain(mountain, view.Explored);
        // …yet its terrain is common knowledge from the start.
        Assert.True(view.RememberedTerrain.TryGetValue(mountain, out var b));
        Assert.Equal(Biome.Mountain, b);
    }

    [Fact]
    public void NonMountainTerrain_Unexplored_StaysFogged()
    {
        // Forest / Hills (a lower band) get no free reveal — fog still hides them.
        var forest = new TileCoord(18, 18);
        var hills = new TileCoord(17, 18);
        var world = MakeWorld((forest, Biome.Forest), (hills, Biome.Hills));

        var view = View.BuildPlayerView(world, playerId: 0);

        Assert.DoesNotContain(forest, view.Visible);
        Assert.DoesNotContain(forest, view.RememberedTerrain.Keys);
        Assert.DoesNotContain(hills, view.RememberedTerrain.Keys);
    }

    [Fact]
    public void EnemyOnUnexploredMountain_StaysHidden_OnlyTerrainRevealed()
    {
        // The race semantics: you see WHERE the mountain is, not who already
        // arrived. An enemy unit on an un-scouted peak must not surface.
        var mountain = new TileCoord(18, 18);
        var world = MakeWorld((mountain, Biome.Mountain));
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(99, mountain) { OwnerId = 1, Role = UnitRole.Soldier });

        var view = View.BuildPlayerView(world, playerId: 0);

        // Terrain revealed…
        Assert.Contains(mountain, view.RememberedTerrain.Keys);
        // …but the enemy on it is NOT (no vision there).
        Assert.DoesNotContain(view.VisibleUnits, u => u.Id == 99);
    }

    [Fact]
    public void VisibleMountain_StillShowsLiveEntitiesOnIt()
    {
        // A mountain the player CAN see behaves normally: it's in Visible and
        // entities on it surface. The reveal only adds terrain memory for the
        // ones you can't see — it never suppresses live visibility.
        var mountain = new TileCoord(3, 2); // adjacent to the castle — in its disc
        var world = MakeWorld((mountain, Biome.Mountain));
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(99, mountain) { OwnerId = 1, Role = UnitRole.Soldier });

        var view = View.BuildPlayerView(world, playerId: 0);

        Assert.Contains(mountain, view.Visible);
        Assert.DoesNotContain(mountain, view.RememberedTerrain.Keys); // shown live, not remembered
        Assert.Contains(view.VisibleUnits, u => u.Id == 99);
    }

    [Fact]
    public void CommonKnowledgeTerrain_IsExactlyMountainTiles()
    {
        var world = MakeWorld(
            (new TileCoord(1, 1), Biome.Mountain),
            (new TileCoord(15, 16), Biome.Mountain),
            (new TileCoord(4, 4), Biome.Forest),
            (new TileCoord(5, 5), Biome.Hills));

        Assert.Equal(
            new HashSet<TileCoord> { new(1, 1), new(15, 16) },
            new HashSet<TileCoord>(world.CommonKnowledgeTerrain));
    }

    [Fact]
    public void MountainReveal_BuildPlayerView_IsPureRead_NoMutation()
    {
        var world = MakeWorld((new TileCoord(18, 18), Biome.Mountain));
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
            View.BuildPlayerView(world, playerId: 0, now: sim.Now);

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void MountainReveal_DoesNotAffectSnapshotHash_AndSurvivesRoundTrip()
    {
        var mountain = new TileCoord(18, 18);
        var world = MakeWorld((mountain, Biome.Mountain));
        var sim = new Simulation(world, seed: 1);

        // Building views must not change the persisted/hashed state (the cache
        // is derived, non-serialized worldgen data).
        View.BuildPlayerView(world, 0, 0);
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        // The restored world recomputes the same reveal from its grid.
        Assert.Contains(mountain, View.BuildPlayerView(restored.World, 0, 0).RememberedTerrain.Keys);
    }
}
