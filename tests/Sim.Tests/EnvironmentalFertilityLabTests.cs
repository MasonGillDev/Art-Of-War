using Sim.Core;
using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Xunit.Abstractions;

namespace Sim.Tests;

// M35 Phase F — THE GRADIENT SWEEP (docs/environmental-fertility.md, knobs
// and cautions). Two Homesteaders on the lab continent for LabDays at three
// gradient strengths: 0 (today's flat world, the identity), mild, strong.
// The per-run curve prints as test output — that report is what the lab
// exists to produce, and the defaults are picked FROM it, by the user.
//
// Hard pins at every strength: the survival benchmark (no starvation death,
// no famine at the horizon, population above genesis). "Noticeable, never
// mandatory": dry-land farming must stay survivable, so the strong rung is
// allowed to be greedy but not lethal.
public class EnvironmentalFertilityLabTests
{
    private readonly ITestOutputHelper _output;
    public EnvironmentalFertilityLabTests(ITestOutputHelper output) { _output = output; }

    private const long LabDays = 100;

    private static (Simulation sim, ViewProjector projector) MakeMatch(BiomeDegradationConfig cfg)
    {
        var opts = new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec with { BiomeDegradation = cfg }, seed: 0xA117);
        return (sim, new ViewProjector(build));
    }

    private static Castle CastleOf(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.OfType<Castle>().Single(c => c.OwnerId == ownerId);

    private static int DesertTiles(Simulation sim, BiomeDegradationConfig cfg)
    {
        var n = 0;
        for (var y = 0; y < sim.World.Grid.Height; y++)
            for (var x = 0; x < sim.World.Grid.Width; x++)
                if (BiomeDegradation.BiomeAt(sim.World, new TileCoord(x, y), sim.Now, cfg) == Biome.Desert) n++;
        return n;
    }

    // Mean baseline over a faction's farm claims, and how far above the flat
    // band baseline that is — "did the brain actually site by the gradient?"
    private static (int farms, double meanBaseline) FarmSiting(Simulation sim, int ownerId, BiomeDegradationConfig cfg)
    {
        var farms = sim.World.Structures.Values.OfType<Extractor>()
            .Where(e => e.OwnerId == ownerId && e.Kind == StructureKind.Farm).ToList();
        var tiles = farms.SelectMany(f => f.ClaimTiles).ToList();
        if (tiles.Count == 0) return (farms.Count, 0);
        return (farms.Count, tiles.Average(t => (double)BiomeDegradation.BaselineFertility(sim.World, t, cfg)));
    }

    [Theory]
    [InlineData("flat",   0,    0,   0)]
    [InlineData("mild",   1000, 250, 300)]
    [InlineData("strong", 2500, 500, 600)]
    public void GradientSweep_LabReport(string rung, int waterBonus, int dryPenalty, int depthPerRing)
    {
        var cfg = new BiomeDegradationConfig() with
        {
            WaterFertilityBonus = waterBonus,
            DryEdgePenalty = dryPenalty,
            ForestDepthBonusPerRing = depthPerRing,
        };
        var (sim, projector) = MakeMatch(cfg);
        var genesisDesert = DesertTiles(sim, cfg);
        var ai = new AiConfig();
        var drivers = new[] { new AiPlayerDriver(0, ai), new AiPlayerDriver(1, ai) };

        _output.WriteLine($"== {rung}: water +{waterBonus} / dry -{dryPenalty} / depth +{depthPerRing} per ring " +
                          $"(band Grassland {cfg.GrasslandBaseline}, Forest {cfg.ForestBaseline}) ==");
        for (long t = 0; t <= LabDays * Time.Day; t += ai.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
            if (t % (20 * Time.Day) == 0)
            {
                var line = $"d{t / Time.Day,3}:";
                foreach (var id in new[] { 0, 1 })
                {
                    var c = CastleOf(sim, id);
                    var (farms, mean) = FarmSiting(sim, id, cfg);
                    line += $"  f{id} pop={sim.World.Players[id].PopulationCount,2} " +
                            $"food={Sim.Core.Food.FoodConsumption.CurrentLevel(c, sim, t),5} " +
                            $"farms={farms} siteBase={mean,6:F0}";
                }
                line += $"  desert+{DesertTiles(sim, cfg) - genesisDesert}";
                _output.WriteLine(line);
            }
        }
        sim.Run(until: LabDays * Time.Day + ai.ThinkPeriodTicks);

        foreach (var id in new[] { 0, 1 })
        {
            var castle = CastleOf(sim, id);
            var pop = sim.World.Players[id].PopulationCount;
            Assert.DoesNotContain(sim.ResolvedLog.OfType<Sim.Core.Food.StarvationDeathEvent>(),
                e => e.HomeAt == castle.At && e.Outcome is null);
            Assert.True(castle.FoodDebt == 0 && castle.FamineStartTick is null,
                $"[{rung}] faction {id} ends day {LabDays} in famine (debt={castle.FoodDebt})");
            Assert.True(pop >= 14, $"[{rung}] faction {id} shrank to {pop}");
        }
    }
}
