using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Logistics;
using Sim.Core.Population;
using Sim.Core.World;
using Xunit.Abstractions;

namespace Sim.Tests;

// SCRATCH — reproduce "train a farmer, assign to a farm, they vanish;
// the farm still lists them and population drops".
public class ScratchFarmerDeath
{
    private readonly ITestOutputHelper _out;
    public ScratchFarmerDeath(ITestOutputHelper o) { _out = o; }

    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord FarmAt = new(15, 10);
    private static readonly TileCoord HouseAt = new(16, 10);

    [Fact]
    public void Trace()
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 5000);            // castle is RICH

        var farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 };
        world.AddStructure(farm);

        // A house near the farm with an EMPTY larder — the "home follows work"
        // destination.
        var house = new House(HouseAt) { OwnerId = 0 };
        world.AddStructure(house);

        var cfg = new PopulationConfig();
        var u = new Unit(1, FarmAt)
        {
            Role = UnitRole.Farmer, OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
            DeathTick = 70L * cfg.TicksPerYear, DeathSeq = 1,
        };
        world.AddUnit(u);
        world.NextUnitId = 2;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++) for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        var sim = new Simulation(world, seed: 5);

        _out.WriteLine($"before assign: home={u.Home?.ToString() ?? "castle"} pop={world.Players[0].PopulationCount}");
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        _out.WriteLine($"after assign:  home={world.Units[1].Home?.ToString() ?? "castle"} " +
                       $"act={world.Units[1].Activity} workers={farm.Workers.Count}");

        for (long t = 0; t <= 8 * Time.Day; t += 6 * Time.Hour)
        {
            sim.Run(t);
            var alive = world.Units.ContainsKey(1);
            _out.WriteLine($"t={t,6} ({t / (double)Time.Day:F1}d) alive={alive} " +
                           $"farmWorkers=[{string.Join(",", farm.Workers)}] buffer={farm.Buffer} " +
                           $"houseFood={FoodConsumption.CurrentLevel(house, world, t)} debt={house.FoodDebt} " +
                           $"famine={house.FamineStartTick is not null} " +
                           $"pop={world.Players[0].PopulationCount}");
            if (!alive) break;
        }
    }
}
