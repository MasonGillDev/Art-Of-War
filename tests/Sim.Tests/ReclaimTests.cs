using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// M26 — reclaiming razed ground (docs/sieges-and-conquest.md update). The
// conquest flow: destroy a structure → clear the rubble → build on the
// land. Pins:
//   1. A razed EXTRACTOR frees its claim tiles IMMEDIATELY (Claims scans
//      live structures; rubble carries no claims) — the fallen kingdom's
//      land is claimable before anyone clears anything.
//   2. Razing a PRODUCING extractor anchors its claims' soil damage at the
//      raze tick (the M9/§2.5 rate-transition discipline) — the producing
//      window's degradation must not evaporate retroactively.
//   3. ClearRubbleIntent → materials-free clearing site → builder →
//      EMPTY tile → buildable again. The full reclaim loop.
public class ReclaimTests
{
    private static Simulation MakeSim(int size = 12)
    {
        var world = new GameWorld(new TileGrid(size, size, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
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

    // A staffed, producing farm whose claims auto-fill on arm (the
    // ArmIfDormant lazy fill — the standard hand-built fixture path).
    private static Extractor MakeProducingFarm(Simulation sim, TileCoord at, int owner, int unitId)
    {
        var farm = new Extractor(StructureKind.Farm, at) { OwnerId = owner };
        sim.World.AddStructure(farm);
        var worker = new Unit(unitId, at) { Role = UnitRole.Farmer, OwnerId = owner };
        sim.World.AddUnit(worker);
        Assert.True(new AssignWorkersIntent(at, new[] { unitId }) { PlayerId = owner }
            .Resolve(sim).IsApplied);
        Assert.True(farm.TickArmed, "farm must be producing");
        Assert.NotEmpty(farm.ClaimTiles);
        return farm;
    }

    // ====================================================================
    // Razing frees the land
    // ====================================================================

    [Fact]
    public void RazedExtractor_FreesItsClaims_Immediately()
    {
        var sim = MakeSim();
        var farm = MakeProducingFarm(sim, new TileCoord(5, 5), owner: 1, unitId: 1);
        var claim = farm.ClaimTiles[0];
        Assert.Equal(farm.At, Claims.ClaimantAt(sim.World, claim));

        // The worker dies with the farm's fall (defender shielding means a
        // live defender would have blocked the raze); remove them first so
        // the tile tells a consistent story.
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, farm);

        Assert.IsType<Rubble>(sim.World.Structures[new TileCoord(5, 5)]);
        // The claims are free NOW — no rubble-clearing required. The former
        // claim tile takes a new structure straight away.
        Assert.Null(Claims.ClaimantAt(sim.World, claim));
        Assert.True(new PlaceSiteIntent(claim, StructureKind.Stockpile) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
    }

    [Fact]
    public void RazingAProducingFarm_AnchorsItsSoilDamage()
    {
        var sim = MakeSim();
        var cfg = sim.World.BiomeDegradationConfig;
        var farm = MakeProducingFarm(sim, new TileCoord(5, 5), owner: 1, unitId: 1);
        var claim = farm.ClaimTiles[0];

        // Produce long enough for real soil damage (config-derived — the
        // period is a balance knob).
        AdvanceTo(sim, 40 * cfg.DegradePeriod);
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        var razeTick = sim.Now;
        Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, farm);

        // The transition catch-up banked the producing window's damage and
        // anchored the tile at the raze tick — the §2.5 discipline. Without
        // it, the next read would re-derive rate ~0 (no extractor) over the
        // WHOLE window and the damage would evaporate retroactively.
        Assert.True(sim.World.Fertility.TryGetValue(claim, out var f),
            "claim tile must carry a fertility entry after the transition");
        Assert.Equal(razeTick, f!.LastUpdateTick);
        Assert.True(f.Deviation < 0, "the producing window's soil damage must be banked");
    }

    // ====================================================================
    // The clearing job
    // ====================================================================

    private static Rubble MakeRubble(Simulation sim, TileCoord at)
    {
        sim.World.AddStructure(new Stockpile(at) { OwnerId = 1 });
        Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, sim.World.Structures[at]);
        return Assert.IsType<Rubble>(sim.World.Structures[at]);
    }

    [Fact]
    public void ClearRubble_FullReclaimFlow_RazeClearBuild()
    {
        var sim = MakeSim();
        var at = new TileCoord(5, 5);
        MakeRubble(sim, at);

        // Player 0 reclaims the fallen player-1 ground: rubble → clearing
        // job (materials-free — labor is the whole price).
        Assert.True(new ClearRubbleIntent(at) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var site = Assert.IsType<ConstructionSite>(sim.World.Structures[at]);
        Assert.Equal(StructureKind.Rubble, site.TargetKind);
        Assert.Equal(0, site.OwnerId);
        Assert.True(site.MaterialsMet(), "clearing needs no materials");
        Assert.Equal(StructureCatalog.Spec(StructureKind.Rubble).BuildDurationTicks,
            site.BuildDurationTicks);

        // One laborer, the standard build dance, and the tile comes back.
        var builder = new Unit(1, at) { Role = UnitRole.Builder, OwnerId = 0 };
        sim.World.AddUnit(builder);
        builder.TrySetActivity(Activity.Building, at);
        site.StartOrResume(sim);
        sim.Run();

        Assert.False(sim.World.Structures.ContainsKey(at));   // reclaimed ground
        Assert.Equal(Activity.Idle, builder.Activity);        // hands freed
        // ...and it builds again: the full destroy → clear → build loop.
        Assert.True(new PlaceSiteIntent(at, StructureKind.House) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
    }

    [Fact]
    public void ClearRubble_RejectsNonRubbleTiles_FailClean()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(new TileCoord(3, 3)) { OwnerId = 0 });

        // Empty ground, a standing structure, out of bounds: all rejected.
        Assert.True(new ClearRubbleIntent(new TileCoord(5, 5)) { PlayerId = 0 }
            .Resolve(sim).IsRejected);
        Assert.True(new ClearRubbleIntent(new TileCoord(3, 3)) { PlayerId = 0 }
            .Resolve(sim).IsRejected);
        Assert.True(new ClearRubbleIntent(new TileCoord(99, 99)) { PlayerId = 0 }
            .Resolve(sim).IsRejected);
        // The castle is untouched.
        Assert.IsType<Castle>(sim.World.Structures[new TileCoord(3, 3)]);
    }

    [Fact]
    public void PlaceSite_RejectsRubbleKind()
    {
        var sim = MakeSim();
        Assert.True(new PlaceSiteIntent(new TileCoord(5, 5), StructureKind.Rubble)
        { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.Empty(sim.World.Structures);
    }

    // ====================================================================
    // Determinism
    // ====================================================================

    [Fact]
    public void MidClear_SnapshotRoundTrips()
    {
        var sim = MakeSim();
        var at = new TileCoord(5, 5);
        MakeRubble(sim, at);
        Assert.True(new ClearRubbleIntent(at) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var site = Assert.IsType<ConstructionSite>(restored.World.Structures[at]);
        Assert.Equal(StructureKind.Rubble, site.TargetKind);
    }

    [Fact]
    public void Reclaim_TwinRun_HashesMatch()
    {
        Simulation Run()
        {
            var sim = MakeSim();
            var at = new TileCoord(5, 5);
            MakeRubble(sim, at);
            Assert.True(new ClearRubbleIntent(at) { PlayerId = 0 }.Resolve(sim).IsApplied);
            var site = (ConstructionSite)sim.World.Structures[at];
            var builder = new Unit(1, at) { Role = UnitRole.Builder, OwnerId = 0 };
            sim.World.AddUnit(builder);
            builder.TrySetActivity(Activity.Building, at);
            site.StartOrResume(sim);
            sim.Run();
            Assert.True(new PlaceSiteIntent(at, StructureKind.House) { PlayerId = 0 }
                .Resolve(sim).IsApplied);
            return sim;
        }

        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }
}
