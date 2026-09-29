using Sim.Core;
using Sim.Core.Caches;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Scouting;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M38 Phase B (docs/scouting-secrets.md): only a scout on a mission charts a
// secret, the chart is delivered only when it gets home alive, the marker is
// the exact tile with a faint hint, and the owner's own eyes strike a marker
// whose secret is gone.
public class ChartTests
{
    private static readonly TileCoord Home = new(5, 5);
    private static readonly TileCoord CacheAt = new(5, 30);
    private static readonly TileCoord LookOut = new(5, 27);   // within a scout's sight of the cache

    private static (Simulation sim, Unit scout) MakeSim()
    {
        var world = new GameWorld(new TileGrid(40, 40, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[CacheConstants.OwnerId] = new Player(CacheConstants.OwnerId);
        world.AddStructure(new Lodge(new TileCoord(4, 4)) { OwnerId = 0 });
        var cache = world.AddStructure(new Cache(CacheAt) { OwnerId = CacheConstants.OwnerId });
        cache.Deposit(Resource.Iron, 5);
        var scout = world.AddUnit(new Unit(1, Home) { OwnerId = 0, Role = UnitRole.Scout });
        return (new Simulation(world, seed: 1), scout);
    }

    private static void Dispatch(Simulation sim, Unit scout, params TileCoord[] waypoints)
    {
        sim.SubmitIntent(sim.Now, new DispatchScoutIntent(scout.Id, waypoints.ToList()) { PlayerId = 0 });
        sim.Run(until: sim.Now);
    }

    private static ChartEntry? Entry(Simulation sim, TileCoord tile) =>
        Charts.Of(sim.World, 0) is { } c && c.TryGetValue(tile, out var e) ? e : null;

    [Fact]
    public void AScoutWhoComesHome_ChartsTheExactTile_WithAHint()
    {
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);

        // Out there, looking right at it: nothing charted yet.
        while (scout.Position != LookOut) sim.Run(until: sim.Now + Time.Hour);
        Assert.Null(Entry(sim, CacheAt));

        sim.Run(until: sim.Now + 10 * Time.Day);
        Assert.Equal(ScoutMissionState.Returned, sim.World.ScoutMissions[scout.Id].State);
        var e = Assert.IsType<ChartEntry>(Entry(sim, CacheAt));
        Assert.Equal(CacheAt, e.Tile);
        Assert.Equal(SecretHint.Glint, e.Hint);
        Assert.Equal(ChartState.Known, e.State);
        Assert.True(e.SeenTick > 0);
    }

    [Fact]
    public void TheReport_OnAMissionThatSawACache_Compiles()
    {
        // 2026-09-23 regression: the report compiler crashed the host on a cache's
        // sentinel owner (-2).
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);
        sim.Run(until: sim.Now + 10 * Time.Day);

        var report = Sim.Server.Scouting.ClaimsCompiler.Compile(
            sim.World, sim.World.ScoutMissions[scout.Id], new Sim.Server.Scouting.ScoutReportConstants());
        Assert.NotNull(report);
    }

    [Fact]
    public void AScoutKilledOnTheWayHome_ChartsNothing()
    {
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);
        while (scout.Position != LookOut) sim.Run(until: sim.Now + Time.Hour);

        CombatRules.OnUnitDeath(sim, scout);
        sim.Run(until: sim.Now + 10 * Time.Day);

        Assert.Null(Charts.Of(sim.World, 0));
    }

    [Fact]
    public void AnyoneElseWhoSeesIt_ChartsNothing()
    {
        var (sim, _) = MakeSim();
        var walker = sim.World.AddUnit(new Unit(2, Home) { OwnerId = 0, Role = UnitRole.Farmer });
        sim.SubmitIntent(0, new MoveIntent(walker.Id, new TileCoord(5, 29)) { PlayerId = 0 });
        sim.Run(until: 10 * Time.Day);

        Assert.Equal(new TileCoord(5, 29), walker.Position);   // stood right by it
        Assert.Null(Charts.Of(sim.World, 0));
    }

    [Fact]
    public void ACharted_CacheEmptiedLater_IsStruck_WhenTheOwnerLooksAgain()
    {
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);
        sim.Run(until: sim.Now + 10 * Time.Day);
        Assert.Equal(ChartState.Known, Entry(sim, CacheAt)!.State);

        // Someone empties it behind the fog: the marker does not know.
        var thief = sim.World.AddUnit(new Unit(3, CacheAt) { OwnerId = 7 });
        while (sim.World.Structures.ContainsKey(CacheAt)) { thief.Cargo.Clear(); CacheLooting.TryLoot(sim, thief, Resource.Iron); }
        Assert.Equal(ChartState.Known, Entry(sim, CacheAt)!.State);

        // The owner's own eyes on the tile strike it.
        var walker = sim.World.AddUnit(new Unit(2, Home) { OwnerId = 0, Role = UnitRole.Farmer });
        sim.SubmitIntent(sim.Now, new MoveIntent(walker.Id, new TileCoord(5, 28)) { PlayerId = 0 });
        sim.Run(until: sim.Now + 10 * Time.Day);

        var e = Entry(sim, CacheAt)!;
        Assert.Equal(ChartState.Gone, e.State);
        Assert.True(e.GoneTick > e.SeenTick);
    }

    [Fact]
    public void AnOwnerWatchingTheTile_SeesItGoAtOnce()
    {
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);
        sim.Run(until: sim.Now + 10 * Time.Day);
        sim.World.AddUnit(new Unit(2, new TileCoord(5, 28)) { OwnerId = 0 });   // standing watch

        var looter = sim.World.AddUnit(new Unit(3, CacheAt) { OwnerId = 0 });
        CacheLooting.TryLoot(sim, looter, Resource.Iron);

        Assert.False(sim.World.Structures.ContainsKey(CacheAt));
        Assert.Equal(ChartState.Gone, Entry(sim, CacheAt)!.State);
    }

    [Fact]
    public void TheChart_SurvivesASnapshot()
    {
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);
        sim.Run(until: sim.Now + 10 * Time.Day);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(CacheAt, Charts.Of(restored.World, 0)![CacheAt].Tile);
    }

    [Fact]
    public void ASnapshotMidMission_DeliversTheSameChart()
    {
        var (sim, scout) = MakeSim();
        Dispatch(sim, scout, LookOut);
        while (scout.Position != LookOut) sim.Run(until: sim.Now + Time.Hour);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        var until = sim.Now + 10 * Time.Day;
        sim.Run(until: until);
        restored.Run(until: until);

        Assert.NotNull(Charts.Of(restored.World, 0));
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
