using Sim.Core.Bandits;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Population;
using Sim.Core.Progression;
using Sim.Core;
using Sim.Core.World;
using Sim.Server.Bandits;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M37 Phase B (docs/progression.md): the reprisal. Training enough soldiers
// raises an omen with a countdown; at its due tick raiders appear out of the
// fog in the announced direction, past the bandit distance floor; the bandit
// driver marches them on the seat; the raid resolves Repelled or Lost and the
// outcome is counted against the milestone. Every number comes from
// ProgressionConfig.
public class OmenTests
{
    private static readonly TileCoord Seat = new(40, 40);
    private static readonly TileCoord BarracksAt = new(42, 40);

    private static ProgressionConfig Cfg => new();

    private static Simulation MakeSim(bool enrolled = true)
    {
        var world = new GameWorld(new TileGrid(80, 80, Biome.Grassland));
        world.Players[0] = new Player(0) { Progress = enrolled ? new ProgressLedger() : null };
        world.Players[BanditConstants.OwnerId] = new Player(BanditConstants.OwnerId);
        world.AddStructure(new Castle(Seat) { OwnerId = 0 });
        world.AddStructure(new Barracks(BarracksAt) { OwnerId = 0 });
        return new Simulation(world, seed: 1);
    }

    // Train `n` soldiers at the barracks, one after another.
    private static void Train(Simulation sim, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var id = sim.World.NextUnitId++;
            var u = sim.World.AddUnit(new Unit(id, BarracksAt) { OwnerId = 0 });
            Assert.True(TrainingRules.Train(sim, u, UnitRole.Soldier));
        }
    }

    // The raid. (Other rows may raise their own omens alongside: exploring far
    // brings a rumour, beating the raid brings refugees.)
    private static Omen TheOmen(Simulation sim) =>
        Assert.Single(sim.World.Omens.Values, o => o.Kind == OmenKind.Raid);

    // -------- the announcement --------

    [Fact]
    public void OneShortOfTheMark_NothingIsAnnounced()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained - 1);
        Assert.Empty(sim.World.Omens);
    }

    [Fact]
    public void TheMark_AnnouncesARaid_WithItsWarning()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);

        var omen = TheOmen(sim);
        Assert.Equal(OmenKind.Raid, omen.Kind);
        Assert.Equal(OmenState.Pending, omen.State);
        Assert.Equal(0, omen.OwnerId);
        Assert.Equal(Seat, omen.Target);
        Assert.Equal(MilestoneCatalog.Reprisal, omen.SourceMilestoneId);
        Assert.Equal(Math.Min(Cfg.ReprisalRaidSize, BanditConstants.MaxPartySize), omen.Size);
        Assert.Equal(sim.Now + Cfg.ReprisalWarningTicks, omen.DueTick);
        Assert.True(sim.World.Players[0].Progress!.HasFired(MilestoneCatalog.Reprisal));
    }

    [Fact]
    public void TrainingMore_AndLosingSoldiers_NeverRaisesASecond()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        foreach (var u in sim.World.Units.Values.Where(u => u.Role == UnitRole.Soldier).ToList())
            CombatRules.OnUnitDeath(sim, u);
        Train(sim, Cfg.ReprisalTrained);

        Assert.Single(sim.World.Omens);
    }

    [Fact]
    public void AnUnenrolledSeat_IsNeverThreatened()
    {
        var sim = MakeSim(enrolled: false);
        Train(sim, Cfg.ReprisalTrained * 2);
        Assert.Empty(sim.World.Omens);
    }

    [Fact]
    public void TheBearing_PointsAtTheWilds()
    {
        var sim = MakeSim();
        // The owner has seen everything except the south-west: that is where
        // the raiders must come from.
        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++)
                if (Omens.SectorOf(x - Seat.X, y - Seat.Y) != Bearing.SouthWest) explored.Add(new TileCoord(x, y));
        sim.World.Explored[0] = explored;

        Train(sim, Cfg.ReprisalTrained);
        Assert.Equal(Bearing.SouthWest, TheOmen(sim).From);
    }

    // -------- the arrival --------

    [Fact]
    public void AtTheDueTick_RaidersArrive_FromTheAnnouncedBearing_PastTheFloor()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        var omen = TheOmen(sim);

        sim.Run(until: omen.DueTick);

        Assert.Equal(OmenState.Arrived, omen.State);
        Assert.Equal(omen.Size, omen.PartyIds.Count);
        var at = sim.World.Units[omen.PartyIds[0]].Position;
        foreach (var id in omen.PartyIds)
        {
            var u = sim.World.Units[id];
            Assert.Equal(UnitRole.Bandit, u.Role);
            Assert.Equal(BanditConstants.OwnerId, u.OwnerId);
            Assert.Equal(at, u.Position);
        }
        Assert.Equal(omen.From, Omens.SectorOf(at.X - Seat.X, at.Y - Seat.Y));
        var ring = Math.Max(Math.Abs(at.X - Seat.X), Math.Abs(at.Y - Seat.Y));
        Assert.InRange(ring, Cfg.OmenSpawnMinDistance, Cfg.OmenSpawnMaxDistance);
        Assert.True(BanditRules.ChebyshevToNearestPlayerPresence(sim.World, at) >= BanditConstants.MinSpawnDistance);
        Assert.False(BanditRules.IsSeenByAnyPlayer(sim.World, at));
    }

    [Fact]
    public void ABlockedBearing_Slips_AndLandsOnceItClears()
    {
        var sim = MakeSim();
        // One ring only, so a single sentry out in the bearing covers it.
        sim.World.RestoreProgressionConfig(Cfg with { OmenSpawnMinDistance = 16, OmenSpawnMaxDistance = 16 });
        Train(sim, Cfg.ReprisalTrained);
        var omen = TheOmen(sim);
        var (dx, dy) = Step(omen.From);
        var sentry = sim.World.AddUnit(new Unit(sim.World.NextUnitId++,
            new TileCoord(Seat.X + 16 * dx, Seat.Y + 16 * dy)) { OwnerId = 0 });

        var firstDue = omen.DueTick;
        sim.Run(until: firstDue);
        Assert.Equal(OmenState.Pending, omen.State);
        Assert.Equal(1, omen.Slips);
        Assert.Equal(firstDue + Cfg.OmenSlipTicks, omen.DueTick);

        // The sentry goes home; the next attempt lands.
        CombatRules.OnUnitDeath(sim, sentry);
        sim.Run(until: omen.DueTick);
        Assert.Equal(OmenState.Arrived, omen.State);
    }

    [Fact]
    public void ABearingBlockedForGood_Fizzles_AfterItsSlips()
    {
        var sim = MakeSim();
        sim.World.RestoreProgressionConfig(Cfg with { OmenSpawnMinDistance = 16, OmenSpawnMaxDistance = 16 });
        Train(sim, Cfg.ReprisalTrained);
        var omen = TheOmen(sim);
        var (dx, dy) = Step(omen.From);
        sim.World.AddUnit(new Unit(sim.World.NextUnitId++,
            new TileCoord(Seat.X + 16 * dx, Seat.Y + 16 * dy)) { OwnerId = 0 });

        sim.Run(until: omen.DueTick + (Cfg.OmenMaxSlips + 1) * Cfg.OmenSlipTicks);

        Assert.Equal(Cfg.OmenMaxSlips, omen.Slips);
        Assert.Equal(OmenState.Fizzled, omen.State);
        Assert.DoesNotContain(sim.World.Units.Values, u => u.Role == UnitRole.Bandit);
    }

    // -------- the outcome --------

    [Fact]
    public void EveryRaiderDead_IsRepelled()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        var omen = TheOmen(sim);
        sim.Run(until: omen.DueTick);

        foreach (var id in omen.PartyIds.ToList())
            CombatRules.OnUnitDeath(sim, sim.World.Units[id]);

        Assert.Equal(OmenState.Repelled, omen.State);
        Assert.Equal(sim.Now, omen.ResolvedTick);
        var ledger = sim.World.Players[0].Progress!;
        Assert.Equal(1, ledger.Count(ProgressKey.Repelled(MilestoneCatalog.Reprisal)));
        Assert.Equal(0, ledger.Count(ProgressKey.Lost(MilestoneCatalog.Reprisal)));
    }

    [Fact]
    public void ARaiderGettingAwayWithLoot_IsLost()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        var omen = TheOmen(sim);
        sim.Run(until: omen.DueTick);

        var ids = omen.PartyIds.ToArray();
        CombatRules.OnUnitDeath(sim, sim.World.Units[ids[0]]);   // one falls
        sim.World.Units[ids[1]].Cargo.Add(Resource.Food, 5);      // one carries loot
        sim.SubmitIntent(sim.Now, new DespawnBanditPartyIntent(ids.Skip(1).ToArray())
            { PlayerId = BanditConstants.OwnerId });
        sim.Run(until: sim.Now);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied, sim.ResolvedLog[^1].Outcome.ToString());
        Assert.Equal(OmenState.Lost, omen.State);
        var ledger = sim.World.Players[0].Progress!;
        Assert.Equal(1, ledger.Count(ProgressKey.Lost(MilestoneCatalog.Reprisal)));
        Assert.Equal(0, ledger.Count(ProgressKey.Repelled(MilestoneCatalog.Reprisal)));
    }

    // -------- the driver --------

    [Fact]
    public void TheDriver_MarchesTheRaidersOnTheSeat()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        var omen = TheOmen(sim);
        sim.Run(until: omen.DueTick);
        var start = sim.World.Units[omen.PartyIds[0]].Position;
        int Dist(TileCoord t) => Math.Max(Math.Abs(t.X - Seat.X), Math.Abs(t.Y - Seat.Y));

        // No random parties: only the omen's raiders are on the map.
        var driver = new BanditDriver(new BanditConfig
        {
            ThinkPeriodTicks = Time.Hour, SpawnGraceTicks = long.MaxValue, Seed = 7,
        });
        for (var t = sim.Now; t <= omen.DueTick + 12 * Time.Hour; t += Time.Hour)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
        }

        var survivors = omen.PartyIds.Where(sim.World.Units.ContainsKey).ToList();
        Assert.NotEmpty(survivors);
        Assert.All(survivors, id => Assert.True(Dist(sim.World.Units[id].Position) < Dist(start),
            $"raider {id} did not close on the seat (from {Dist(start)} to {Dist(sim.World.Units[id].Position)})"));
    }

    // -------- determinism --------

    [Fact]
    public void TwinRuns_ThroughAWholeReprisal_HashTheSame()
    {
        string Run()
        {
            var sim = MakeSim();
            Train(sim, Cfg.ReprisalTrained);
            var omen = TheOmen(sim);
            sim.Run(until: omen.DueTick);
            foreach (var id in omen.PartyIds.ToList())
                CombatRules.OnUnitDeath(sim, sim.World.Units[id]);
            return Snapshot.Hash(sim);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ASnapshotMidCountdown_ArrivesTheSame()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        var due = TheOmen(sim).DueTick;
        sim.Run(until: due / 2);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        sim.Run(until: due);
        restored.Run(until: due);
        Assert.Equal(OmenState.Arrived, TheOmen(restored).State);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void ASnapshotMidRaid_ResolvesTheSame()
    {
        var sim = MakeSim();
        Train(sim, Cfg.ReprisalTrained);
        sim.Run(until: TheOmen(sim).DueTick);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        foreach (var s in new[] { sim, restored })
            foreach (var id in TheOmen(s).PartyIds.ToList())
                CombatRules.OnUnitDeath(s, s.World.Units[id]);
        Assert.Equal(1, restored.World.Players[0].Progress!.Count(ProgressKey.Repelled(MilestoneCatalog.Reprisal)));
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    private static (int dx, int dy) Step(Bearing b) => b switch
    {
        Bearing.North => (0, 1), Bearing.NorthEast => (1, 1), Bearing.East => (1, 0),
        Bearing.SouthEast => (1, -1), Bearing.South => (0, -1), Bearing.SouthWest => (-1, -1),
        Bearing.West => (-1, 0), _ => (-1, 1),
    };
}
