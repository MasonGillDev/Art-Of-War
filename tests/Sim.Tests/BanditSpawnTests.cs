using Sim.Core.Bandits;
using Sim.Core.Engine;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M16 Phase 2 — spawn/despawn validation (docs/m16-bandits-spec.md):
// parties materialize only in the dark and at a distance, vanish only in
// the dark, and take their cargo with them when they do.
public class BanditSpawnTests
{
    // 64×64 grassland; player castle at (5,5) with one builder. Everything
    // beyond castle vision (5) + MinSpawnDistance is safely dark and far.
    private static Simulation MakeSim()
    {
        var spec = new GenesisSpec
        {
            Width = 64, Height = 64,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(5, 5),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(5, 5), UnitRole.Builder, OwnerId: 0),
                    },
                },
            },
        };
        return new Simulation(spec, seed: 0xBA2D);
    }

    private static readonly TileCoord FarDark = new(40, 40);

    private static IntentOutcome Spawn(Simulation sim, TileCoord at, int size,
        int playerId = BanditConstants.OwnerId) =>
        new SpawnBanditPartyIntent(at, size) { PlayerId = playerId }.Resolve(sim);

    [Fact]
    public void Spawn_FarDarkTile_CreatesParty()
    {
        var sim = MakeSim();
        var outcome = Spawn(sim, FarDark, size: 3);
        Assert.True(outcome.IsApplied, outcome.Reason);

        var bandits = sim.World.Units.Values
            .Where(u => u.OwnerId == BanditConstants.OwnerId).ToList();
        Assert.Equal(3, bandits.Count);
        Assert.All(bandits, b =>
        {
            Assert.Equal(UnitRole.Bandit, b.Role);
            Assert.Equal(FarDark, b.Position);
            Assert.Equal(sim.Now, b.BornTick);
            Assert.Null(b.DeathTick);   // age-exempt: no lifespan scheduled
        });
        Assert.Equal(3, sim.World.Players[BanditConstants.OwnerId].PopulationCount);
        // Player food machinery untouched (bandits have no castle).
        Assert.Equal(1, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void Spawn_ValidationMatrix()
    {
        var sim = MakeSim();
        // Visible tile (inside castle vision radius).
        Assert.True(Spawn(sim, new TileCoord(7, 5), 2).IsRejected);
        // Dark but too close: Chebyshev 8 from the castle is outside its
        // Euclidean-5 vision yet inside MinSpawnDistance.
        var nearDark = new TileCoord(5 + BanditConstants.MinSpawnDistance - 2, 5);
        Assert.False(BanditRules.IsSeenByAnyPlayer(sim.World, nearDark));
        Assert.True(Spawn(sim, nearDark, 2).IsRejected);
        // Out of bounds.
        Assert.True(Spawn(sim, new TileCoord(64, 1), 2).IsRejected);
        // Bad sizes.
        Assert.True(Spawn(sim, FarDark, 0).IsRejected);
        Assert.True(Spawn(sim, FarDark, BanditConstants.MaxPartySize + 1).IsRejected);
        // Wrong PlayerId — players can't conjure bandits even if the wire
        // guard were bypassed.
        Assert.True(Spawn(sim, FarDark, 2, playerId: 0).IsRejected);
        // Water tile.
        sim.World.Grid.SetBiome(FarDark, Biome.Water);
        Assert.True(Spawn(sim, FarDark, 2).IsRejected);
        // Nothing leaked from all those rejections.
        Assert.Equal(0, sim.World.Players[BanditConstants.OwnerId].PopulationCount);
    }

    [Fact]
    public void Spawn_ValidatesAtResolveTime_NotSubmitTime()
    {
        // The driver proposes from a stale read: the tile is dark at submit
        // but a player unit stands there by the time the intent resolves.
        var sim = MakeSim();
        var tile = FarDark;
        // Teleport-place a player scout next to the spawn site BEFORE the
        // queued intent fires (direct add = "the world changed in flight").
        sim.SubmitIntent(100, new SpawnBanditPartyIntent(tile, 2)
            { PlayerId = BanditConstants.OwnerId });
        sim.World.AddUnit(new Unit(90, new TileCoord(tile.X - 1, tile.Y))
        {
            Role = UnitRole.Scout, OwnerId = 0, BornTick = 0,
        });
        sim.Run(until: 100);

        Assert.Equal(0, sim.World.Players[BanditConstants.OwnerId].PopulationCount);
    }

    [Fact]
    public void IsSeenByAnyPlayer_MatchesVisibleTilesUnion()
    {
        // The validation MUST agree with the canonical fog math — pin the
        // mirror against View.VisibleTiles across the whole map.
        var sim = MakeSim();
        sim.World.AddUnit(new Unit(91, new TileCoord(30, 12))
        {
            Role = UnitRole.Scout, OwnerId = 0, BornTick = 0,
        });
        sim.World.AddStructure(new Tower(new TileCoord(20, 30)) { OwnerId = 0 });

        var union = Sim.Core.Vision.View.VisibleTiles(sim.World, 0);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var t = new TileCoord(x, y);
                Assert.Equal(union.Contains(t),
                    BanditRules.IsSeenByAnyPlayer(sim.World, t));
            }
    }

    [Fact]
    public void Despawn_InDark_RemovesParty_AndLootLeavesTheWorld()
    {
        var sim = MakeSim();
        Assert.True(Spawn(sim, FarDark, 2).IsApplied);
        var ids = sim.World.Units.Values
            .Where(u => u.OwnerId == BanditConstants.OwnerId)
            .Select(u => u.Id).ToArray();
        // Hand them stolen goods.
        foreach (var id in ids)
        {
            sim.World.Units[id].CargoResource = Resource.Wood;
            sim.World.Units[id].CargoAmount = 10;
        }

        var outcome = new DespawnBanditPartyIntent(ids)
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim);
        Assert.True(outcome.IsApplied, outcome.Reason);

        Assert.Equal(0, sim.World.Players[BanditConstants.OwnerId].PopulationCount);
        Assert.DoesNotContain(sim.World.Units.Values,
            u => u.OwnerId == BanditConstants.OwnerId);
        // The loot did NOT drop — it vanished with them.
        Assert.False(sim.World.GroundResources.ContainsKey(FarDark));
    }

    [Fact]
    public void Despawn_WhileAnyUnitSeen_Rejects_Atomically()
    {
        var sim = MakeSim();
        Assert.True(Spawn(sim, FarDark, 2).IsApplied);
        var ids = sim.World.Units.Values
            .Where(u => u.OwnerId == BanditConstants.OwnerId)
            .Select(u => u.Id).ToArray();
        // A pursuing scout keeps ONE of them in sight.
        sim.World.Units[ids[1]].Position = new TileCoord(50, 50);
        sim.World.AddUnit(new Unit(92, new TileCoord(49, 50))
        {
            Role = UnitRole.Scout, OwnerId = 0, BornTick = 0,
        });

        var outcome = new DespawnBanditPartyIntent(ids)
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim);
        Assert.True(outcome.IsRejected);
        // Atomic: the unseen one didn't vanish either.
        Assert.Equal(2, sim.World.Players[BanditConstants.OwnerId].PopulationCount);
    }

    [Fact]
    public void Despawn_ValidationMatrix()
    {
        var sim = MakeSim();
        Assert.True(Spawn(sim, FarDark, 1).IsApplied);
        var id = sim.World.Units.Values
            .Single(u => u.OwnerId == BanditConstants.OwnerId).Id;

        // Wrong PlayerId.
        Assert.True(new DespawnBanditPartyIntent(new[] { id }) { PlayerId = 0 }
            .Resolve(sim).IsRejected);
        // Unknown unit.
        Assert.True(new DespawnBanditPartyIntent(new[] { 9999 })
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim).IsRejected);
        // Duplicate ids.
        Assert.True(new DespawnBanditPartyIntent(new[] { id, id })
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim).IsRejected);
        // A non-bandit unit.
        Assert.True(new DespawnBanditPartyIntent(new[] { 1 })
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim).IsRejected);
        // Empty list.
        Assert.True(new DespawnBanditPartyIntent(Array.Empty<int>())
            { PlayerId = BanditConstants.OwnerId }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void SpawnDespawn_TwinRun_HashesMatch_AndSurviveSnapshot()
    {
        Simulation Run()
        {
            var sim = MakeSim();
            sim.SubmitIntent(0, new SpawnBanditPartyIntent(FarDark, 3)
                { PlayerId = BanditConstants.OwnerId });
            sim.Run(until: 50);
            // March the party toward the castle, then despawn the survivors
            // back in the dark before they get close.
            foreach (var u in sim.World.Units.Values
                         .Where(u => u.OwnerId == BanditConstants.OwnerId).ToList())
                sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(u.Id, new TileCoord(35, 35))
                    { PlayerId = BanditConstants.OwnerId });
            sim.Run(until: 2000);
            var ids = sim.World.Units.Values
                .Where(u => u.OwnerId == BanditConstants.OwnerId)
                .Select(u => u.Id).OrderBy(i => i).ToArray();
            sim.SubmitIntent(sim.Now, new DespawnBanditPartyIntent(ids)
                { PlayerId = BanditConstants.OwnerId });
            sim.Run(until: 3000);
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.Equal(0, a.World.Players[BanditConstants.OwnerId].PopulationCount);

        // And a mid-march snapshot round-trips identically.
        var c = MakeSim();
        c.SubmitIntent(0, new SpawnBanditPartyIntent(FarDark, 3)
            { PlayerId = BanditConstants.OwnerId });
        c.Run(until: 50);
        foreach (var u in c.World.Units.Values
                     .Where(u => u.OwnerId == BanditConstants.OwnerId).ToList())
            c.SubmitIntent(c.Now, new Sim.Core.Movement.MoveIntent(u.Id, new TileCoord(35, 35))
                { PlayerId = BanditConstants.OwnerId });
        c.Run(until: 500);   // mid-march, arrival events in flight
        var restored = Snapshot.Restore(Snapshot.Serialize(c), seed: 0xBA2D);
        Assert.Equal(Snapshot.Hash(c), Snapshot.Hash(restored));
        c.Run(until: 3000);
        restored.Run(until: 3000);
        Assert.Equal(Snapshot.Hash(c), Snapshot.Hash(restored));
    }
}
