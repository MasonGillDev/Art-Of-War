using Sim.Core.Biomes;
using Sim.Core.Canals;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M21 Phase B — canals: an expensive whole-PATH build job that floods a chain
// of land tiles into Water (docs/canals.md). Validates extend-from-water
// placement, per-tile cost/time scaling, the completion flood (no resulting
// structure), reservation exclusion, the irrigation composition with Phase A
// (water restores degraded land), and mid-build persistence.
public class CanalsTests
{
    private static Simulation MakeSim(int size = 12, Biome biome = Biome.Grassland)
    {
        var grid = new TileGrid(size, size, biome);
        var world = new GameWorld(grid);
        return new Simulation(world, seed: 1);
    }

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    // Place a canal, deliver its (length-scaled) materials, staff it with the
    // required builders, and run it to completion. Returns the site (now
    // removed from the world — the path is Water).
    private static ConstructionSite PlaceAndCompleteCanal(Simulation sim, List<TileCoord> path)
    {
        var outcome = new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        var site = (ConstructionSite)sim.World.Structures[path[0]];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 1; i <= site.RequiredBuilderCount; i++)
        {
            var u = new Unit(i, path[0]) { Role = UnitRole.Builder };
            sim.World.AddUnit(u);
            u.TrySetActivity(Activity.Building, path[0]);
        }
        site.StartOrResume(sim);
        sim.Run();
        return site;
    }

    // ====================================================================
    // Placement validation (the "redirect water" rules)
    // ====================================================================

    [Fact]
    public void Canal_ExtendsFromWater_Completion_FloodsWholePath()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);   // source
        var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) };

        PlaceAndCompleteCanal(sim, path);

        // No resulting structure; every path tile is now Water.
        Assert.False(sim.World.Structures.ContainsKey(new TileCoord(1, 5)));
        foreach (var t in path)
            Assert.Equal(Biome.Water, sim.World.Grid.BiomeAt(t));
        // Builders released to Idle.
        Assert.All(sim.World.Units.Values, u => Assert.Equal(Activity.Idle, u.Activity));
    }

    [Fact]
    public void Canal_CostAndDuration_ScaleWithPathLength()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5), new(4, 5) }; // 4
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var site = (ConstructionSite)sim.World.Structures[new TileCoord(1, 5)];
        var spec = StructureCatalog.Spec(StructureKind.Canal);
        Assert.Equal(spec.BuildCost[Resource.Stone] * 4, site.Required[Resource.Stone]);
        Assert.Equal(spec.BuildCost[Resource.Wood] * 4, site.Required[Resource.Wood]);
        Assert.Equal(spec.BuildDurationTicks * 4, site.BuildDurationTicks);
    }

    [Fact]
    public void Canal_NotExtendingFromWater_Rejected()
    {
        var sim = MakeSim(); // all Grassland, no water anywhere
        var path = new List<TileCoord> { new(3, 5), new(4, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.False(sim.World.Structures.ContainsKey(new TileCoord(3, 5)));
    }

    [Fact]
    public void Canal_DisconnectedPath_Rejected()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var path = new List<TileCoord> { new(1, 5), new(3, 5) }; // gap: (1,5)→(3,5)
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Canal_OnStructure_Rejected()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        sim.World.AddStructure(new Castle(new TileCoord(2, 5)));
        var path = new List<TileCoord> { new(1, 5), new(2, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Canal_OnMountain_Rejected()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        sim.World.Grid.SetBiome(new TileCoord(2, 5), Biome.Mountain);
        var path = new List<TileCoord> { new(1, 5), new(2, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Canal_OnExistingWater_Rejected()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        sim.World.Grid.SetBiome(new TileCoord(1, 5), Biome.Water); // already water
        var path = new List<TileCoord> { new(1, 5), new(2, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Canal_OutOfBounds_Rejected()
    {
        var sim = MakeSim(size: 6);
        var path = new List<TileCoord> { new(10, 10) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Canal_OnClaimedTile_Rejected()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var farm = new Extractor(StructureKind.Farm, new TileCoord(5, 5));
        farm.ClaimTiles.Add(new TileCoord(2, 5));
        sim.World.AddStructure(farm);
        var path = new List<TileCoord> { new(1, 5), new(2, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void Canal_EmptyPath_Rejected()
    {
        var sim = MakeSim();
        Assert.True(new PlaceCanalIntent(new List<TileCoord>()) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    // ====================================================================
    // Reservation exclusion (in-flight canal tiles are territory)
    // ====================================================================

    [Fact]
    public void Canal_ReservedTiles_BlockStructurePlacement()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(CanalReservation.IsReserved(sim.World, new TileCoord(2, 5)));

        // A Stockpile on a reserved mid-path tile (no structure of its own) is
        // rejected by the reservation check.
        var outcome = new PlaceSiteIntent(new TileCoord(2, 5), StructureKind.Stockpile)
            { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsRejected);
    }

    [Fact]
    public void PlaceSiteIntent_RejectsCanalKind()
    {
        var sim = MakeSim();
        var outcome = new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Canal)
            { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsRejected);
    }

    // ====================================================================
    // Irrigation — canal water restores degraded land beside it (A×B)
    // ====================================================================

    [Fact]
    public void Canal_Irrigation_RestoresDegradedLandBesideIt()
    {
        var sim = MakeSim();
        var cfg = sim.World.BiomeDegradationConfig;
        // Source water far from the field; the field is latched (not near
        // water) until the canal reaches it.
        sim.World.Grid.SetBiome(new TileCoord(5, 8), Biome.Water);
        var field = new TileCoord(5, 5);
        var margin = 100; // points below DesertThreshold
        var dev = cfg.DesertThreshold - margin - cfg.GrasslandBaseline;
        sim.World.Fertility[field] = new Fertility(dev, 0);

        // Before any canal: latched Desert (nearest water is 3 tiles away).
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(sim.World, field, 0, cfg));

        // Dig a canal from the source up to a tile adjacent to the field.
        var path = new List<TileCoord> { new(5, 7), new(5, 6) }; // (5,6) is 1 tile from field
        PlaceAndCompleteCanal(sim, path);
        var anchor = sim.Now; // recovery anchors at completion

        // Now within WaterRecoveryRadius of canal water → recovers. It climbs
        // `margin` points to cross DesertThreshold; one period short it is
        // still Desert, at the crossing it is Grassland.
        var periods = (margin + cfg.RecoveryAmount - 1) / cfg.RecoveryAmount;
        var crossTick = anchor + periods * cfg.RecoveryPeriod;
        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(sim.World, field, anchor, cfg));
        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(sim.World, field, crossTick - cfg.RecoveryPeriod, cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(sim.World, field, crossTick, cfg));
    }

    // ====================================================================
    // Persistence
    // ====================================================================

    [Fact]
    public void Canal_MidBuild_SnapshotRoundTrips_PreservesPath()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var site = (ConstructionSite)sim.World.Structures[new TileCoord(1, 5)];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 1; i <= site.RequiredBuilderCount; i++)
        {
            var u = new Unit(i, new TileCoord(1, 5)) { Role = UnitRole.Builder };
            sim.World.AddUnit(u);
            u.TrySetActivity(Activity.Building, new TileCoord(1, 5));
        }
        site.StartOrResume(sim);
        AdvanceTo(sim, site.ScheduledCompletion!.Value / 2); // mid-build

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var rsite = (ConstructionSite)restored.World.Structures[new TileCoord(1, 5)];
        Assert.Equal(StructureKind.Canal, rsite.TargetKind);
        Assert.Equal(path, rsite.CanalPath);
        Assert.Equal(site.BuildDurationTicks, rsite.BuildDurationTicks);
    }

    [Fact]
    public void Canal_RecoveryAfterCrash_MidBuild_ReplaysIdentical()
    {
        // The M4 contract: Hash(uninterrupted) == Hash(snapshot-and-recover)
        // from an identical mid-build point. We snapshot the SAME sim partway
        // through the canal build, restore a clone, then run BOTH forward — so
        // both share the exact event history up to the snapshot and only the
        // anchor-regeneration of the queued BuildCompleteEvent is under test.
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(0, 5), Biome.Water);
        var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) };
        var site = (ConstructionSite)sim.World.AddStructure(
            new ConstructionSite(new TileCoord(1, 5), StructureKind.Canal, path) { OwnerId = 0 });
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 1; i <= site.RequiredBuilderCount; i++)
        {
            var u = new Unit(i, new TileCoord(1, 5)) { Role = UnitRole.Builder };
            sim.World.AddUnit(u);
            u.TrySetActivity(Activity.Building, new TileCoord(1, 5));
        }
        site.StartOrResume(sim);
        AdvanceTo(sim, site.ScheduledCompletion!.Value / 2); // mid-build

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        sim.Run();      // uninterrupted continuation
        restored.Run(); // recovered continuation

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        // And the canal actually flooded in both.
        Assert.Equal(Biome.Water, sim.World.Grid.BiomeAt(new TileCoord(3, 5)));
        Assert.Equal(Biome.Water, restored.World.Grid.BiomeAt(new TileCoord(3, 5)));
    }

    // ====================================================================
    // Boat composition (Phase C) — canal water IS Biome.Water, so existing
    // boat movement and Dock placement compose with zero new boat code.
    // ====================================================================

    [Fact]
    public void Canal_CreatesNavigableWater_BoatSailsInland()
    {
        var sim = MakeSim(size: 8);
        for (var y = 0; y < 8; y++) sim.World.Grid.SetBiome(new TileCoord(0, y), Biome.Water); // coast
        var boat = sim.World.AddUnit(new Unit(50, new TileCoord(0, 5))
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water, BornTick = 0,
        });

        // Before the canal, the inland tile (3,5) is landlocked — no water route.
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(3, 5))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Null(boat.PathRemaining);

        // Dig a canal inland from the coast and let it complete.
        PlaceAndCompleteCanal(sim, new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) });

        // Now the boat sails the canal to the inland end — the supply line.
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(3, 5))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.NotNull(boat.PathRemaining);
        sim.Run();
        Assert.Equal(new TileCoord(3, 5), boat.Position);
    }

    [Fact]
    public void Dock_SlipOnCanalTile_Accepted()
    {
        var sim = MakeSim(size: 8);
        for (var y = 0; y < 8; y++) sim.World.Grid.SetBiome(new TileCoord(0, y), Biome.Water);
        PlaceAndCompleteCanal(sim, new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) });

        // An inland Dock at (3,6) with its slip on the canal tile (3,5): you
        // can build a shipyard at the heart of the kingdom served by the canal.
        var outcome = new PlaceSiteIntent(new TileCoord(3, 6), StructureKind.Dock,
            dockSlip: new TileCoord(3, 5)) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
    }

    // ====================================================================
    // Headline determinism (M21 contract)
    // ====================================================================

    [Fact]
    public void Canals_TwinRun_HashesMatch()
    {
        // Two identical scenarios — build a multi-tile canal from a coast,
        // sail a boat through it, and have a degraded field beside the canal
        // recover — must produce equal Snapshot.Hash. This is the M21
        // milestone contract (architecture §1).
        Simulation Scenario()
        {
            var sim = MakeSim(size: 10);
            for (var y = 0; y < 10; y++) sim.World.Grid.SetBiome(new TileCoord(0, y), Biome.Water);
            var cfg = sim.World.BiomeDegradationConfig;
            // A latched field at (3,4): 3 tiles from the coast (> radius 2).
            sim.World.Fertility[new TileCoord(3, 4)] =
                new Fertility(cfg.DesertThreshold - 100 - cfg.GrasslandBaseline, 0);
            // A boat on the coast.
            sim.World.AddUnit(new Unit(50, new TileCoord(0, 5))
            {
                Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water, BornTick = 0,
            });
            // Dig a canal inland (row 5) past the field.
            var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) };
            var site = (ConstructionSite)sim.World.AddStructure(
                new ConstructionSite(new TileCoord(1, 5), StructureKind.Canal, path) { OwnerId = 0 });
            foreach (var (r, n) in site.Required) site.Deposit(r, n);
            for (var i = 1; i <= site.RequiredBuilderCount; i++)
            {
                var u = new Unit(i, new TileCoord(1, 5)) { Role = UnitRole.Builder };
                sim.World.AddUnit(u);
                u.TrySetActivity(Activity.Building, new TileCoord(1, 5));
            }
            site.StartOrResume(sim);
            sim.Run(); // canal floods; (3,4) becomes near-water
            new Sim.Core.Movement.MoveIntent(50, new TileCoord(3, 5)) { PlayerId = 0 }.Resolve(sim);
            sim.Run(); // boat sails the canal inland
            return sim;
        }

        var a = Scenario();
        var b = Scenario();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));

        // Sanity: all three effects actually happened.
        Assert.Equal(Biome.Water, a.World.Grid.BiomeAt(new TileCoord(3, 5)));   // canal flooded
        Assert.Equal(new TileCoord(3, 5), a.World.Units[50].Position);          // boat sailed
        var cfg2 = a.World.BiomeDegradationConfig;
        Assert.Equal(Biome.Grassland,                                           // field recovers
            BiomeDegradation.BiomeAt(a.World, new TileCoord(3, 4), a.Now + 1_000_000, cfg2));
    }
}
