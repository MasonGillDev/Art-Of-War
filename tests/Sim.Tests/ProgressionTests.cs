using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.Progression;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M37 Phase A (docs/progression.md): the progress ledger counts what happened,
// a milestone fires once per player the first time its condition holds, AI
// seats are never enrolled, and the ledger survives a snapshot.
//
// The engine tests pass their own rows through the internal overloads, so a
// retune of the real catalog never turns them red.
public class ProgressionTests
{
    private static readonly TileCoord CastleAt = new(1, 1);

    private static Simulation MakeSim(bool enrolled = true)
    {
        var world = new GameWorld(new TileGrid(12, 12, Biome.Grassland));
        world.Players[0] = new Player(0) { Progress = enrolled ? new ProgressLedger() : null };
        world.AddStructure(new Castle(CastleAt) { OwnerId = 0 });
        return new Simulation(world, seed: 1);
    }

    private static int FoodIn(Simulation sim) =>
        ((Castle)sim.World.Structures[CastleAt]).Holdings.GetValueOrDefault(Resource.Food);

    private static readonly ProgressKey Soldiers = ProgressKey.Trained(UnitRole.Soldier);
    private static readonly ProgressKey Archers = ProgressKey.Trained(UnitRole.Archer);

    private static Milestone GiftAt(int id, Condition when, int food = 10) =>
        new(id, $"gift {id}", when, new Effect[] { new Grant(Resource.Food, food) });

    // -------- the engine --------

    [Fact]
    public void ARow_FiresOnce_TheFirstTimeItsConditionHolds()
    {
        var sim = MakeSim();
        var rows = new[] { GiftAt(1, Condition.AtLeast(2, Soldiers)) };
        var before = FoodIn(sim);

        Progression.Bump(sim, 0, Soldiers, 1, rows);
        Assert.Equal(before, FoodIn(sim));
        Assert.False(sim.World.Players[0].Progress!.HasFired(1));

        Progression.Bump(sim, 0, Soldiers, 1, rows);
        Assert.Equal(before + 10, FoodIn(sim));
        Assert.True(sim.World.Players[0].Progress!.HasFired(1));

        // Crossing again, and far past, fires nothing more.
        Progression.Bump(sim, 0, Soldiers, 5, rows);
        Progression.Check(sim, 0, rows);
        Assert.Equal(before + 10, FoodIn(sim));
        Assert.Equal(7, sim.World.Players[0].Progress!.Count(Soldiers));
    }

    [Fact]
    public void AtLeast_SumsItsCounters()
    {
        var sim = MakeSim();
        var rows = new[] { GiftAt(1, Condition.AtLeast(3, Soldiers, Archers)) };

        Progression.Bump(sim, 0, Soldiers, 2, rows);
        Assert.False(sim.World.Players[0].Progress!.HasFired(1));
        Progression.Bump(sim, 0, Archers, 1, rows);
        Assert.True(sim.World.Players[0].Progress!.HasFired(1));
    }

    [Fact]
    public void AFiring_CanSatisfyAnotherRow_InTheSameCheck()
    {
        var sim = MakeSim();
        var before = FoodIn(sim);
        // The follow-up is listed FIRST, so the check must loop back to it.
        var rows = new[]
        {
            GiftAt(2, Condition.Fired(1), food: 5),
            GiftAt(1, Condition.AtLeast(1, Soldiers), food: 10),
        };

        Progression.Bump(sim, 0, Soldiers, 1, rows);

        var ledger = sim.World.Players[0].Progress!;
        Assert.True(ledger.HasFired(1));
        Assert.True(ledger.HasFired(2));
        Assert.Equal(before + 15, FoodIn(sim));
    }

    [Fact]
    public void AllAndAny_Combine()
    {
        var sim = MakeSim();
        var rows = new[]
        {
            GiftAt(1, Condition.All(Condition.AtLeast(1, Soldiers), Condition.AtLeast(1, Archers))),
            GiftAt(2, Condition.Any(Condition.AtLeast(5, Soldiers), Condition.AtLeast(1, Archers))),
        };
        var ledger = sim.World.Players[0].Progress!;

        Progression.Bump(sim, 0, Soldiers, 1, rows);
        Assert.False(ledger.HasFired(1));
        Assert.False(ledger.HasFired(2));

        Progression.Bump(sim, 0, Archers, 1, rows);
        Assert.True(ledger.HasFired(1));
        Assert.True(ledger.HasFired(2));
    }

    [Fact]
    public void Gauges_ReadTheWorldAsItStands()
    {
        var sim = MakeSim();
        var player = sim.World.Players[0];
        sim.World.AddUnit(new Unit(1, new TileCoord(5, 5)) { OwnerId = 0 });
        sim.World.AddUnit(new Unit(2, new TileCoord(5, 5)) { OwnerId = 0 });
        sim.World.Explored[0] = new HashSet<TileCoord> { new(0, 0), new(0, 1), new(0, 2) };

        Assert.Equal(2, GaugeAtLeast.Read(sim.World, player, ProgressGauge.Population));
        Assert.Equal(3, GaugeAtLeast.Read(sim.World, player, ProgressGauge.TilesExplored));

        var rows = new[]
        {
            GiftAt(1, Condition.Gauge(ProgressGauge.Population, 2)),
            GiftAt(2, Condition.Gauge(ProgressGauge.TilesExplored, 4)),
        };
        Progression.Check(sim, 0, rows);
        Assert.True(player.Progress!.HasFired(1));
        Assert.False(player.Progress!.HasFired(2));
    }

    [Fact]
    public void AnUnenrolledPlayer_CountsNothing_AndFiresNothing()
    {
        var sim = MakeSim(enrolled: false);
        var before = FoodIn(sim);
        var rows = new[] { GiftAt(1, Condition.AtLeast(1, Soldiers)) };

        Progression.Bump(sim, 0, Soldiers, 3, rows);
        Progression.Check(sim, 0, rows);

        Assert.Null(sim.World.Players[0].Progress);
        Assert.Equal(before, FoodIn(sim));
    }

    [Fact]
    public void ADefeatedPlayer_CountsNothing_AndFiresNothing()
    {
        var sim = MakeSim();
        sim.World.Players[0].Defeated = true;
        var rows = new[] { GiftAt(1, Condition.AtLeast(1, Soldiers)) };

        Progression.Bump(sim, 0, Soldiers, 3, rows);

        Assert.Equal(0, sim.World.Players[0].Progress!.Count(Soldiers));
        Assert.False(sim.World.Players[0].Progress!.HasFired(1));
    }

    [Fact]
    public void Catalog_IdsAreUnique()
    {
        var ids = MilestoneCatalog.For(new ProgressionConfig()).Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // -------- the bump points --------

    [Fact]
    public void Training_Counts_AndTheCountOutlivesTheSoldier()
    {
        var sim = MakeSim();
        var at = new TileCoord(4, 4);
        sim.World.AddStructure(new Barracks(at) { OwnerId = 0 });
        var unit = sim.World.AddUnit(new Unit(1, at) { OwnerId = 0 });

        Assert.True(TrainingRules.Train(sim, unit, UnitRole.Soldier));
        Assert.Equal(1, sim.World.Players[0].Progress!.Count(Soldiers));

        CombatRules.OnUnitDeath(sim, unit);
        Assert.Equal(1, sim.World.Players[0].Progress!.Count(Soldiers));
    }

    [Fact]
    public void AFinishedBuild_Counts_ItsKind()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(6, 6);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.Barracks) { OwnerId = 0 });
        foreach (var (r, n) in StructureCatalog.Spec(StructureKind.Barracks).BuildCost) site.Deposit(r, n);
        sim.World.AddUnit(new Unit(1, siteTile) { Role = UnitRole.Builder, OwnerId = 0 });

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        Assert.IsType<Barracks>(sim.World.Structures[siteTile]);
        Assert.Equal(1, sim.World.Players[0].Progress!.Count(ProgressKey.Completed(StructureKind.Barracks)));
    }

    [Fact]
    public void ARefiner_Counts_WhatItMakes()
    {
        var sim = MakeSim();
        var spec = StructureCatalog.Spec(StructureKind.Smelter);
        var at = new TileCoord(8, 8);
        var smelter = (Extractor)sim.World.AddStructure(new Extractor(StructureKind.Smelter, at) { OwnerId = 0 });
        var bronze = spec.Recipes.Single(r => r.Output == Resource.Bronze);
        foreach (var (r, n) in bronze.Inputs) smelter.DepositInput(r, 2 * n);
        sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Farmer, OwnerId = 0 });
        sim.SubmitIntent(0, new AssignWorkersIntent(at, new[] { 1 }));

        sim.Run(until: 3 * spec.ProductionPeriodTicks);

        Assert.True(smelter.Buffer > 0);
        Assert.Equal(smelter.Buffer, sim.World.Players[0].Progress!.Count(ProgressKey.Refined(bronze.Output)));
    }

    // -------- persistence and enrolment --------

    [Fact]
    public void TheLedger_SurvivesASnapshot()
    {
        var sim = MakeSim();
        sim.World.Players[1] = new Player(1);   // an unenrolled seat stays unenrolled
        var rows = new[] { GiftAt(3, Condition.AtLeast(2, Soldiers)) };
        Progression.Bump(sim, 0, Soldiers, 2, rows);
        Progression.Bump(sim, 0, ProgressKey.Completed(StructureKind.House), 3, rows);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var ledger = restored.World.Players[0].Progress!;
        Assert.Equal(2, ledger.Count(Soldiers));
        Assert.Equal(3, ledger.Count(ProgressKey.Completed(StructureKind.House)));
        Assert.True(ledger.HasFired(3));
        Assert.Null(restored.World.Players[1].Progress);
    }

    [Fact]
    public void TheHumanSeat_IsEnrolled_AndAiSeatsAreNot()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = true });
        var sim = new Simulation(build.Spec, seed: 0x3A1E);

        Assert.NotNull(sim.World.Players[0].Progress);
        Assert.All(sim.World.Players.Where(p => p.Key != 0), p => Assert.Null(p.Value.Progress));

        // A bare ServerOptions (the labs, which drive player 0 with an AI) is off.
        var off = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1 });
        Assert.Null(new Simulation(off.Spec, seed: 0x3A1E).World.Players[0].Progress);

        // The host is on unless told otherwise.
        Assert.True(ServerOptions.Parse(Array.Empty<string>()).Progression);
        Assert.False(ServerOptions.Parse(new[] { "--progression", "0" }).Progression);
    }
}
