using Sim.Core;
using Sim.Core.Bandits;
using Sim.Core.Caches;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Progression;
using Sim.Core.Scouting;
using Sim.Core.World;
using Sim.Server.Bandits;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M39 (docs/bandit-camps.md): a bandit camp is raised by "Smoke on the horizon"
// (population), stands in the owner's wildest direction with a garrison and a
// hoard, regrows its garrison, musters raids on a schedule, and its raiders
// carry their loot home. Razing it spills the hoard, credits the razers, ends
// the rumour, and fires "The camp burns". Every number comes from CampConfig /
// ProgressionConfig.
public class CampTests
{
    private static readonly TileCoord Seat = new(40, 40);

    private static CampConfig Cc => new();
    private static ProgressionConfig Pc => new();

    private static Simulation MakeSim()
    {
        var world = new GameWorld(new TileGrid(80, 80, Biome.Grassland));
        world.Players[0] = new Player(0) { Progress = new ProgressLedger() };
        world.Players[BanditConstants.OwnerId] = new Player(BanditConstants.OwnerId);
        var castle = world.AddStructure(new Castle(Seat) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 400);
        return new Simulation(world, seed: 1);
    }

    private static void AddPeople(Simulation sim, int n, TileCoord at)
    {
        for (var i = 0; i < n; i++) sim.World.AddUnit(new Unit(sim.World.NextUnitId++, at) { OwnerId = 0 });
    }

    // Raise the camp the way the game does: population reaches the mark.
    private static BanditCamp Raise(Simulation sim)
    {
        AddPeople(sim, Pc.SmokePopulation, Seat);
        Progression.Check(sim, 0);
        return Assert.Single(sim.World.Structures.Values.OfType<BanditCamp>());
    }

    private static int Cheb(TileCoord a, TileCoord b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static List<Unit> Bandits(Simulation sim) =>
        sim.World.Units.Values.Where(u => u.OwnerId == BanditConstants.OwnerId).ToList();

    // -------- raising --------

    [Fact]
    public void OneShortOfTheMark_NoCamp()
    {
        var sim = MakeSim();
        AddPeople(sim, Pc.SmokePopulation - 1, Seat);
        Progression.Check(sim, 0);
        Assert.Empty(sim.World.Structures.Values.OfType<BanditCamp>());
    }

    [Fact]
    public void AtTheMark_ACampStands_OutInTheWilds_GarrisonedAndStocked_AndIsRumoured()
    {
        var sim = MakeSim();
        var camp = Raise(sim);

        Assert.Equal(BanditConstants.OwnerId, camp.OwnerId);
        Assert.Equal(0, camp.TargetOwnerId);
        Assert.Equal(MilestoneCatalog.SmokeOnTheHorizon, camp.SourceMilestoneId);
        Assert.InRange(Cheb(camp.At, Seat), Cc.MinDistance, Cc.MaxDistance);
        Assert.Equal(StructureCatalog.Spec(StructureKind.BanditCamp).BaseHealth, camp.Health);
        Assert.Equal(Cc.HoardStartIron, camp.AmountOf(Resource.Bronze));
        Assert.Equal(Cc.HoardStartOre, camp.AmountOf(Resource.CopperOre));
        Assert.Equal(Cc.GarrisonStart, Camps.Garrison(sim.World, camp).Count);

        var omen = Assert.Single(sim.World.Omens.Values, o => o.Kind == OmenKind.Camp);
        Assert.Equal(OmenState.Arrived, omen.State);
        Assert.Equal(camp.At, omen.Target);
        Assert.Equal(omen.From, Omens.SectorOf(camp.At.X - Seat.X, camp.At.Y - Seat.Y));
        var dx = camp.At.X - omen.AreaCenter.X;
        var dy = camp.At.Y - omen.AreaCenter.Y;
        Assert.True(dx * dx + dy * dy <= omen.AreaRadius * omen.AreaRadius);
        Assert.Equal(sim.Now + Cc.FirstRaidDelayTicks, omen.DueTick);

        Assert.Equal(SecretHint.Smoke, Charts.HintFor(StructureKind.BanditCamp));
    }

    // -------- the camp's life --------

    [Fact]
    public void TheGarrison_RegrowsOneARecruitPeriod_ToTheCap_NeverAbove()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        foreach (var id in Camps.Garrison(sim.World, camp).Take(2))
            CombatRules.OnUnitDeath(sim, sim.World.Units[id]);
        Assert.Equal(Cc.GarrisonStart - 2, Camps.Garrison(sim.World, camp).Count);

        sim.Run(until: sim.Now + Cc.RecruitPeriodTicks);
        Assert.Equal(Cc.GarrisonStart - 1, Camps.Garrison(sim.World, camp).Count);

        // Well past two more periods, but before the first raid.
        sim.Run(until: sim.Now + 2 * Cc.RecruitPeriodTicks);
        Assert.True(sim.Now < camp.LastRaidTick + Cc.RaidPeriodTicks);
        Assert.Equal(Cc.GarrisonCap, Camps.Garrison(sim.World, camp).Count);
    }

    [Fact]
    public void ARaid_IsMustered_OnSchedule_TheLowestIdsRide()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        var firstRaid = camp.LastRaidTick + Cc.RaidPeriodTicks;
        var garrison = Camps.Garrison(sim.World, camp);

        sim.Run(until: firstRaid - 1);
        Assert.Empty(camp.Raiders);

        sim.Run(until: firstRaid + Cc.TickPeriodTicks);
        Assert.Equal(garrison.Take(Cc.RaidSize), camp.Raiders);
        Assert.Equal(Cc.GarrisonStart - Cc.RaidSize, Camps.Garrison(sim.World, camp).Count);
    }

    [Fact]
    public void AFatCamp_DoesNotRaid()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        camp.Deposit(Resource.Food, Cc.HoardCap);
        sim.Run(until: camp.LastRaidTick + Cc.RaidPeriodTicks + 2 * Cc.TickPeriodTicks);
        Assert.Empty(camp.Raiders);
    }

    [Fact]
    public void AThinGarrison_DoesNotRaid()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        // Leave one fewer than a raid plus the home guard, and stop recruiting from refilling it in time.
        var keep = Cc.RaidSize + Cc.MinHome - 1;
        foreach (var id in Camps.Garrison(sim.World, camp).Skip(keep))
            CombatRules.OnUnitDeath(sim, sim.World.Units[id]);
        camp.LastRecruitTick = long.MaxValue / 2;   // no recruits in this window
        sim.Run(until: camp.LastRaidTick + Cc.RaidPeriodTicks + 2 * Cc.TickPeriodTicks);
        Assert.Empty(camp.Raiders);
    }

    // -------- the raid, played by the driver --------

    [Fact]
    public void Raiders_RideOut_Steal_AndCarryTheLootHome()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        // Only the camp's raiders act: no random parties.
        var driver = new BanditDriver(new BanditConfig { ThinkPeriodTicks = Time.Hour, SpawnGraceTicks = long.MaxValue, Seed = 7 });
        // The people who drew the camp leave (nobody fights at the seat, and
        // nobody eats the castle's food: every unit of it lost is stolen).
        foreach (var u in sim.World.Units.Values.Where(u => u.OwnerId == 0).ToList()) CombatRules.OnUnitDeath(sim, u);
        var castle = (Castle)sim.World.Structures[Seat];
        var foodBefore = castle.AmountOf(Resource.Food);
        var hoardBefore = camp.TotalHeld();

        var until = camp.LastRaidTick + Cc.RaidPeriodTicks + 12 * Time.Day;
        for (var t = sim.Now; t <= until; t += Time.Hour)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
        }

        // Two raids ride in that window (a week apart), each carrying a full
        // load home: 3 raiders x 15.
        var stolen = foodBefore - castle.AmountOf(Resource.Food);
        Assert.Equal(2 * Cc.RaidSize * Sim.Core.Logistics.UnitCargoCatalog.BanditCapacity, stolen);
        Assert.Equal(hoardBefore + stolen, camp.TotalHeld());
        Assert.Equal(Cc.GarrisonStart, Camps.Garrison(sim.World, camp).Count + camp.Raiders.Count(sim.World.Units.ContainsKey));
    }

    // -------- razing --------

    [Fact]
    public void RazingTheCamp_SpillsTheHoard_CreditsTheRazer_AndTheCaptivesComeHome()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        var at = camp.At;
        var hoard = camp.Holdings.ToDictionary(kv => kv.Key, kv => kv.Value);
        // Eight soldiers walk in (8 bare soldiers beat a garrison of 6 with 6 standing).
        for (var i = 0; i < 8; i++)
            sim.World.AddUnit(new Unit(sim.World.NextUnitId++, at) { OwnerId = 0, Role = UnitRole.Soldier });
        CombatTrigger.MaybeBeginCombatOnTile(sim, at);

        sim.Run(until: sim.Now + 3 * Time.Day);

        Assert.IsType<Rubble>(sim.World.Structures[at]);
        Assert.Empty(Camps.Garrison(sim.World, camp));
        Assert.True(sim.World.GroundResources.TryGetValue(at, out var pile));
        foreach (var (r, n) in hoard) Assert.Equal(n, pile!.GetValueOrDefault(r));

        var ledger = sim.World.Players[0].Progress!;
        Assert.Equal(1, ledger.Count(ProgressKey.CampRazed(MilestoneCatalog.SmokeOnTheHorizon)));
        Assert.True(ledger.HasFired(MilestoneCatalog.TheCampBurns));
        Assert.Equal(OmenState.Fulfilled, sim.World.Omens.Values.Single(o => o.Kind == OmenKind.Camp).State);
        var captives = sim.World.Omens.Values.Single(o => o.Kind == OmenKind.Refugees && o.SourceMilestoneId == MilestoneCatalog.TheCampBurns);
        Assert.Equal(Pc.CampCaptives, captives.Size);
    }

    // -------- the wire --------

    [Fact]
    public void TheWire_TellsTheOwnerAboutTheCamp_ButNotWhereItIs_UntilItIsInSight()
    {
        var build = Sim.Server.WorldFactory.Build(new Sim.Server.ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = true });
        var sim = new Simulation(build.Spec, seed: 1);
        var projector = new Sim.Server.ViewProjector(build);
        Omens.RaiseCamp(sim, sim.World.Players[0], MilestoneCatalog.SmokeOnTheHorizon);
        var camp = Assert.Single(sim.World.Structures.Values.OfType<BanditCamp>());
        var omen = Assert.Single(sim.World.Omens.Values, o => o.Kind == OmenKind.Camp);

        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var row = Assert.Single(mine.Omens);
        Assert.Equal((int)OmenKind.Camp, row.Kind);
        Assert.Equal((-1, -1), (row.TargetX, row.TargetY));
        Assert.Equal((omen.AreaCenter.X, omen.AreaCenter.Y, omen.AreaRadius), (row.AreaX, row.AreaY, row.AreaRadius));
        Assert.Equal(omen.DueTick - sim.Now, row.TicksLeft);
        Assert.DoesNotContain(mine.Structures, st => st.X == camp.At.X && st.Y == camp.At.Y);
        Assert.Empty(projector.Project(sim, sim.Now, playerId: 1, reveal: false).Omens);

        // Someone of yours walks up to it: the camp is seen, health and all (burning it is a siege).
        sim.World.AddUnit(new Unit(sim.World.NextUnitId++, new TileCoord(camp.At.X + 1, camp.At.Y)) { OwnerId = 0 });
        var close = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var seen = Assert.Single(close.Structures, st => st.X == camp.At.X && st.Y == camp.At.Y);
        Assert.Equal((int)StructureKind.BanditCamp, seen.Kind);
        Assert.Equal(StructureCatalog.Spec(StructureKind.BanditCamp).BaseHealth, seen.MaxHealth);
        Assert.Equal(camp.Health, seen.Health);
    }

    // -------- the pressure fix --------

    [Fact]
    public void NobodysStructures_NoLongerDrawBandits()
    {
        var world = new GameWorld(new TileGrid(60, 60, Biome.Grassland));
        world.Players[BanditConstants.OwnerId] = new Player(BanditConstants.OwnerId);
        world.Players[CacheConstants.OwnerId] = new Player(CacheConstants.OwnerId);
        for (var i = 0; i < 10; i++)
            world.AddStructure(new Cache(new TileCoord(5 + 4 * i, 30)) { OwnerId = CacheConstants.OwnerId });
        var sim = new Simulation(world, seed: 1);
        var driver = new BanditDriver(new BanditConfig
            { StructuresPerParty = 1, MaxLiveParties = 4, ThinkPeriodTicks = Time.Hour, SpawnGraceTicks = 0, Seed = 7 });

        for (var t = 0L; t <= 2 * Time.Day; t += Time.Hour)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
        }
        Assert.Empty(Bandits(sim));
    }

    // -------- determinism --------

    [Fact]
    public void ASnapshotMidRaid_RunsOnTheSame()
    {
        var sim = MakeSim();
        var camp = Raise(sim);
        sim.Run(until: camp.LastRaidTick + Cc.RaidPeriodTicks + Cc.TickPeriodTicks);
        Assert.NotEmpty(camp.Raiders);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var until = sim.Now + 2 * Cc.RecruitPeriodTicks;
        sim.Run(until: until);
        restored.Run(until: until);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
