using Sim.Core;
using Sim.Core.Bandits;
using Sim.Core.Caches;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Population;
using Sim.Core.Progression;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M37 Phase C (docs/progression.md): good fortune. Beating the reprisal pays
// out twice (the war chest where the last raider fell, and refugees who heard
// of it); exploring far enough brings a rumour of a ruin in the fog; three
// houses bring settlers. Newcomers walk in from the settled side. Every number
// comes from ProgressionConfig.
public class ArrivalTests
{
    private static readonly TileCoord Seat = new(40, 40);
    private static readonly TileCoord BarracksAt = new(42, 40);

    private static ProgressionConfig Cfg => new();

    private static Simulation MakeSim()
    {
        var world = new GameWorld(new TileGrid(80, 80, Biome.Grassland));
        world.Players[0] = new Player(0) { Progress = new ProgressLedger() };
        world.Players[BanditConstants.OwnerId] = new Player(BanditConstants.OwnerId);
        world.Players[CacheConstants.OwnerId] = new Player(CacheConstants.OwnerId);
        var castle = world.AddStructure(new Castle(Seat) { OwnerId = 0 });
        castle.Deposit(Resource.Food, castle.Capacity);   // nobody starves mid-test
        world.AddStructure(new Barracks(BarracksAt) { OwnerId = 0 });
        return new Simulation(world, seed: 1);
    }

    private static void TrainSoldiers(Simulation sim, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var u = sim.World.AddUnit(new Unit(sim.World.NextUnitId++, BarracksAt) { OwnerId = 0 });
            Assert.True(TrainingRules.Train(sim, u, UnitRole.Soldier));
        }
    }

    // Raise the reprisal and let the raiders land. Returns the raid.
    private static Omen LandTheRaid(Simulation sim)
    {
        TrainSoldiers(sim, Cfg.ReprisalTrained);
        var raid = Assert.Single(sim.World.Omens.Values);
        sim.Run(until: raid.DueTick);
        Assert.Equal(OmenState.Arrived, raid.State);
        return raid;
    }

    private static Omen? OmenOf(Simulation sim, OmenKind kind) =>
        sim.World.Omens.Values.SingleOrDefault(o => o.Kind == kind);

    // -------- the reprisal pays out --------

    [Fact]
    public void KillingEveryRaider_DropsTheWarChest_WhereTheLastOneFell()
    {
        var sim = MakeSim();
        var raid = LandTheRaid(sim);
        var ids = raid.PartyIds.ToList();
        var last = sim.World.Units[ids[^1]];

        foreach (var id in ids) CombatRules.OnUnitDeath(sim, sim.World.Units[id]);

        Assert.True(sim.World.GroundResources.TryGetValue(last.Position, out var pile));
        Assert.Equal(Cfg.ReprisalChestAmount, pile!.GetValueOrDefault(Cfg.ReprisalChestResource));
    }

    [Fact]
    public void ARaiderWhoRuns_TakesTheChestWithThem_ButTheRaidIsStillRepelled()
    {
        var sim = MakeSim();
        var raid = LandTheRaid(sim);
        var ids = raid.PartyIds.ToArray();

        // One flees empty-handed, the rest are killed.
        sim.SubmitIntent(sim.Now, new DespawnBanditPartyIntent(new[] { ids[0] }) { PlayerId = BanditConstants.OwnerId });
        sim.Run(until: sim.Now);
        foreach (var id in ids.Skip(1)) CombatRules.OnUnitDeath(sim, sim.World.Units[id]);

        Assert.Equal(1, sim.World.Players[0].Progress!.Count(ProgressKey.Repelled(MilestoneCatalog.Reprisal)));
        Assert.DoesNotContain(sim.World.GroundResources.Values,
            pile => pile.GetValueOrDefault(Cfg.ReprisalChestResource) > 0);
    }

    [Fact]
    public void BeatingTheReprisal_BringsRefugees_WhoWalkToTheSeat()
    {
        var sim = MakeSim();
        var raid = LandTheRaid(sim);
        foreach (var id in raid.PartyIds.ToList()) CombatRules.OnUnitDeath(sim, sim.World.Units[id]);

        var word = Assert.IsType<Omen>(OmenOf(sim, OmenKind.Refugees));
        Assert.Equal(MilestoneCatalog.WordSpreads, word.SourceMilestoneId);
        Assert.Equal(Cfg.WordSpreadsRefugees, word.Size);
        Assert.Equal(sim.Now + Cfg.WordSpreadsWarningTicks, word.DueTick);

        var before = sim.World.Players[0].PopulationCount;
        var knownIds = sim.World.Units.Keys.ToHashSet();
        sim.Run(until: word.DueTick);

        Assert.Equal(OmenState.Fulfilled, word.State);
        Assert.Equal(before + Cfg.WordSpreadsRefugees, sim.World.Players[0].PopulationCount);
        var newcomers = sim.World.Units.Values.Where(u => !knownIds.Contains(u.Id)).ToList();
        Assert.Equal(Cfg.WordSpreadsRefugees, newcomers.Count);
        Assert.All(newcomers, u =>
        {
            Assert.Equal(0, u.OwnerId);
            Assert.Equal(UnitRole.None, u.Role);
            Assert.NotNull(u.DeathTick);   // an ordinary life, rolled like anyone's
            Assert.Equal(Seat, u.PathFinalDest);
        });

        // And they get there.
        sim.Run(until: sim.Now + 10 * Time.Day);
        Assert.All(newcomers, u => Assert.Equal(Seat, sim.World.Units[u.Id].Position));
    }

    [Fact]
    public void LosingTheReprisal_BringsNoRefugees()
    {
        var sim = MakeSim();
        var raid = LandTheRaid(sim);
        var ids = raid.PartyIds.ToArray();
        sim.World.Units[ids[0]].Cargo.Add(Resource.Food, 5);
        sim.SubmitIntent(sim.Now, new DespawnBanditPartyIntent(ids) { PlayerId = BanditConstants.OwnerId });
        sim.Run(until: sim.Now);

        Assert.Equal(1, sim.World.Players[0].Progress!.Count(ProgressKey.Lost(MilestoneCatalog.Reprisal)));
        Assert.Equal(OmenState.Lost, raid.State);
        Assert.Null(OmenOf(sim, OmenKind.Refugees));
        Assert.False(sim.World.Players[0].Progress!.HasFired(MilestoneCatalog.WordSpreads));
    }

    [Fact]
    public void Newcomers_WalkInFromTheSettledSide()
    {
        var sim = MakeSim();
        // The owner knows only the north-east: that is the settled side.
        var known = new HashSet<TileCoord>();
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++)
                if (Omens.SectorOf(x - Seat.X, y - Seat.Y) == Bearing.NorthEast) known.Add(new TileCoord(x, y));
        sim.World.Explored[0] = known;

        Progression.Bump(sim, 0, ProgressKey.Completed(StructureKind.House), Cfg.GoodHomeHouses);

        var settlers = Assert.IsType<Omen>(OmenOf(sim, OmenKind.Refugees));
        Assert.Equal(MilestoneCatalog.AGoodHome, settlers.SourceMilestoneId);
        Assert.Equal(Cfg.GoodHomeSettlers, settlers.Size);
        Assert.Equal(Bearing.NorthEast, settlers.From);
    }

    // -------- far horizons --------

    [Fact]
    public void FarHorizons_PutsARuinInTheFog_AndTheRumourEndsWhenItIsEmptied()
    {
        var sim = MakeSim();
        // Explore everything but the west, until the mark.
        var known = new HashSet<TileCoord>();
        for (var y = 0; y < 80 && known.Count < Cfg.FarHorizonsTiles; y++)
            for (var x = 0; x < 80 && known.Count < Cfg.FarHorizonsTiles; x++)
                if (Omens.SectorOf(x - Seat.X, y - Seat.Y) != Bearing.West) known.Add(new TileCoord(x, y));
        sim.World.Explored[0] = known;
        Assert.Equal(Cfg.FarHorizonsTiles, known.Count);

        Progression.Check(sim, 0);

        var rumour = Assert.IsType<Omen>(OmenOf(sim, OmenKind.Rumour));
        Assert.Equal(OmenState.Arrived, rumour.State);
        var ruin = Assert.IsType<Cache>(sim.World.Structures[rumour.Target]);
        Assert.DoesNotContain(rumour.Target, known);
        Assert.Equal(rumour.From, Omens.SectorOf(rumour.Target.X - Seat.X, rumour.Target.Y - Seat.Y));
        Assert.Equal(Cfg.FarHorizonsIron, ruin.Holdings.GetValueOrDefault(Resource.Bronze));
        Assert.Equal(Cfg.FarHorizonsSwords, ruin.Holdings.GetValueOrDefault(Resource.BronzeSword));

        // Empty it, a load at a time.
        var looter = sim.World.AddUnit(new Unit(sim.World.NextUnitId++, rumour.Target) { OwnerId = 0, Role = UnitRole.Hauler });
        for (var trips = 0; trips < 100 && sim.World.Structures.ContainsKey(rumour.Target); trips++)
        {
            looter.Cargo.Clear();
            var what = ruin.Holdings.Keys.First();
            CacheLooting.TryLoot(sim, looter, what);
        }
        Assert.False(sim.World.Structures.ContainsKey(rumour.Target));
        Assert.Equal(OmenState.Fulfilled, rumour.State);
    }

    [Fact]
    public void ARuinEmptiedByPlainLoading_IsGone_AndTheRumourEnds()
    {
        // Bandits steal with LoadCargo, not the loot verb; anyone may take the
        // ruin, and whoever empties it, it is gone.
        var sim = MakeSim();
        sim.World.RestoreProgressionConfig(Cfg with { FarHorizonsTiles = 1 });
        sim.World.Explored[0] = new HashSet<TileCoord> { Seat };
        Progression.Check(sim, 0);
        var rumour = Assert.IsType<Omen>(OmenOf(sim, OmenKind.Rumour));
        var ruin = (Cache)sim.World.Structures[rumour.Target];

        var thief = sim.World.AddUnit(new Unit(sim.World.NextUnitId++, rumour.Target)
            { OwnerId = BanditConstants.OwnerId, Role = UnitRole.Bandit });
        for (var trips = 0; trips < 100 && sim.World.Structures.ContainsKey(rumour.Target); trips++)
        {
            thief.Cargo.Clear();
            sim.SubmitIntent(sim.Now, new LoadCargoIntent(thief.Id, ruin.Holdings.Keys.First())
                { PlayerId = BanditConstants.OwnerId });
            sim.Run(until: sim.Now);
        }

        Assert.False(sim.World.Structures.ContainsKey(rumour.Target));
        Assert.Equal(OmenState.Fulfilled, rumour.State);
    }

    // -------- determinism --------

    [Fact]
    public void ASnapshotWithEveryKindOfOmen_RestoresAndRunsTheSame()
    {
        var sim = MakeSim();
        var raid = LandTheRaid(sim);
        foreach (var id in raid.PartyIds.ToList()) CombatRules.OnUnitDeath(sim, sim.World.Units[id]);
        var known = new HashSet<TileCoord>();
        for (var i = 0; i < Cfg.FarHorizonsTiles; i++) known.Add(new TileCoord(i % 80, 79 - i / 80));
        sim.World.Explored[0] = known;
        Progression.Check(sim, 0);
        Assert.NotNull(OmenOf(sim, OmenKind.Refugees));
        Assert.NotNull(OmenOf(sim, OmenKind.Rumour));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var until = OmenOf(sim, OmenKind.Refugees)!.DueTick + 2 * Time.Day;
        sim.Run(until: until);
        restored.Run(until: until);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
