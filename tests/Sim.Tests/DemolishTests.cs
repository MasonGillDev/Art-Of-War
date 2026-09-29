using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// DemolishStructureIntent (docs/demolish.md). Pins:
//   * owner-only, instant, no unit involved, no refund, NO rubble;
//   * the vault spills to the tile's ground pile (raze economy);
//   * workers and residents are released cleanly;
//   * claims free with the extractor and the fertility catch-up lands;
//   * a besieged fort's combat state closes; the tile is buildable at once;
//   * rejections: not yours, rubble, cache, site, castle, empty tile;
//   * determinism: snapshot round-trip and twin-run hash.
public class DemolishTests
{
    private static Simulation MakeSim(int size = 8)
    {
        var world = new GameWorld(new TileGrid(size, size, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 1);
    }

    private sealed class NoOpEvent : ScheduledEvent { public override void Apply(Simulation sim) { } }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    private static IntentOutcome Demolish(Simulation sim, TileCoord at, int player = 0) =>
        new DemolishStructureIntent(at) { PlayerId = player }.Resolve(sim);

    [Fact]
    public void Demolish_OwnStockpile_GoneNoRubble_HoldingsSpill_TileBuildableNow()
    {
        var sim = MakeSim();
        var at = new TileCoord(3, 3);
        var sp = sim.World.AddStructure(new Stockpile(at) { OwnerId = 0 });
        sp.Deposit(Resource.Wood, 40);

        Assert.True(Demolish(sim, at).IsApplied);

        Assert.False(sim.World.Structures.ContainsKey(at));
        Assert.Equal(40, sim.World.GroundResources[at][Resource.Wood]);
        // Same tick, the ground is yours to build on again.
        Assert.True(new PlaceSiteIntent(at, StructureKind.House) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void Demolish_Rejections_MutateNothing()
    {
        var sim = MakeSim();
        var w = sim.World;
        w.AddStructure(new Stockpile(new TileCoord(1, 1)) { OwnerId = 1 });
        w.AddStructure(new Rubble(new TileCoord(2, 2)) { OwnerId = SiegeConstants.RubbleOwnerId });
        w.AddStructure(new Castle(new TileCoord(3, 3)) { OwnerId = 0 });
        w.AddStructure(new ConstructionSite(new TileCoord(4, 4), StructureKind.House) { OwnerId = 0 });
        var before = Snapshot.Hash(sim);

        Assert.True(Demolish(sim, new TileCoord(1, 1)).IsRejected);      // not yours
        Assert.True(Demolish(sim, new TileCoord(2, 2)).IsRejected);      // rubble
        Assert.True(Demolish(sim, new TileCoord(3, 3)).IsRejected);      // castle
        Assert.True(Demolish(sim, new TileCoord(4, 4)).IsRejected);      // site under way
        Assert.True(Demolish(sim, new TileCoord(5, 5)).IsRejected);      // nothing there
        Assert.True(Demolish(sim, new TileCoord(9, 9)).IsRejected);      // out of bounds

        Assert.Equal(before, Snapshot.Hash(sim));
    }

    [Fact]
    public void Demolish_Cache_Rejected()
    {
        var sim = MakeSim();
        var at = new TileCoord(2, 2);
        sim.World.AddStructure(new Cache(at) { OwnerId = 0 });
        Assert.True(Demolish(sim, at).IsRejected);
        Assert.IsType<Cache>(sim.World.Structures[at]);
    }

    [Fact]
    public void Demolish_Extractor_ReleasesWorker_FreesClaim_SpillsBuffer()
    {
        var sim = MakeSim();
        var w = sim.World;
        var at = new TileCoord(3, 3);
        var claim = new TileCoord(4, 3);
        var farm = w.AddStructure(new Extractor(StructureKind.Farm, at) { OwnerId = 0 });
        farm.ClaimTiles.Add(claim);
        var u = w.AddUnit(new Unit(1, at) { Role = UnitRole.Farmer, OwnerId = 0 });
        u.TrySetActivity(Activity.Working, at);
        farm.Workers.Add(u.Id);
        farm.ArmIfDormant(sim);
        farm.Buffer = 7;

        Assert.True(Demolish(sim, at).IsApplied);

        Assert.Equal(Activity.Idle, u.Activity);
        Assert.Null(u.Assignment);
        Assert.Equal(7, w.GroundResources[at][Resource.Food]);
        Assert.Null(Claims.ClaimantAt(w, claim));
        Assert.True(new PlaceSiteIntent(claim, StructureKind.House) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void Demolish_ProducingExtractor_KeepsSoilDamage()
    {
        // Same anchor discipline as razing: the worked tile's fertility is
        // caught up under the producing rate BEFORE the extractor goes, so
        // the deviation it earned stays on the books.
        var sim = MakeSim(10);
        var w = sim.World;
        var cfg = w.BiomeDegradationConfig;
        var at = new TileCoord(5, 5);
        var claim = new TileCoord(6, 5);
        var farm = w.AddStructure(new Extractor(StructureKind.Farm, at) { OwnerId = 0 });
        farm.ClaimTiles.Add(claim);
        var u = w.AddUnit(new Unit(1, at) { Role = UnitRole.Farmer, OwnerId = 0 });
        u.TrySetActivity(Activity.Working, at);
        farm.Workers.Add(u.Id);
        farm.ArmIfDormant(sim);

        AdvanceTo(sim, 3 * cfg.DegradePeriod);
        Assert.True(Demolish(sim, at).IsApplied);

        Assert.True(w.Fertility.TryGetValue(claim, out var f));
        Assert.True(f.Deviation < 0, "expected the soil damage to persist, deviation=" + f.Deviation);
    }

    [Fact]
    public void Demolish_House_ResidentsGoHomeToCastle()
    {
        var sim = MakeSim();
        var w = sim.World;
        w.AddStructure(new Castle(new TileCoord(1, 1)) { OwnerId = 0 });
        var at = new TileCoord(4, 4);
        var house = w.AddStructure(new House(at) { OwnerId = 0 });
        var u = w.AddUnit(new Unit(1, at) { Role = UnitRole.Farmer, OwnerId = 0 });
        Sim.Core.Population.Population.SetHome(sim, u, at);
        Assert.Equal(1, house.ResidentCount);

        Assert.True(Demolish(sim, at).IsApplied);

        Assert.Null(u.Home);
        Assert.False(w.Structures.ContainsKey(at));
    }

    [Fact]
    public void Demolish_BesiegedWall_ClosesTheSiege()
    {
        var sim = MakeSim();
        var w = sim.World;
        w.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        var at = new TileCoord(4, 4);
        w.AddStructure(new Wall(at) { OwnerId = 0 });
        w.AddUnit(new Unit(1, new TileCoord(4, 5)) { Role = UnitRole.Soldier, OwnerId = 1 });
        // Open the adjacency siege the way an arrival does.
        Sim.Core.Fortifications.FortSiege.MaybeBeginSiegeAdjacentTo(sim, new TileCoord(4, 5));
        Assert.True(w.CombatStates.ContainsKey(at));

        Assert.True(Demolish(sim, at).IsApplied);

        Assert.False(w.CombatStates.ContainsKey(at));
        Assert.False(w.Structures.ContainsKey(at));
        sim.Run();   // the pending round stales out on its anchor; nothing throws
    }

    [Fact]
    public void Demolish_SnapshotRoundTrip_AndTwinRun()
    {
        static Simulation Run()
        {
            var sim = MakeSim();
            var sp = sim.World.AddStructure(new Stockpile(new TileCoord(2, 2)) { OwnerId = 0 });
            sp.Deposit(Resource.Wood, 5);
            sim.SubmitIntent(1, new DemolishStructureIntent(new TileCoord(2, 2)) { PlayerId = 0 });
            sim.SubmitIntent(2, new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.House) { PlayerId = 0 });
            sim.Run();
            return sim;
        }
        var a = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(Run()));
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(Snapshot.Restore(Snapshot.Serialize(a), seed: 1)));
        Assert.IsType<ConstructionSite>(a.World.Structures[new TileCoord(2, 2)]);
    }
}
