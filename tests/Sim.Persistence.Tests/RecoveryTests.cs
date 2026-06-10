using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Persistence;

namespace Sim.Persistence.Tests;

// M4 Phase D â€” the closure gate for the milestone.
//
// CrashRecoveryMatchesUninterrupted: scenario S exercises every kind of
// in-flight process (mid-walk, mid-haul, mid-production, mid-build,
// mid-group-movement). Run S uninterrupted; record hash A. Run S with
// periodic snapshots + durable intents; "crash" mid-flight; Recover from
// the stores; run to the same end tick; assert hash matches A.
//
// PreSnapshotIntentsCanBeDeleted_StillRecovers: the insulation proof.
// Delete intents older than the latest snapshot from the log before
// recovery. Recovery still completes; hash still matches.
public class RecoveryTests
{
    private const ulong Seed = 0xC4A511;

    // The scenario factory â€” both the uninterrupted run and the
    // crash/recover run use the same intents in the same order.
    private static GenesisSpec MakeSpec() => new()
    {
        Width = 20, Height = 20,
        FactionStarts = new[]
        {
            new FactionStartSpec
            {
                OwnerId = 0,
                CastlePosition = new TileCoord(0, 0),
                CastleHoldings = new SortedDictionary<Resource, int>
                {
                    [Resource.Wood] = 200, [Resource.Stone] = 100,
                },
                UnitSpawns = new[]
                {
                    new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder),
                    new UnitSpawn(2, new TileCoord(0, 0), UnitRole.Hauler),
                    new UnitSpawn(3, new TileCoord(0, 0), UnitRole.Builder),
                    new UnitSpawn(4, new TileCoord(0, 0), UnitRole.Builder),
                },
            },
        },
    };

    // Apply the same script of intents at the same ticks to both runs. The
    // script generates multiple in-flight processes that overlap in time.
    private static void SeedHelperStructures(GameWorld world)
    {
        // A Stockpile at (15, 0) pre-loaded with Wood gives the hauler
        // somewhere meaningful to haul from.
        var stockpile = world.AddStructure(new Stockpile(new TileCoord(15, 0)) { OwnerId = 0 });
        stockpile.Deposit(Resource.Wood, 50);
    }

    private static List<(long tick, Sim.Core.Intents.Intent intent)> BuildIntentScript() => new()
    {
        (0,   new MoveIntent(1, new TileCoord(18, 18))),
        (0,   new HaulIntent(2, new TileCoord(15, 0), new TileCoord(0, 0), Resource.Wood)),
        (0,   new FormGroupIntent(new[] { 3, 4 }, new TileCoord(5, 5))),
        (10,  new MoveIntent(1, new TileCoord(2, 18))),  // mid-walk retask
    };

    private const long EndTick = 300;

    private static Simulation RunUninterrupted()
    {
        var world = Genesis.Build(MakeSpec());
        SeedHelperStructures(world);
        var sim = new Simulation(world, seed: Seed);
        foreach (var (tick, intent) in BuildIntentScript())
            sim.SubmitIntent(tick, intent);
        sim.Run(until: EndTick);
        return sim;
    }

    // ---------- Headline test ----------

    [Fact]
    public void CrashRecoveryMatchesUninterrupted()
    {
        var hashA = Snapshot.Hash(RunUninterrupted());

        // Crash-and-recover path:
        //  1. Run a sim with durable intents + periodic snapshots up to a
        //     mid-flight tick.
        //  2. Drop the in-memory sim ("crash").
        //  3. Recover from the stores.
        //  4. Run to EndTick.
        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();

        // Pre-crash: build sim, persist initial snapshot, submit intents
        // durably, run forward with periodic snapshots.
        {
            var world = Genesis.Build(MakeSpec());
            SeedHelperStructures(world);
            var sim = new Simulation(world, seed: Seed);
            snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(sim));

            // Submit every intent durably; run between snapshot boundaries
            // so a snapshot can land between intents.
            var script = BuildIntentScript();
            var i = 0;
            for (long target = 50; target <= 150 /* crash tick */; target += 50)
            {
                while (i < script.Count && script[i].tick <= target)
                {
                    var (t, intent) = script[i++];
                    DurableSubmit.SubmitIntentDurable(sim, intents, t, intent);
                }
                sim.Run(until: target);
                snaps.SaveSnapshot(sim.Now, Snapshot.FormatVersion, Snapshot.Serialize(sim));
            }
            // sim falls out of scope here â€” that's the "crash."
        }

        // Recover and run to EndTick.
        var recovered = Recovery.Recover(intents, snaps, Seed, targetTick: EndTick);
        Assert.Equal(hashA, Snapshot.Hash(recovered));
    }

    // ---------- Insulation proof ----------

    [Fact]
    public void PreSnapshotIntentsCanBeDeleted_StillRecovers()
    {
        var hashA = Snapshot.Hash(RunUninterrupted());

        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();

        // Identical pre-crash to CrashRecoveryMatchesUninterrupted.
        var world = Genesis.Build(MakeSpec());
        SeedHelperStructures(world);
        var sim = new Simulation(world, seed: Seed);
        snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(sim));
        var script = BuildIntentScript();
        var i = 0;
        for (long target = 50; target <= 150; target += 50)
        {
            while (i < script.Count && script[i].tick <= target)
            {
                var (t, intent) = script[i++];
                DurableSubmit.SubmitIntentDurable(sim, intents, t, intent);
            }
            sim.Run(until: target);
            snaps.SaveSnapshot(sim.Now, Snapshot.FormatVersion, Snapshot.Serialize(sim));
        }

        // Delete every intent older than the latest snapshot.
        var latestSnapTick = snaps.LoadLatest()!.Tick;
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"))
        {
            // Different connection â€” can't access the in-memory store. Use
            // a direct DELETE on the actual intents store instead.
        }
        DeletePreSnapshotIntents(intents, latestSnapTick);

        var recovered = Recovery.Recover(intents, snaps, Seed, targetTick: EndTick);
        Assert.Equal(hashA, Snapshot.Hash(recovered));
    }

    // ---------- No snapshot â†’ clear error ----------

    [Fact]
    public void NoSnapshot_Throws()
    {
        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();
        var ex = Assert.Throws<RecoveryException>(
            () => Recovery.Recover(intents, snaps, Seed));
        Assert.Contains("no snapshot", ex.Message);
    }

    // ---------- Retention ----------

    [Fact]
    public void SnapshotStore_KeepsLast3_PrunesOlder()
    {
        using var snaps = SqliteSnapshotStore.OpenInMemory(retention: 3);
        for (long t = 100; t <= 600; t += 100)
            snaps.SaveSnapshot(t, Snapshot.FormatVersion, new byte[] { (byte)(t / 100) });

        var list = snaps.ListSnapshots();
        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { 600L, 500L, 400L }, list.Select(s => s.Tick));
    }

    // ---------- M6: diplomacy survives crash+recover ----------

    [Fact]
    public void DeclareWar_RecoveryFiresAtCorrectTick()
    {
        // Build a two-faction world; declare war at tick 10; persist intents +
        // a snapshot mid-Delay; "crash"; recover; run past effective tick.
        // The war must take effect at the same tick an uninterrupted run hits.
        const long Delay = 100;
        const long EndTickLocal = 200;

        GenesisSpec MakeTwoFactionSpec() => new()
        {
            Width = 20, Height = 20,
            Diplomacy = new DiplomacyConfig(Delay: Delay, ProposalExpiryTicks: 300),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(15, 15) },
            },
        };

        // Path A: uninterrupted.
        var simA = new Simulation(Genesis.Build(MakeTwoFactionSpec()), seed: Seed);
        simA.SubmitIntent(10, new DeclareWarIntent(0, 1));
        simA.Run(until: EndTickLocal);
        var hashA = Snapshot.Hash(simA);

        // Path B: snapshot mid-Delay, recover, run to EndTick.
        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();

        var sim = new Simulation(Genesis.Build(MakeTwoFactionSpec()), seed: Seed);
        snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(sim));
        DurableSubmit.SubmitIntentDurable(sim, intents, at: 10, new DeclareWarIntent(0, 1));
        sim.Run(until: 10 + Delay / 2); // mid-Delay
        snaps.SaveSnapshot(sim.Now, Snapshot.FormatVersion, Snapshot.Serialize(sim));
        // sim falls out of scope = "crash"

        var recovered = Recovery.Recover(intents, snaps, Seed, targetTick: EndTickLocal);
        Assert.Equal(hashA, Snapshot.Hash(recovered));
        Assert.True(recovered.World.Diplomacy.AreHostile(0, 1));
    }

    // ---------- M7: in-progress combat survives crash+recover ----------

    [Fact]
    public void MidFightCombat_RecoveryResolvesIdentically()
    {
        // Build a 2-faction world with a contested tile mid-fight. Snapshot
        // mid-battle through the durable store, drop the sim ("crash"),
        // recover, run to natural resolution. Hash must match an
        // uninterrupted run.
        GenesisSpec MakeSpec() => new()
        {
            Width = 20, Height = 20,
            Combat = new CombatConfig(RoundIntervalTicks: 10),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        const ulong LocalSeed = 0xC0F;
        var tile = new TileCoord(10, 10);

        Simulation BuildAndStart()
        {
            var w = Genesis.Build(MakeSpec());
            w.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
            for (var i = 0; i < 2; i++)
                w.AddUnit(new Unit(100 + i, tile) { Role = UnitRole.Builder, OwnerId = 0 });
            for (var i = 0; i < 2; i++)
                w.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });
            var s = new Simulation(w, seed: LocalSeed);
            CombatTrigger.MaybeBeginCombatOnTile(s, tile);
            return s;
        }

        // Path A: uninterrupted.
        var simA = BuildAndStart();
        simA.Run(until: 1000);
        var hashA = Snapshot.Hash(simA);

        // Path B: snapshot mid-fight via the durable store, recover.
        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();

        var simB = BuildAndStart();
        snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(simB));
        simB.Run(until: 30); // mid-fight (rounds at ticks 10, 20, 30)
        Assert.True(simB.World.CombatStates.ContainsKey(tile));
        snaps.SaveSnapshot(simB.Now, Snapshot.FormatVersion, Snapshot.Serialize(simB));
        // sim falls out of scope = crash.

        var recovered = Recovery.Recover(intents, snaps, LocalSeed, targetTick: 1000);
        Assert.Equal(hashA, Snapshot.Hash(recovered));
    }

    // ---------- M8: pending death survives crash+recover ----------

    [Fact]
    public void DeathByAge_RecoveryFiresAtCorrectTick()
    {
        // Snapshot a sim with a pending old-age death anchor, recover,
        // and verify the unit dies at the same tick an uninterrupted run
        // would produce.
        var cfg = new PopulationConfig(
            TicksPerYear: 10,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: 50, BirthFoodCost: 5,
            LifespanMinYears: 20, LifespanMaxYears: 20);
        GenesisSpec MakeSpec() => new()
        {
            Width = 10, Height = 10,
            Population = cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 0,
                    UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder) },
                },
            },
        };
        const ulong LocalSeed = 0xA8E;

        var simA = new Simulation(MakeSpec(), LocalSeed);
        var deathTick = simA.World.Units[1].DeathTick!.Value;
        simA.Run(until: deathTick + 5);
        var hashA = Snapshot.Hash(simA);

        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();

        var simB = new Simulation(MakeSpec(), LocalSeed);
        snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(simB));
        simB.Run(until: deathTick / 2);
        snaps.SaveSnapshot(simB.Now, Snapshot.FormatVersion, Snapshot.Serialize(simB));
        // simB falls out of scope = crash.

        var recovered = Recovery.Recover(intents, snaps, LocalSeed, targetTick: deathTick + 5);
        Assert.Equal(hashA, Snapshot.Hash(recovered));
        Assert.False(recovered.World.Units.ContainsKey(1));
    }

    // ---------- M8: mid-gestation crash recovery produces child ----------

    [Fact]
    public void MidGestation_RecoveryProducesChild()
    {
        // Snapshot a mid-gestation world via the durable store, recover,
        // verify the birth fires at the original tick with identical hash.
        const long Gestation = 50;
        const int Food = 5;
        var cfg = new PopulationConfig(
            TicksPerYear: 10,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: Gestation, BirthFoodCost: Food,
            LifespanMinYears: 500, LifespanMaxYears: 500);
        var tile = new TileCoord(5, 5);
        GenesisSpec MakeSpec() => new()
        {
            Width = 10, Height = 10,
            Population = cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 25,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, tile, UnitRole.Builder, StartingAgeYears: 25),
                        new UnitSpawn(2, tile, UnitRole.Builder, StartingAgeYears: 25),
                    },
                },
            },
        };
        const ulong LocalSeed = 0xA8E;
        const long EndTick = Gestation + 30;

        Simulation BuildPrimed()
        {
            var s = new Simulation(MakeSpec(), LocalSeed);
            var h = s.World.AddStructure(new House(tile) { OwnerId = 0 });
            h.Deposit(Resource.Food, Food);
            return s;
        }

        var simA = BuildPrimed();
        simA.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        simA.Run(until: EndTick);
        var hashA = Snapshot.Hash(simA);

        using var intents = SqliteIntentStore.OpenInMemory();
        using var snaps   = SqliteSnapshotStore.OpenInMemory();

        var simB = BuildPrimed();
        DurableSubmit.SubmitIntentDurable(simB, intents, at: 0,
            new BeginBreedingIntent(tile, 1, 2));
        snaps.SaveSnapshot(0, Snapshot.FormatVersion, Snapshot.Serialize(simB));
        simB.Run(until: Gestation / 2);
        snaps.SaveSnapshot(simB.Now, Snapshot.FormatVersion, Snapshot.Serialize(simB));
        // crash.

        var recovered = Recovery.Recover(intents, snaps, LocalSeed, targetTick: EndTick);
        Assert.Equal(hashA, Snapshot.Hash(recovered));
        Assert.Single(recovered.World.Units.Values.Where(u => u.OwnerId == 0 && u.Role == UnitRole.None));
    }

    // ---------- Helpers ----------

    private static void DeletePreSnapshotIntents(SqliteIntentStore store, long snapshotTick)
    {
        // The store doesn't expose a delete API publicly (intent stores are
        // append-only in production). For the insulation test we reach
        // through to the underlying connection via the same connection
        // string trick: open another reference and run a DELETE. But we
        // hold the store across `using`, so any connection-mutating side
        // path needs cooperation. Simplest: use the SqliteConnection
        // already held by the store via reflection-free indirection ...
        //
        // The cleanest test-only path: add an internal helper to
        // SqliteIntentStore. Keeps the production surface unchanged.
        store.DeleteIntentsAtOrBefore(snapshotTick);
    }
}
