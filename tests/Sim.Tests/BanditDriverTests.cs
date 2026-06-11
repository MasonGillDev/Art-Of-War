using Sim.Core.Bandits;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Server.Bandits;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M16 Phase 4 — the bandit driver (the proto-automation layer): a brain
// OUTSIDE the sim that reads pure state and submits ordinary intents.
// Tests drive Think() by hand between Run() calls — no clock thread.
public class BanditDriverTests
{
    // A 64×64 grassland world built WITHOUT the spec ctor so tests can
    // compose structures/units directly (and the replay headline can
    // rebuild the identical starting world twice).
    private static Simulation MakeWorld(out GameWorld world)
    {
        var grid = new TileGrid(64, 64, Biome.Grassland);
        world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[BanditConstants.OwnerId] = new Player(BanditConstants.OwnerId);
        world.AddStructure(new Castle(new TileCoord(5, 5)) { OwnerId = 0 });
        return new Simulation(world, seed: 0xD21F3);
    }

    // Step the world forward, thinking once per driver period.
    private static void RunWithDriver(Simulation sim, BanditDriver driver, long until, long step)
    {
        for (var t = sim.Now; t <= until; t += step)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
        }
    }

    [Fact]
    public void Disabled_SubmitsNothing()
    {
        var sim = MakeWorld(out var world);
        var driver = new BanditDriver(new BanditConfig
        {
            Enabled = false, StructuresPerParty = 1, ThinkPeriodTicks = 10,
        });
        RunWithDriver(sim, driver, until: 1_000, step: 10);
        Assert.Equal(0, world.Players[BanditConstants.OwnerId].PopulationCount);
    }

    [Fact]
    public void Spawns_ToProsperityTarget_AndRespectsCap()
    {
        var sim = MakeWorld(out var world);
        world.AddStructure(new Stockpile(new TileCoord(7, 5)) { OwnerId = 0 });
        // 2 player structures / 1 per party = target 2, capped at 2.
        var cfg = new BanditConfig
        {
            StructuresPerParty = 1, MaxLiveParties = 2,
            PartySizeMin = 2, PartySizeMax = 3,
            ThinkPeriodTicks = 10, Seed = 7,
        };
        var driver = new BanditDriver(cfg);
        RunWithDriver(sim, driver, until: 2_000, step: 10);

        var banditCount = world.Players[BanditConstants.OwnerId].PopulationCount;
        Assert.True(banditCount >= cfg.PartySizeMin, $"no parties spawned ({banditCount})");
        Assert.True(banditCount <= cfg.MaxLiveParties * cfg.PartySizeMax,
            $"cap exceeded ({banditCount})");
        // Every bandit spawned beyond MinSpawnDistance of the base (they
        // may have wandered since, but none spawned in the player's lap —
        // pin via BornTick: all spawns happened, none adjacent at birth is
        // implied by the intent validation; here just sanity-check fog).
        Assert.All(world.Units.Values.Where(u => u.OwnerId == BanditConstants.OwnerId),
            u => Assert.Null(u.DeathTick));
    }

    [Fact]
    public void RaidLoop_Steals_Flees_Despawns_LootLeavesWorld()
    {
        var sim = MakeWorld(out var world);
        // An unstaffed, buffered lumber camp 30 tiles from the castle —
        // the far frontier. A party lurks 5 tiles beyond it (within their
        // sight 6, beyond every player vision source).
        var campAt = new TileCoord(35, 35);
        world.Grid.SetBiome(campAt, Biome.Forest);
        var camp = world.AddStructure(new Extractor(StructureKind.LumberCamp, campAt) { OwnerId = 0 });
        camp.Buffer = 20;
        camp.TickArmed = false;
        world.AddUnit(new Unit(100, new TileCoord(40, 35))
        {
            Role = UnitRole.Bandit, OwnerId = BanditConstants.OwnerId, BornTick = 0,
        });
        world.AddUnit(new Unit(101, new TileCoord(40, 35))
        {
            Role = UnitRole.Bandit, OwnerId = BanditConstants.OwnerId, BornTick = 0,
        });

        var driver = new BanditDriver(new BanditConfig
        {
            StructuresPerParty = 1000,   // no fresh spawns — this party only
            ThinkPeriodTicks = 30, Seed = 11,
        });

        RunWithDriver(sim, driver, until: 60_000, step: 30);

        // The raid happened: buffer emptied, raiders took it and vanished
        // into the fog WITH the loot.
        Assert.Equal(0, camp.Buffer);
        Assert.Equal(0, world.Players[BanditConstants.OwnerId].PopulationCount);
        Assert.Empty(world.GroundResources);
        // The camp itself was never harmed (no structure damage in M16).
        Assert.True(world.Structures.ContainsKey(campAt));
    }

    [Fact]
    public void Ambusher_SitsStill_UntilSomethingWalksIn()
    {
        // AmbusherEvery = 1: every spawned party lurks. The driver picks
        // the (seeded, deterministic) spawn site; we read it back, verify
        // the party holds position, then walk prey into its sight.
        var sim = MakeWorld(out var world);
        var driver = new BanditDriver(new BanditConfig
        {
            StructuresPerParty = 1, MaxLiveParties = 1,
            PartySizeMin = 1, PartySizeMax = 1,
            AmbusherEvery = 1, ThinkPeriodTicks = 30, Seed = 13,
        });

        RunWithDriver(sim, driver, until: 600, step: 30);
        var bandit = world.Units.Values
            .Single(u => u.OwnerId == BanditConstants.OwnerId);
        var lurkAt = bandit.Position;

        // An ambusher does not wander: thousands of ticks, zero movement.
        RunWithDriver(sim, driver, until: sim.Now + 3_000, step: 30);
        Assert.Equal(lurkAt, bandit.Position);

        // Prey wanders into sight — adjacent, weak, laden.
        world.AddUnit(new Unit(200, new TileCoord(lurkAt.X + 2, lurkAt.Y))
        {
            Role = UnitRole.Hauler, OwnerId = 0, BornTick = 0,
            CargoResource = Resource.Wood, CargoAmount = 10,
        });
        RunWithDriver(sim, driver, until: sim.Now + 10_000, step: 30);

        // Sprung: the party converged, combat ran on co-location, the
        // hauler (10HP/1pwr) lost to the bandit (25HP/3pwr), and its
        // cargo hit the ground where it fell.
        Assert.False(world.Units.ContainsKey(200));
        Assert.True(world.GroundResources.Values.Any(p => p.ContainsKey(Resource.Wood)));
    }

    [Fact]
    public void Headline_ReplayFromIntentLog_HashesMatch()
    {
        // THE architectural claim (docs/m16-bandits-spec.md): the driver
        // is OUTSIDE the sim, so replaying its logged intents — without
        // the driver — reproduces the world bit-for-bit. This is the same
        // proof the future player-automation layer rides on.
        Simulation Build()
        {
            var sim = MakeWorld(out var world);
            var campAt = new TileCoord(35, 35);
            world.Grid.SetBiome(campAt, Biome.Forest);
            var camp = world.AddStructure(new Extractor(StructureKind.LumberCamp, campAt) { OwnerId = 0 });
            camp.Buffer = 20;
            camp.TickArmed = false;
            world.AddUnit(new Unit(100, new TileCoord(40, 35))
            {
                Role = UnitRole.Bandit, OwnerId = BanditConstants.OwnerId, BornTick = 0,
            });
            return sim;
        }

        // Live run: driver thinks, decides, submits.
        var live = Build();
        var driver = new BanditDriver(new BanditConfig
        {
            StructuresPerParty = 1000, ThinkPeriodTicks = 30, Seed = 17,
        });
        for (long t = 0; t <= 30_000; t += 30)
        {
            live.Run(until: t);
            driver.Think(live, t);
        }
        var endTick = live.Now;

        // Replay run: no driver — just the durable log, round-tripped
        // through the SAME JSON registry the SQLite store uses. Submission
        // must INTERLEAVE chronologically the way live submission did
        // (the driver submits at tick T after T's events have run, so its
        // intent sorts after them); run to each intent's tick, then submit.
        var replay = Build();
        foreach (var ev in live.ResolvedLog.OfType<IntentEvent>().OrderBy(e => e.Seq))
        {
            replay.Run(until: ev.At);
            var (typeName, payload) = Sim.Persistence.IntentJson.Serialize(ev.Intent);
            replay.SubmitIntent(ev.At, Sim.Persistence.IntentJson.Deserialize(typeName, payload));
        }
        replay.Run(until: endTick);

        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(replay));
    }
}
