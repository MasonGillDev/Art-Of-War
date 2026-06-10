using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Persistence;

namespace Sim.Persistence.Tests;

// M4 Phase C: log-then-apply discipline. The intent must be durably
// committed BEFORE the sim sees it; recovery from a crash between log and
// apply replays the intent.
public class DurableSubmitTests
{
    private static Simulation MakeSim()
    {
        var spec = new GenesisSpec
        {
            Width = 8, Height = 8,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder) },
                },
            },
        };
        var world = Genesis.Build(spec);
        return new Simulation(world, seed: 1);
    }

    [Fact]
    public void SubmitIntentDurable_AppliesAndLogs()
    {
        var sim = MakeSim();
        using var store = SqliteIntentStore.OpenInMemory();

        var intent = new MoveIntent(1, new TileCoord(5, 5));
        DurableSubmit.SubmitIntentDurable(sim, store, at: 0, intent);

        // Logged.
        var rows = store.LoadIntentsAfter(-1).ToList();
        Assert.Single(rows);
        Assert.Equal("MoveIntent", rows[0].TypeName);
        Assert.Equal(0L, rows[0].Tick);

        // Applied — sim has the intent in its log + a queued IntentEvent.
        Assert.Single(sim.IntentLog);
    }

    [Fact]
    public void Submitted_IntentSurvives_StoreReopen()
    {
        var tempPath = Path.Combine(Path.GetTempPath(),
            $"aow-durable-{Guid.NewGuid():N}.db");
        try
        {
            var sim = MakeSim();
            using (var store = SqliteIntentStore.Open(tempPath))
            {
                DurableSubmit.SubmitIntentDurable(sim, store, at: 0,
                    new MoveIntent(1, new TileCoord(7, 0)));
            }
            using var reopened = SqliteIntentStore.Open(tempPath);
            var rows = reopened.LoadIntentsAfter(-1).ToList();
            Assert.Single(rows);
            var replay = IntentJson.Deserialize(rows[0].TypeName, rows[0].PayloadJson);
            Assert.IsType<MoveIntent>(replay);
            Assert.Equal(new TileCoord(7, 0), ((MoveIntent)replay).Destination);
        }
        finally
        {
            // Pooled connections hold the file handle past Dispose —
            // flush the pools before deleting (same fix as IntentStoreTests).
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var p in new[] { tempPath, tempPath + "-wal", tempPath + "-shm" })
                if (File.Exists(p)) File.Delete(p);
        }
    }
}
