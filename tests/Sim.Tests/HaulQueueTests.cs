using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Hauling;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server.Hauling;

namespace Sim.Tests;

// M36 Phase B (docs/hauling-queue-and-routes.md): one pool of haulers, a
// round-robin queue of jobs, the front job takes the free hauler nearest its
// pickup and goes to the back.
public class HaulQueueTests
{
    private const int Cap = UnitCargoCatalog.HaulerCapacity;
    private static readonly TileCoord Src = new(2, 2);

    private static Simulation MakeSim(out GameWorld world)
    {
        world = new GameWorld(new TileGrid(24, 12, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 0x4A01);
    }

    private static Stockpile Pile(GameWorld world, TileCoord at, int wood = 0, int owner = 0)
    {
        var p = (Stockpile)world.AddStructure(new Stockpile(at) { OwnerId = owner });
        if (wood > 0) p.Deposit(Resource.Wood, wood);
        return p;
    }

    private static Unit Hauler(GameWorld world, int id, TileCoord at, int owner = 0) =>
        world.AddUnit(new Unit(id, at) { Role = UnitRole.Hauler, OwnerId = owner });

    private static int SetJob(Simulation sim, TileCoord src, TileCoord dst, int target,
        HaulJobKind kind = HaulJobKind.Standing, Resource r = Resource.Wood, int player = 0)
    {
        var id = sim.World.NextHaulJobId;
        var outcome = new SetHaulJobIntent(src, dst, r, kind, target) { PlayerId = player }.Resolve(sim);
        Assert.True(outcome.IsApplied, outcome.Reason);
        return id;
    }

    private static HaulingDriver Driver() => new(new HaulingConfig { ThinkPeriodTicks = 1 });

    // One think, then resolve what it submitted.
    private static void Think(HaulingDriver driver, Simulation sim)
    {
        driver.Think(sim, sim.Now);
        sim.Run(until: sim.Now);
    }

    // Think every tick until `until`, running the world between.
    private static void RunFor(HaulingDriver driver, Simulation sim, long until)
    {
        for (var t = sim.Now; t <= until; t++)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            sim.Run(until: t);
        }
    }

    private static List<int> QueueOrder(GameWorld world, int owner = 0) =>
        world.HaulJobs.Values.Where(j => j.OwnerId == owner)
            .OrderBy(j => j.QueueStamp).ThenBy(j => j.JobId).Select(j => j.JobId).ToList();

    [Fact]
    public void OneHauler_ServesHungryJobsInTurn_AndATakenJobGoesToTheBack()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 1000);
        var a = SetJob(sim, Src, Pile(world, new TileCoord(6, 2)).At, 500);
        var b = SetJob(sim, Src, Pile(world, new TileCoord(6, 4)).At, 500);
        var c = SetJob(sim, Src, Pile(world, new TileCoord(6, 6)).At, 500);
        var hauler = Hauler(world, 1, Src);
        var driver = Driver();

        var served = new List<int>();
        for (var trip = 0; trip < 4; trip++)
        {
            Think(driver, sim);
            served.Add(hauler.HaulPlan!.JobId);
            sim.Run();   // walk the trip out; the hauler ends idle at the dest
            // Walk home so every trip starts at the source (nearest-hauler
            // plays no part here; only the queue order does).
            sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(1, Src) { PlayerId = 0 });
            sim.Run();
        }

        Assert.Equal(new[] { a, b, c, a }, served);
    }

    [Fact]
    public void TakenJob_MovesToTheBack_ThePassedOverKeepTheirPlace()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 100);
        var empty = Pile(world, new TileCoord(10, 2));   // a source with nothing
        var a = SetJob(sim, empty.At, Pile(world, new TileCoord(6, 2)).At, 50);
        var b = SetJob(sim, Src, Pile(world, new TileCoord(6, 4)).At, 50);
        var c = SetJob(sim, Src, Pile(world, new TileCoord(6, 6)).At, 50);
        Hauler(world, 1, Src);
        var driver = Driver();

        Think(driver, sim);

        // a had nothing to lift and stays at the front; b was served and went
        // to the back; c waits for the next free hauler.
        Assert.Equal(new[] { a, c, b }, QueueOrder(world));
        Assert.Equal(HaulJobState.SourceEmpty, driver.Reports[a].State);
        Assert.Equal(HaulJobState.Dispatched, driver.Reports[b].State);
        Assert.Equal(HaulJobState.WaitingForHauler, driver.Reports[c].State);
    }

    [Fact]
    public void NoFreeHauler_NothingMoves()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 100);
        var a = SetJob(sim, Src, Pile(world, new TileCoord(6, 2)).At, 50);
        var b = SetJob(sim, Src, Pile(world, new TileCoord(6, 4)).At, 50);
        var driver = Driver();
        var logBefore = sim.IntentLog.Count;

        Think(driver, sim);

        Assert.Equal(new[] { a, b }, QueueOrder(world));
        Assert.Equal(logBefore, sim.IntentLog.Count);   // no intents at all
        Assert.Equal(HaulJobState.WaitingForHauler, driver.Reports[a].State);
        Assert.Equal(0, driver.Reports[a].Position);
    }

    [Fact]
    public void Need_CountsWhatIsOnTheWay_OneLoadShortSendsOneHauler()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var dst = Pile(world, new TileCoord(8, 2));
        var job = SetJob(sim, Src, dst.At, Cap);   // exactly one load short
        Hauler(world, 1, Src);
        Hauler(world, 2, Src);
        var driver = Driver();

        Think(driver, sim);
        // The next think (a tick later) sees the first trip on the way.
        var next = sim.Now + 1;
        driver.Think(sim, next);
        sim.Run(until: next);

        Assert.Single(world.Units.Values, u => u.HaulPlan is not null);
        Assert.Equal(HaulJobState.Satisfied, driver.Reports[job].State);
        Assert.Equal(Cap, driver.Reports[job].OnTheWay);
    }

    [Fact]
    public void HungryLine_TakesSeveralHaulersInOneThink_LastTripSizedToNeed()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var job = SetJob(sim, Src, Pile(world, new TileCoord(8, 2)).At, Cap + 5);
        Hauler(world, 1, Src);
        Hauler(world, 2, Src);
        Hauler(world, 3, Src);
        var driver = Driver();

        Think(driver, sim);

        var amounts = world.Units.Values.Where(u => u.HaulPlan is not null)
            .Select(u => u.HaulPlan!.Amount).OrderByDescending(a => a).ToArray();
        Assert.Equal(new[] { Cap, 5 }, amounts);   // no overshoot; third hauler stays free
        Assert.Equal(2, driver.Reports[job].Haulers);
    }

    [Fact]
    public void Pickup_IsServedByTheHaulerNearestTheSource()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var dst = Pile(world, new TileCoord(20, 2));
        // One load wanted, two haulers: only the one nearer the PICKUP goes,
        // even though the other stands next to the destination.
        SetJob(sim, Src, dst.At, Cap);
        var nearDest = Hauler(world, 1, new TileCoord(19, 2));
        var nearSrc = Hauler(world, 2, new TileCoord(4, 3));
        var driver = Driver();

        Think(driver, sim);

        Assert.NotNull(nearSrc.HaulPlan);
        Assert.Null(nearDest.HaulPlan);
    }

    [Fact]
    public void Nearest_TieBreaksOnYThenXThenId()
    {
        var sim = MakeSim(out var world);
        Pile(world, new TileCoord(10, 5), wood: 500);
        SetJob(sim, new TileCoord(10, 5), Pile(world, new TileCoord(20, 5)).At, Cap);
        // All three are 2 tiles away. (8,3) has the lowest y; ids are
        // deliberately reversed against position.
        Hauler(world, 3, new TileCoord(8, 3));
        Hauler(world, 2, new TileCoord(12, 3));
        Hauler(world, 1, new TileCoord(12, 7));
        var driver = Driver();

        Think(driver, sim);

        Assert.NotNull(world.Units[3].HaulPlan);
    }

    [Fact]
    public void SharedSource_IsNotCountedTwice()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 10);
        var a = SetJob(sim, Src, Pile(world, new TileCoord(8, 2)).At, 100);
        var b = SetJob(sim, Src, Pile(world, new TileCoord(8, 6)).At, 100);
        Hauler(world, 1, Src);
        Hauler(world, 2, Src);
        var driver = Driver();

        Think(driver, sim);

        Assert.Single(world.Units.Values, u => u.HaulPlan is not null);
        Assert.Equal(10, world.Units.Values.Single(u => u.HaulPlan is not null).HaulPlan!.Amount);
        Assert.Equal(HaulJobState.Dispatched, driver.Reports[a].State);
        Assert.Equal(HaulJobState.SourceEmpty, driver.Reports[b].State);
    }

    [Fact]
    public void OnceJob_DeliversItsTotal_ThenLeavesTheQueue()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var dst = Pile(world, new TileCoord(6, 2));
        var job = SetJob(sim, Src, dst.At, Cap + 7, HaulJobKind.Once);
        Hauler(world, 1, Src);
        var driver = Driver();

        RunFor(driver, sim, until: 2000);

        Assert.Equal(Cap + 7, dst.AmountOf(Resource.Wood));
        Assert.False(world.HaulJobs.ContainsKey(job));
    }

    [Fact]
    public void Pool_IsOnlyEmptyDormantHaulers()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var job = SetJob(sim, Src, Pile(world, new TileCoord(8, 2)).At, 100);
        world.AddUnit(new Unit(1, Src) { Role = UnitRole.Builder });              // not a hauler
        world.AddUnit(new Unit(2, Src) { Role = UnitRole.Hauler, Cargo = { { Resource.Ore, 1 } } }); // laden
        Hauler(world, 3, Src);
        world.Claims[3] = new Claim(3, OrderId: 99, ClaimPurpose.Crew);          // the AI substrate's
        Hauler(world, 4, Src, owner: 1);                                          // someone else's
        var driver = Driver();

        Think(driver, sim);

        Assert.All(world.Units.Values, u => Assert.Null(u.HaulPlan));
        Assert.Equal(HaulJobState.WaitingForHauler, driver.Reports[job].State);
    }

    // docs/citizen-hauling.md — untrained citizens of any age are the
    // overflow tier: taken only when no hauler is free, carrying their own 5.
    private static Unit Citizen(GameWorld world, int id, TileCoord at, int ageYears = 30) =>
        world.AddUnit(new Unit(id, at)
        {
            Role = UnitRole.None,
            BornTick = -(long)ageYears * world.PopulationConfig.TicksPerYear,
        });

    [Fact]
    public void NoHaulerFree_AnIdleCitizen_TakesTheTrip_AtCitizenCapacity()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        SetJob(sim, Src, Pile(world, new TileCoord(8, 2)).At, 100);
        var citizen = Citizen(world, 1, Src);

        Think(Driver(), sim);

        Assert.NotNull(citizen.HaulPlan);
        Assert.Equal(UnitCargoCatalog.DefaultCapacity, citizen.HaulPlan!.Amount);
    }

    [Fact]
    public void AFreeHauler_IsPreferred_OverANearerCitizen()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        SetJob(sim, Src, Pile(world, new TileCoord(8, 2)).At, Cap);   // one hauler load
        var citizen = Citizen(world, 1, Src);
        var hauler = Hauler(world, 2, new TileCoord(10, 8));

        Think(Driver(), sim);

        Assert.NotNull(hauler.HaulPlan);
        Assert.Null(citizen.HaulPlan);
    }

    [Fact]
    public void Children_HaulToo_ButTrainedWorkersDoNot()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var job = SetJob(sim, Src, Pile(world, new TileCoord(8, 2)).At, 100);
        var child = Citizen(world, 1, Src, ageYears: 1);
        var farmer = world.AddUnit(new Unit(2, Src) { Role = UnitRole.Farmer, BornTick = -30 * world.PopulationConfig.TicksPerYear });

        Think(Driver(), sim);

        Assert.NotNull(child.HaulPlan);
        Assert.Null(farmer.HaulPlan);
    }

    [Fact]
    public void LostEnd_LeavesTheJobUnserviceable()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var dst = Pile(world, new TileCoord(8, 2));
        var job = SetJob(sim, Src, dst.At, 100);
        world.Structures.Remove(dst.At);
        Hauler(world, 1, Src);
        var driver = Driver();

        Think(driver, sim);

        Assert.Null(world.Units[1].HaulPlan);
        Assert.Equal(HaulJobState.Unserviceable, driver.Reports[job].State);
    }

    [Fact]
    public void SetHaulJob_Validation()
    {
        var sim = MakeSim(out var world);
        var mine = Pile(world, Src, wood: 10);
        var other = Pile(world, new TileCoord(8, 2));
        var theirs = Pile(world, new TileCoord(8, 6), owner: 1);

        IntentOutcome Set(TileCoord s, TileCoord d, Resource r = Resource.Wood, int target = 10,
            HaulJobKind kind = HaulJobKind.Standing) =>
            new SetHaulJobIntent(s, d, r, kind, target) { PlayerId = 0 }.Resolve(sim);

        Assert.True(Set(mine.At, theirs.At).IsRejected);            // foreign destination
        Assert.True(Set(theirs.At, mine.At).IsRejected);            // foreign source
        Assert.True(Set(mine.At, mine.At).IsRejected);              // same tile
        Assert.True(Set(mine.At, new TileCoord(20, 10)).IsRejected);// no structure
        Assert.True(Set(mine.At, other.At, Resource.None).IsRejected);
        Assert.True(Set(mine.At, other.At, target: 0).IsRejected);
        Assert.True(Set(mine.At, other.At, kind: (HaulJobKind)9).IsRejected);

        for (var i = 0; i < HaulingConstants.MaxJobsPerPlayer; i++)
            Assert.True(Set(mine.At, other.At).IsApplied);
        Assert.True(Set(mine.At, other.At).IsRejected);             // cap
    }

    [Fact]
    public void HaulIntent_NamingAnotherPlayersJob_IsRejected()
    {
        var sim = MakeSim(out var world);
        Pile(world, new TileCoord(8, 6), wood: 50, owner: 1);
        var theirs = SetJob(sim, new TileCoord(8, 6), Pile(world, new TileCoord(12, 6), owner: 1).At,
            50, player: 1);
        Pile(world, Src, wood: 50);
        var dst = Pile(world, new TileCoord(6, 2));
        Hauler(world, 1, Src);

        var outcome = new HaulIntent(1, Src, dst.At, Resource.Wood, jobId: theirs) { PlayerId = 0 }
            .Resolve(sim);

        Assert.True(outcome.IsRejected);
    }

    [Fact]
    public void ClearedJob_InFlightTripStillLands_NotCredited()
    {
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 500);
        var dst = Pile(world, new TileCoord(6, 2));
        var job = SetJob(sim, Src, dst.At, 100, HaulJobKind.Once);
        Hauler(world, 1, Src);
        var driver = Driver();

        Think(driver, sim);
        Assert.True(new ClearHaulJobIntent(job) { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run();

        Assert.Equal(Cap, dst.AmountOf(Resource.Wood));
        Assert.Empty(world.HaulJobs);
    }

    // ---- found in play (2026-09-23) ---------------------------------------------

    [Fact]
    public void MovingAHauler_MidTrip_EndsTheHaul_ButKeepsTheCargo()
    {
        // A breed order walked a laden queue hauler into a house. The move won,
        // but the haul plan rode along: the hauler ended Idle holding a dead
        // plan, and the queue treated it as busy (and its cargo as on the way)
        // forever.
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 100);
        var dst = Pile(world, new TileCoord(20, 2));
        var hauler = Hauler(world, 1, Src);
        sim.SubmitIntent(0, new HaulIntent(1, Src, dst.At, Resource.Wood) { PlayerId = 0 });
        sim.Run(until: 1);
        Assert.Equal(Cap, hauler.CargoAmount);

        sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(1, new TileCoord(2, 9)) { PlayerId = 0 });
        sim.Run();

        Assert.Null(hauler.HaulPlan);
        Assert.Equal(new TileCoord(2, 9), hauler.Position);
        Assert.Equal(Cap, hauler.CargoAmount);          // not destroyed, not delivered
        Assert.Equal(0, dst.AmountOf(Resource.Wood));
    }

    [Fact]
    public void QueueTrip_RefusesAUnitAnOrderAlreadyClaimed()
    {
        // The substrate and the queue think in the same tick; the claim
        // resolves first, so the queue's trip must be refused, not share the body.
        var sim = MakeSim(out var world);
        Pile(world, Src, wood: 100);
        var dst = Pile(world, new TileCoord(8, 2));
        var job = SetJob(sim, Src, dst.At, 50);
        var hauler = Hauler(world, 1, Src);
        world.Claims[1] = new Claim(1, OrderId: 7, ClaimPurpose.InFlight);

        var outcome = new HaulIntent(1, Src, dst.At, Resource.Wood, amount: 10, jobId: job) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsRejected);
        Assert.Null(hauler.HaulPlan);
        // A manual haul (no job) is the player's word and still goes.
        Assert.True(new HaulIntent(1, Src, dst.At, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void SmelterInputs_CountAsStock_ForAJobKeepingOreThere()
    {
        var sim = MakeSim(out var world);
        var mine = Pile(world, Src);
        mine.Deposit(Resource.Ore, 100);
        var smelter = (Extractor)world.AddStructure(new Extractor(StructureKind.Smelter, new TileCoord(8, 2)) { OwnerId = 0 });
        smelter.DepositInput(Resource.Ore, 12);
        var job = SetJob(sim, Src, smelter.At, 12, r: Resource.Ore);
        Hauler(world, 1, Src);
        var driver = Driver();

        Think(driver, sim);

        Assert.Equal(HaulJobState.Satisfied, driver.Reports[job].State);
        Assert.Null(world.Units[1].HaulPlan);
    }

    // ---- headline: determinism ------------------------------------------------

    private static (Simulation sim, HaulingDriver driver) Busy()
    {
        var sim = MakeSim(out var world);
        Pile(world, new TileCoord(2, 2), wood: 2000);
        Pile(world, new TileCoord(2, 9), wood: 2000);
        SetJob(sim, new TileCoord(2, 2), Pile(world, new TileCoord(14, 2)).At, 300);
        SetJob(sim, new TileCoord(2, 9), Pile(world, new TileCoord(14, 9)).At, 300);
        SetJob(sim, new TileCoord(2, 2), Pile(world, new TileCoord(20, 5)).At, 120, HaulJobKind.Once);
        for (var i = 1; i <= 4; i++) Hauler(world, i, new TileCoord(i * 3, 5));
        return (sim, Driver());
    }

    [Fact]
    public void TwinRun_SameHash()
    {
        var (a, da) = Busy();
        var (b, db) = Busy();
        RunFor(da, a, until: 900);
        RunFor(db, b, until: 900);

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.NotEmpty(a.IntentLog.Where(e => e.Intent is RequeueHaulJobIntent));
    }

    [Fact]
    public void SnapshotMidQueue_RecoversToTheSameWorld()
    {
        var (live, dl) = Busy();
        RunFor(dl, live, until: 400);   // haulers mid-trip, jobs part-served

        var restored = Snapshot.Restore(Snapshot.Serialize(live), seed: 0x4A01);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));

        // Both carry on with a FRESH driver: the driver holds no state, so
        // the one that survived the restart and a new one must agree.
        RunFor(Driver(), live, until: 900);
        RunFor(Driver(), restored, until: 900);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));
    }
}
