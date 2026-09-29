using Sim.Core;
using Sim.Core.Bandits;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Landing;
using Sim.Core.World;
using Sim.Server.Bandits;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Two-act pacing — the landing (docs/two-act-pacing.md). On day X war bands come
// out of the fog for every kingdom: from tiles no player can see, far from every
// kingdom, each band from its own side; they gather in sight of the castle and
// strike together.
public class LandingHostTests
{
    private const long Landing = 500;
    private static readonly TileCoord SeatA = new(40, 40);
    private static readonly TileCoord SeatB = new(60, 20);

    private static GenesisSpec Spec(LandingConfig landing, int kingdoms = 2) => new()
    {
        Width = 80, Height = 80,
        FactionStarts = kingdoms == 1
            ? new[] { new FactionStartSpec { OwnerId = 0, CastlePosition = SeatA } }
            : new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = SeatA },
                new FactionStartSpec { OwnerId = 1, CastlePosition = SeatB },
            },
        Landing = landing,
    };

    // The spec-aware ctor: it is the one that schedules the landing at genesis.
    private static Simulation MakeSim(int kingdoms = 2) =>
        new(Spec(new LandingConfig(Landing), kingdoms), seed: 0x5EA);

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    [Fact]
    public void TheLanding_IsScheduledAtGenesis_AndComesOnItsTick()
    {
        var sim = MakeSim();
        Assert.NotNull(sim.World.LandingSeq);

        sim.Run(until: Landing - 1);
        Assert.Empty(sim.World.LandingHosts);

        sim.Run(until: Landing);
        Assert.Null(sim.World.LandingSeq);
        Assert.Equal(new[] { 0, 1 }, sim.World.LandingHosts.Keys);
        foreach (var host in sim.World.LandingHosts.Values)
            Assert.Equal(Landing + sim.World.LandingConfig.AssaultDelayTicks, host.AssaultTick);
    }

    [Fact]
    public void AOneActWorld_HasNoLanding()
    {
        var sim = new Simulation(Spec(default), seed: 0x5EA);
        Assert.Null(sim.World.LandingSeq);
        sim.Run(until: Landing + 10);
        Assert.Empty(sim.World.LandingHosts);
    }

    // The user's rule: they come from the fog, and it must be fog for EVERYONE,
    // so they never appear beside another kingdom.
    [Fact]
    public void EveryBand_ComesOutOfFogNoPlayerCanSee_FarFromEveryKingdom()
    {
        var sim = MakeSim();
        sim.Run(until: Landing);
        var world = sim.World;
        var cfg = world.LandingConfig;

        foreach (var host in world.LandingHosts.Values)
        {
            Assert.Equal(cfg.Fronts, host.Fronts.Count);
            foreach (var front in host.Fronts)
            {
                Assert.False(BanditRules.IsSeenByAnyPlayer(world, front.Landed));
                foreach (var u in world.Units.Values.Where(u => u.OwnerId >= 0))
                    Assert.True(Chebyshev(u.Position, front.Landed) >= BanditConstants.MinSpawnDistance);
                foreach (var s in world.Structures.Values.Where(s => s.OwnerId >= 0))
                    Assert.True(Chebyshev(s.At, front.Landed) >= BanditConstants.MinSpawnDistance);

                var d = Chebyshev(host.Seat, front.Landed);
                Assert.InRange(d, cfg.MinDistance, cfg.MaxDistance);

                Assert.Equal(cfg.BandSize, front.UnitIds.Count);
                foreach (var id in front.UnitIds)
                {
                    var u = world.Units[id];
                    Assert.Equal(UnitRole.Bandit, u.Role);
                    Assert.Equal(BanditConstants.OwnerId, u.OwnerId);
                    Assert.Equal(front.Landed, u.Position);
                }
            }
        }
    }

    // Each band has its own side of the castle: the board opens a fight on the
    // edge facing where a unit came from, so different sides are different fronts.
    [Fact]
    public void TheBands_ComeFromDifferentSides()
    {
        var sim = MakeSim();
        sim.Run(until: Landing);

        foreach (var host in sim.World.LandingHosts.Values)
        {
            Assert.Equal(host.Fronts.Count, host.Fronts.Select(f => f.Approach).Distinct().Count());
            foreach (var f in host.Fronts)
            {
                var dx = f.Approach.X - host.Seat.X;
                var dy = f.Approach.Y - host.Seat.Y;
                Assert.Equal(1, Math.Abs(dx) + Math.Abs(dy));   // an orthogonal neighbour
                // They gather StagingDistance out along that same side.
                var d = sim.World.LandingConfig.StagingDistance;
                Assert.Equal(new TileCoord(host.Seat.X + dx * d, host.Seat.Y + dy * d), f.Staging);
                // And come out of the fog on that side of the castle.
                Assert.True((f.Landed.X - host.Seat.X) * dx + (f.Landed.Y - host.Seat.Y) * dy > 0);
            }
        }
    }

    [Fact]
    public void ADefeatedKingdom_GetsNoHost()
    {
        var sim = MakeSim();
        sim.World.Players[1].Defeated = true;
        sim.Run(until: Landing);

        Assert.Equal(new[] { 0 }, sim.World.LandingHosts.Keys);
    }

    // THE HEADLINE: a landing still to come survives a snapshot. The regenerated
    // event fires on the same tick with the same Seq, raising the same hosts.
    [Fact]
    public void ALandingStillToCome_SurvivesASnapshot()
    {
        var straight = MakeSim();
        straight.Run(until: Landing + 300);

        var early = MakeSim();
        early.Run(until: Landing - 100);
        var restored = Snapshot.Restore(Snapshot.Serialize(early), seed: 0x5EA);
        Assert.Equal(early.World.LandingSeq, restored.World.LandingSeq);
        restored.Run(until: Landing + 300);

        Assert.Equal(Snapshot.Hash(straight), Snapshot.Hash(restored));
    }

    [Fact]
    public void AfterTheLanding_ASnapshotKeepsEveryBand()
    {
        var sim = MakeSim();
        sim.Run(until: Landing + 10);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0x5EA);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Null(restored.World.LandingSeq);
        foreach (var (id, host) in sim.World.LandingHosts)
        {
            var back = restored.World.LandingHosts[id];
            Assert.Equal(host.Seat, back.Seat);
            Assert.Equal(host.AssaultTick, back.AssaultTick);
            Assert.Equal(host.Fronts.Select(f => (f.Landed, f.Approach, f.Staging)),
                         back.Fronts.Select(f => (f.Landed, f.Approach, f.Staging)));
            Assert.Equal(host.Fronts.SelectMany(f => f.UnitIds), back.Fronts.SelectMany(f => f.UnitIds));
        }
    }

    [Fact]
    public void ADeadBandit_LeavesItsBand()
    {
        var sim = MakeSim();
        sim.Run(until: Landing);
        var front = sim.World.LandingHosts[0].Fronts[0];
        var victim = front.UnitIds[0];

        CombatRules.OnUnitDeath(sim, sim.World.Units[victim]);

        Assert.DoesNotContain(victim, front.UnitIds);
        Assert.False(LandingRules.IsHostBound(sim.World, victim));
    }

    [Fact]
    public void TheLanding_IsDeterministic()
    {
        string Run()
        {
            var sim = MakeSim();
            sim.Run(until: Landing + 200);
            return Snapshot.Hash(sim);
        }

        Assert.Equal(Run(), Run());
    }

    // The driver (Sim.Server): bands march to their gathering points and hold
    // there, and only at the assault tick do they move on the castle, each onto
    // it through its own side.
    [Fact]
    public void TheBands_GatherInSight_ThenStrikeTogether_EachFromItsOwnSide()
    {
        var sim = MakeSim(kingdoms: 1);
        // No ordinary bandit parties: only the landing moves.
        var driver = new BanditDriver(new BanditConfig { MaxLiveParties = 0, SpawnGraceTicks = long.MaxValue });

        var lastPos = new Dictionary<int, TileCoord>();
        var enteredFrom = new Dictionary<int, TileCoord>();   // unit -> tile it stepped onto the castle from
        long firstOnSeat = long.MaxValue;
        LandingHost? host = null;
        var assault = 0L;
        var atStagingBeforeAssault = new HashSet<int>();

        for (long t = 1; t <= Landing + 3 * Time.Day; t++)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            if (host is null && sim.World.LandingHosts.TryGetValue(0, out var h)) { host = h; assault = h.AssaultTick; }
            if (host is null) continue;

            foreach (var f in host.Fronts)
                foreach (var id in f.UnitIds)
                {
                    if (!sim.World.Units.TryGetValue(id, out var u)) continue;
                    if (t == assault - 1 && u.Position == f.Staging) atStagingBeforeAssault.Add(id);
                    if (u.Position == host.Seat && !enteredFrom.ContainsKey(id))
                    {
                        enteredFrom[id] = lastPos[id];
                        firstOnSeat = Math.Min(firstOnSeat, t);
                    }
                    lastPos[id] = u.Position;
                }
        }

        Assert.NotNull(host);
        var bandits = host!.Fronts.SelectMany(f => f.UnitIds).ToList();
        // Every band had gathered at its point before the assault.
        Assert.Equal(bandits.OrderBy(i => i), atStagingBeforeAssault.OrderBy(i => i));
        // Nobody touched the castle before the strike.
        Assert.True(firstOnSeat >= assault, $"first on the castle at {firstOnSeat}, assault {assault}");
        // Every band reached it, each through its own approach tile.
        foreach (var f in host.Fronts)
            foreach (var id in f.UnitIds)
            {
                Assert.True(enteredFrom.ContainsKey(id), $"bandit {id} never reached the castle");
                Assert.Equal(f.Approach, enteredFrom[id]);
            }
    }
}
