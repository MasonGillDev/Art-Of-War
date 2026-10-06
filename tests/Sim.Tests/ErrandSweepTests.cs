using Sim.Core;
using Sim.Core.Boats;
using Sim.Core.Caches;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// THE REST OF THE GOAL-SHAPING SWEEP: equip, loot, embark
// (docs/goal-shaped-intents.md §3.5).
//
// Each of these used to read its target out of the unit's FEET, so the player's
// one decision — arm him, take that cache, put them on that boat — decomposed
// into a march, a wait, and a second intent. These tests pin the conversion and,
// more importantly, the three different answers to "what happens when the
// precondition isn't met on arrival", because they are genuinely different:
//
//   equip  → WAITS (a shelf gets restocked)
//   loot   → DISSOLVES (a cache never refills)
//   embark → WAITS for the hull, DISSOLVES if it fills up first
public class ErrandSweepTests
{
    private static readonly TileCoord Keep = new(4, 4);
    private static readonly TileCoord Store = new(14, 4);
    private static readonly TileCoord CacheAt = new(4, 14);

    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 }).Deposit(Resource.Food, 100_000);

        var cfg = world.PopulationConfig;
        // A soldier at the keep — far from the store, which is the whole point.
        world.AddUnit(new Unit(1, Keep)
        {
            Role = UnitRole.Soldier, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear,
        });
        world.NextUnitId = 2;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 30);
    }

    private static Stockpile AddStore(Simulation sim, int swords)
    {
        var store = (Stockpile)sim.World.AddStructure(new Stockpile(Store) { OwnerId = 0 });
        if (swords > 0) store.Deposit(Resource.BronzeSword, swords);
        return store;
    }

    // ---- equip -------------------------------------------------------------

    [Fact]
    public void ASoldierArmedFromAcrossTheMap_WalksToTheStoreAndTakesTheSword()
    {
        var sim = BuildWorld();
        var store = AddStore(sim, swords: 1);

        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword, Store) { PlayerId = 0 });
        sim.Run(0);

        Assert.False(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Equal(GoalKind.Equip, sim.World.Units[1].Goal!.Kind);
        Assert.Empty(sim.World.Units[1].Buffs);

        sim.Run(10 * Time.Day);

        Assert.Equal(Store, sim.World.Units[1].Position);
        Assert.NotEmpty(sim.World.Units[1].Buffs);
        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(0, store.AmountOf(Resource.BronzeSword));
    }

    [Fact]
    public void AnEmptyArmouryIsAWait_AndTheDeliveryArmsHim()
    {
        // Scarcity is a precondition, not a rejection. A sword that does not
        // exist yet may be forged or hauled, so he stands in the storehouse
        // until one arrives — and the deposit itself is the wake-up.
        var sim = BuildWorld();
        var store = AddStore(sim, swords: 0);

        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword, Store) { PlayerId = 0 });
        sim.Run(10 * Time.Day);

        Assert.Equal(Store, sim.World.Units[1].Position);
        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);
        Assert.Empty(sim.World.Units[1].Buffs);

        CargoTransfer.DepositInto(sim, store, Resource.BronzeSword, 1);

        Assert.NotEmpty(sim.World.Units[1].Buffs);
        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
    }

    [Fact]
    public void OneSwordAmongTwoHopefuls_ArmsExactlyOne()
    {
        var sim = BuildWorld();
        var store = AddStore(sim, swords: 0);
        var cfg = sim.World.PopulationConfig;
        sim.World.AddUnit(new Unit(2, Keep)
        {
            Role = UnitRole.Soldier, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear,
        });

        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword, Store) { PlayerId = 0 });
        sim.SubmitIntent(0, new EquipUnitIntent(2, Resource.BronzeSword, Store) { PlayerId = 0 });
        sim.Run(10 * Time.Day);
        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Waiting, sim.World.Units[2].Activity);

        CargoTransfer.DepositInto(sim, store, Resource.BronzeSword, 1);

        var armed = sim.World.Units.Values.Count(u => u.Buffs.Count > 0);
        Assert.Equal(1, armed);
        Assert.Equal(0, store.AmountOf(Resource.BronzeSword));
        // The other is still waiting, not dissolved — his sword may yet come.
        Assert.Contains(sim.World.Units.Values, u => u.Activity == Activity.Waiting);
    }

    [Fact]
    public void StandingInAnEmptyArmoury_StillRejects()
    {
        // The wait is what you opt into by naming a store from a distance. A
        // player looking straight at the shelf is entitled to be told it is
        // bare, not to have their soldier enrolled in an open-ended vigil.
        var sim = BuildWorld();
        AddStore(sim, swords: 0);
        sim.World.Units[1].Position = Store;

        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword) { PlayerId = 0 });
        sim.Run(0);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void AnItemHisRoleCannotHold_RejectsBeforeHeTakesAStep()
    {
        var sim = BuildWorld();
        var store = (Stockpile)sim.World.AddStructure(new Stockpile(Store) { OwnerId = 0 });
        store.Deposit(Resource.Bow, 1);

        // A Soldier cannot carry a Bow — impossible-forever, so it never
        // becomes an errand.
        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.Bow, Store) { PlayerId = 0 });
        sim.Run(0);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void TheStoreRazedMidWalk_DissolvesTheEquipErrand()
    {
        var sim = BuildWorld();
        var store = AddStore(sim, swords: 1);

        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword, Store) { PlayerId = 0 });
        sim.Run(0);
        SiegeDamage.RazeStructure(sim, store);
        sim.Run(sim.Now);

        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    // ---- loot --------------------------------------------------------------

    // CacheConstants.OwnerId, not the default 0. A cache belongs to NOBODY, and
    // a fixture that quietly gave it to player zero is what let a blanket
    // ownership check in GoalRules pass here while dissolving every loot errand
    // in a real game. Build the fixture the way the world builds it.
    private static Cache AddCache(Simulation sim, int wood)
    {
        var cache = (Cache)sim.World.AddStructure(
            new Cache(CacheAt) { OwnerId = CacheConstants.OwnerId });
        cache.Deposit(Resource.Wood, wood);
        return cache;
    }

    [Fact]
    public void ACacheAcrossTheFog_IsWalkedToAndLooted()
    {
        var sim = BuildWorld();
        AddCache(sim, wood: 5);

        sim.SubmitIntent(0, new LootCacheIntent(1, Resource.Wood, CacheAt) { PlayerId = 0 });
        sim.Run(0);
        Assert.Equal(GoalKind.Loot, sim.World.Units[1].Goal!.Kind);

        sim.Run(10 * Time.Day);

        Assert.Equal(CacheAt, sim.World.Units[1].Position);
        Assert.Equal(Resource.Wood, sim.World.Units[1].CargoResource);
        Assert.True(sim.World.Units[1].CargoAmount > 0);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void ARivalEmptiesItFirst_AndTheErrandDissolvesRatherThanWaiting()
    {
        // The difference between treasure and trade: a storehouse gets
        // restocked, a cache does not. Arriving to an emptied one ends the
        // errand — losing the race is a real outcome, and a silent wait for a
        // refill that can never come would be the stall the contract forbids.
        var sim = BuildWorld();
        var cache = AddCache(sim, wood: 5);

        sim.SubmitIntent(0, new LootCacheIntent(1, Resource.Wood, CacheAt) { PlayerId = 0 });
        sim.Run(0);

        cache.Withdraw(Resource.Wood, 5);          // somebody else got there first
        sim.World.Structures.Remove(CacheAt);
        sim.Run(10 * Time.Day);

        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(0, sim.World.Units[1].CargoAmount);
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    [Fact]
    public void AnUNOWNEDCacheIsStillLootable_TheRegressionThatOnlyALiveRunFound()
    {
        // The bug this pins: GoalRules dissolved any errand whose target was
        // not owned by the traveller, which is right for a workplace and wrong
        // for treasure. Caches carry CacheConstants.OwnerId (-2) precisely
        // because they belong to nobody, so every loot errand in a real game
        // walked the whole way and dissolved on arrival with "target no longer
        // ours" — while the tests passed, because their fixtures built caches
        // with the default owner 0 and player zero happened to match.
        //
        // Hence the explicit sentinel here, and hence the rule: a fixture that
        // is more convenient than the world is a fixture that hides bugs.

        var sim = BuildWorld();
        var cfg = sim.World.PopulationConfig;
        var hauler = sim.World.AddUnit(new Unit(9, Keep)
        { Role = UnitRole.Hauler, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        var cache = (Cache)sim.World.AddStructure(
            new Cache(CacheAt) { OwnerId = CacheConstants.OwnerId });
        cache.Deposit(Resource.Food, 60);
        Assert.NotEqual(0, CacheConstants.OwnerId);   // the whole point

        sim.SubmitIntent(0, new LootCacheIntent(9, Resource.Food, CacheAt) { PlayerId = 0 });
        sim.Run(0);
        var dispatched = sim.World.Units[9].Goal is not null;

        sim.Run(30 * Time.Day);
        var u = sim.World.Units[9];
        Assert.True(u.CargoAmount > 0,
            $"dispatched={dispatched} pos=({u.Position.X},{u.Position.Y}) act={u.Activity} " +
            $"goal={u.Goal?.Kind.ToString() ?? "none"} cargo={u.CargoResource}:{u.CargoAmount} " +
            $"cacheThere={sim.World.Structures.ContainsKey(CacheAt)} " +
            $"reasons=[{string.Join("|", sim.ResolvedLog.OfType<GoalDissolvedEvent>().Select(e => e.Reason))}]");
    }

    [Fact]
    public void AFullCarrier_IsRefusedBeforeItWalks()
    {
        // M36: cargo can be mixed, so carrying stone no longer blocks looting
        // wood. A carrier with no space left still can't, and is refused
        // before it sets off.
        var sim = BuildWorld();
        AddCache(sim, wood: 5);
        var carrier = sim.World.Units[1];
        carrier.Cargo.Add(Resource.Stone, carrier.CargoCapacity);

        sim.SubmitIntent(0, new LootCacheIntent(1, Resource.Wood, CacheAt) { PlayerId = 0 });
        sim.Run(0);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Null(sim.World.Units[1].Goal);
    }

    // ---- embark ------------------------------------------------------------
    //
    // Two moving parts, so two orders of arrival, and both must work.

    private static (Simulation sim, Dock dock, Unit boat) MakeHarbour(out TileCoord far)
    {
        var grid = new TileGrid(16, 8, Biome.Grassland);
        for (var y = 0; y < 8; y++)
            for (var x = 10; x < 16; x++) grid.SetBiome(new TileCoord(x, y), Biome.Water);

        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(1, 1)) { OwnerId = 0 })
             .Deposit(Resource.Food, 100_000);

        var dock = (Dock)world.AddStructure(
            new Dock(new TileCoord(9, 4), new TileCoord(10, 4)) { OwnerId = 0 });
        var boat = world.AddUnit(new Unit(50, dock.Slip)
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });

        far = new TileCoord(1, 4);
        var cfg = world.PopulationConfig;
        world.AddUnit(new Unit(1, far) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        world.NextUnitId = 60;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 16; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return (new Simulation(world, seed: 77), dock, boat);
    }

    [Fact]
    public void APassengerAcrossTheMap_WalksToTheQuayAndBoards()
    {
        var (sim, dock, boat) = MakeHarbour(out _);

        sim.SubmitIntent(0, new EmbarkIntent(boat.Id, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.Equal(GoalKind.Embark, sim.World.Units[1].Goal!.Kind);
        Assert.Equal(boat.Id, sim.World.Units[1].Goal!.PartnerUnitId);

        sim.Run(20 * Time.Day);

        Assert.Contains(1, boat.Passengers);
        Assert.True(sim.World.Units[1].IsEmbarked);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void ThePassengerWaitsOnTheQuayWhenTheHullIsElsewhere_AndBoardsWhenItDocks()
    {
        // The boat-arrives-last case. Without its own wake-up the passenger
        // would stand on the quay forever while the hull tied up beside them.
        var (sim, dock, boat) = MakeHarbour(out _);
        boat.Position = new TileCoord(14, 1);      // out at sea, not by the dock

        // The quay has to be NAMED here: a hull at sea is beside no dock, so
        // there is nothing to derive it from. That is the whole reason
        // EmbarkIntent takes an optional DockTile.
        sim.SubmitIntent(0, new EmbarkIntent(boat.Id, new[] { 1 }, dock.At) { PlayerId = 0 });
        sim.Run(20 * Time.Day);

        Assert.Equal(dock.At, sim.World.Units[1].Position);
        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);
        Assert.False(sim.World.Units[1].IsEmbarked);

        // Sail her in. Arrival boards whoever is queued.
        sim.SubmitIntent(sim.Now, new MoveIntent(boat.Id, dock.Slip) { PlayerId = 0 });
        sim.Run(sim.Now + 20 * Time.Day);

        Assert.Contains(1, boat.Passengers);
        Assert.True(sim.World.Units[1].IsEmbarked);
    }

    [Fact]
    public void PassengersOnTheQuayStillBoardAtomically()
    {
        // The immediate path keeps its all-or-nothing rule: a player looking at
        // a crew on the quay is entitled to "all of them, or tell me why".
        var (sim, dock, boat) = MakeHarbour(out _);
        var cfg = sim.World.PopulationConfig;
        sim.World.AddUnit(new Unit(2, dock.At) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        sim.World.AddUnit(new Unit(3, dock.At) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new EmbarkIntent(boat.Id, new[] { 2, 3 }) { PlayerId = 0 });
        sim.Run(0);

        Assert.Contains(2, boat.Passengers);
        Assert.Contains(3, boat.Passengers);
        Assert.Null(sim.World.Units[2].Goal);   // no errand: they were already there
    }

    [Fact]
    public void AMixedListBoardsTheQuaysideNowAndWalksTheRest()
    {
        var (sim, dock, boat) = MakeHarbour(out _);
        var cfg = sim.World.PopulationConfig;
        sim.World.AddUnit(new Unit(2, dock.At) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new EmbarkIntent(boat.Id, new[] { 1, 2 }) { PlayerId = 0 });
        sim.Run(0);

        Assert.Contains(2, boat.Passengers);               // was standing there
        Assert.NotNull(sim.World.Units[1].Goal);           // is walking
        Assert.False(sim.World.Units[1].IsEmbarked);

        sim.Run(20 * Time.Day);
        Assert.Contains(1, boat.Passengers);
    }

    [Fact]
    public void ThePassengerWhoArrivesToAFullHull_IsToldRatherThanLeftStanding()
    {
        var (sim, dock, boat) = MakeHarbour(out _);

        sim.SubmitIntent(0, new EmbarkIntent(boat.Id, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        // Others take every berth while our passenger is still walking.
        // PassengerCap is init-only, so the hull is filled rather than shrunk.
        var cfg = sim.World.PopulationConfig;
        for (var i = 0; i < boat.PassengerCap; i++)
        {
            var squatter = sim.World.AddUnit(new Unit(200 + i, dock.At)
            { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
            boat.Passengers.Add(squatter.Id);
            squatter.EmbarkedOn = boat.Id;
        }

        sim.Run(20 * Time.Day);

        Assert.False(sim.World.Units[1].IsEmbarked);
        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    // ---- persistence + determinism -----------------------------------------

    [Fact]
    public void AllThreeErrandsSurviveASnapshot()
    {
        var sim = BuildWorld();
        AddStore(sim, swords: 0);
        AddCache(sim, wood: 5);
        var cfg = sim.World.PopulationConfig;
        sim.World.AddUnit(new Unit(2, Keep) { Role = UnitRole.Hauler, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword, Store) { PlayerId = 0 });
        sim.SubmitIntent(0, new LootCacheIntent(2, Resource.Wood, CacheAt) { PlayerId = 0 });
        sim.Run(0);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 30);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(GoalKind.Equip, restored.World.Units[1].Goal!.Kind);
        Assert.Equal((int)Resource.BronzeSword, restored.World.Units[1].Goal!.Arg);
        Assert.Equal(GoalKind.Loot, restored.World.Units[2].Goal!.Kind);

        // And they still complete on the far side of the restart.
        restored.Run(10 * Time.Day);
        Assert.Equal(Resource.Wood, restored.World.Units[2].CargoResource);
        Assert.Equal(Activity.Waiting, restored.World.Units[1].Activity);
    }

    [Fact]
    public void TwinRun_OfTheEquipErrand_IsIdentical()
    {
        static string Run()
        {
            var sim = BuildWorld();
            AddStore(sim, swords: 1);
            sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.BronzeSword, Store) { PlayerId = 0 });
            sim.Run(10 * Time.Day);
            return Snapshot.Hash(sim);
        }

        Assert.Equal(Run(), Run());
    }
}
