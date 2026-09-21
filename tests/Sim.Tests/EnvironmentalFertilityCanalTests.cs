using Sim.Core.Biomes;
using Sim.Core.Canals;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M35 Phase C — canal completion moves the environmental BASELINE, through
// the same transition discipline M21 built for the rate change
// (docs/environmental-fertility.md decision 4; docs/canals.md update):
//   * catch up under the OLD rate and baseline, anchor, THEN flood;
//   * the lift is INSTANT — stored deviation is untouched, so fertility jumps
//     by the baseline delta on the tick the water lands;
//   * the jump is observation-independent;
//   * the affected set widens to the fertility radii only when a strength
//     knob is non-zero (at strength 0 it is exactly M21's).
public class EnvironmentalFertilityCanalTests
{
    private static readonly BiomeDegradationConfig Flat = new(
        ForestBaseline:    100,
        GrasslandBaseline:  50,
        DesertBaseline:     10,
        HillsBaseline:      30,
        MountainBaseline:   60,
        WaterBaseline:       0,
        ForestThreshold:    75,
        DesertThreshold:    25,
        RecoveryAmount:      1,
        RecoveryPeriod:     30,
        DegradePeriod:      40,
        DegradeRadius:       2,
        WaterRecoveryRadius: 2,
        WaterRecoveryAmount: 1);

    // Water reaches further than recovery here (radius 3 vs 2) so the union
    // is visibly wider than M21's set. +20 on the bank, tapering by 5 a ring.
    private static readonly BiomeDegradationConfig Graded = Flat with
    {
        WaterFertilityRadius = 3,
        WaterFertilityBonus = 20,
    };

    private static GameWorld MakeWorld(TileGrid grid, BiomeDegradationConfig cfg) => new(
        grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
        new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
        cfg);

    [Fact]
    public void CanalCompletion_LiftsTheBaselineInstantly_AndAnchorsTheOldRate()
    {
        // A healthy grassland tile 2 rings from where the canal will run,
        // recovering from an old deficit at 1 per 30 ticks. lastUpdate 0.
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var tile = new TileCoord(5, 5);
        var canal = new TileCoord(5, 7);
        var world = MakeWorld(grid, Graded);
        world.Fertility[tile] = new Fertility(-20, 0);

        // Dry world: baseline 50 (no penalty configured), fert 30 + 1/30t.
        Assert.Equal(50, BiomeDegradation.BaselineFertility(world, tile, Graded));
        Assert.Equal(30 + 20, BiomeDegradation.FertilityAt(world, tile, 600, Graded)); // fully recovered by 600

        // Re-seed a live deficit so the jump is visible on top of it.
        world.Fertility[tile] = new Fertility(-20, 0);
        Assert.Equal(30 + 10, BiomeDegradation.FertilityAt(world, tile, 300, Graded));  // 10 periods

        // The transition at 300: catch up (old rate, old baseline), anchor, flood.
        BiomeDegradation.OnWaterProximityChanged(world, new[] { canal }, now: 300, Graded);
        grid.SetBiome(canal, Biome.Water);
        world.Fertility.Remove(canal);

        // Anchored at 300 with the recovered deviation (-10), untouched by the lift.
        Assert.Equal(300, world.Fertility[tile].LastUpdateTick);
        Assert.Equal(-10, world.Fertility[tile].Deviation);

        // INSTANT LIFT: distance 2 -> +20 * (3 + 1 - 2) / 4 = +10 on the
        // baseline, so the tile reads 60 - 10 = 50 on the same tick.
        Assert.Equal(60, BiomeDegradation.BaselineFertility(world, tile, Graded));
        Assert.Equal(50, BiomeDegradation.FertilityAt(world, tile, 300, Graded));
        // And recovery resumes from the anchor toward the NEW baseline.
        Assert.Equal(55, BiomeDegradation.FertilityAt(world, tile, 450, Graded));
        Assert.Equal(60, BiomeDegradation.FertilityAt(world, tile, 900, Graded));
    }

    [Fact]
    public void CanalCompletion_JumpIsObservationIndependent()
    {
        // One world is read once after the canal; the other is read at many
        // ticks before and after. Same stored state, same answers.
        GameWorld Build()
        {
            var grid = new TileGrid(12, 12, Biome.Grassland);
            var w = MakeWorld(grid, Graded);
            w.Fertility[new TileCoord(5, 5)] = new Fertility(-20, 0);
            return w;
        }
        void Flood(GameWorld w, long now)
        {
            var canal = new TileCoord(5, 7);
            BiomeDegradation.OnWaterProximityChanged(w, new[] { canal }, now, Graded);
            w.Grid.SetBiome(canal, Biome.Water);
            w.Fertility.Remove(canal);
        }
        var tile = new TileCoord(5, 5);
        var once = Build();
        var many = Build();
        foreach (var t in new long[] { 7, 40, 133, 299 })
            BiomeDegradation.FertilityAt(many, tile, t, Graded);
        Flood(once, 300);
        Flood(many, 300);
        foreach (var t in new long[] { 300, 301, 333, 449, 450, 600 })
            BiomeDegradation.FertilityAt(many, tile, t, Graded);

        Assert.Equal(once.Fertility[tile].Deviation, many.Fertility[tile].Deviation);
        Assert.Equal(once.Fertility[tile].LastUpdateTick, many.Fertility[tile].LastUpdateTick);
        Assert.Equal(
            BiomeDegradation.FertilityAt(once, tile, 900, Graded),
            BiomeDegradation.FertilityAt(many, tile, 900, Graded));
    }

    [Fact]
    public void AffectedSet_WidensToTheFertilityRadius_OnlyWhenTheKnobIsOn()
    {
        // A tile 3 rings out: inside WaterFertilityRadius (3), outside
        // WaterRecoveryRadius (2). With the bonus on it must be anchored (its
        // baseline changes); with the bonus off it must NOT be touched
        // (anchoring would drop its carry and shift its recovery clock).
        static GameWorld Build(BiomeDegradationConfig cfg)
        {
            var grid = new TileGrid(12, 12, Biome.Grassland);
            var w = MakeWorld(grid, cfg);
            w.Fertility[new TileCoord(5, 4)] = new Fertility(-20, 0);   // 3 rings from (5,7)
            return w;
        }
        var canal = new[] { new TileCoord(5, 7) };
        var far = new TileCoord(5, 4);

        var on = Build(Graded);
        BiomeDegradation.OnWaterProximityChanged(on, canal, now: 300, Graded);
        Assert.Equal(300, on.Fertility[far].LastUpdateTick);

        var off = Build(Flat);
        BiomeDegradation.OnWaterProximityChanged(off, canal, now: 300, Flat);
        Assert.Equal(0, off.Fertility[far].LastUpdateTick);     // untouched: M21's set exactly
    }

    // ---- end to end through the real canal build job ----

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    [Fact]
    public void RealCanal_RaisesTheBaseline_OfTheFieldsBesideIt()
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        grid.SetBiome(new TileCoord(0, 5), Biome.Water);   // source
        var world = MakeWorld(grid, Graded);
        var sim = new Simulation(world, seed: 1);
        var field = new TileCoord(3, 3);                    // 2 rings from (3,5); 3 from the source lake
        Assert.Equal(55, BiomeDegradation.BaselineFertility(world, field, Graded)); // +5 from the lake already

        var path = new List<TileCoord> { new(1, 5), new(2, 5), new(3, 5) };
        var outcome = new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        var site = (ConstructionSite)world.Structures[path[0]];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 1; i <= site.RequiredBuilderCount; i++)
        {
            var u = new Unit(i, path[0]) { Role = UnitRole.Builder };
            world.AddUnit(u);
            u.TrySetActivity(Activity.Building, path[0]);
        }
        site.StartOrResume(sim);
        sim.Run();

        Assert.Equal(Biome.Water, world.Grid.BiomeAt(new TileCoord(3, 5)));
        Assert.Equal(60, BiomeDegradation.BaselineFertility(world, field, Graded));
        Assert.Equal(60, BiomeDegradation.FertilityAt(world, field, sim.Now, Graded));
        // Snapshot round-trip after the flood hashes equal: the lifted
        // baseline is derived from the grid, never stored.
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
