using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Wire;

namespace Sim.Tests;

// Kingdom analytics on the wire (docs/structure-rates-on-the-wire.md):
//   * StructDto.OutputPerDay is the tick's own formula over a day, own-only;
//   * Producing mirrors whether the production clock is running;
//   * StructDto.EatsPerDay is a food home's residents' daily appetite, in the
//     SAME per-day unit, so a farm reads directly against a house;
//   * ProductionRate.PerPeriod is what ProductionTickEvent actually spends.
// Every expected number is derived from the catalog and the food constants,
// never hard-coded — they are balance knobs.
public class StructureRatesWireTests
{
    private static (Simulation sim, ViewProjector projector) MakeWorld()
    {
        var opts = new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        return (new Simulation(build.Spec, seed: 0xA117), new ViewProjector(build));
    }

    private static Extractor PlaceFarm(Simulation sim, int owner)
    {
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        for (var y = 3; y < sim.World.Grid.Height - 3; y++)
            for (var x = 3; x < sim.World.Grid.Width - 3; x++)
            {
                var t = new TileCoord(x, y);
                if (sim.World.Grid.BiomeAt(t) != Biome.Grassland || sim.World.Structures.ContainsKey(t)) continue;
                if (Claims.AutoSelect(sim.World, t, spec, sim.Now) is null) continue;
                return (Extractor)sim.World.AddStructure(new Extractor(StructureKind.Farm, t) { OwnerId = owner });
            }
        throw new InvalidOperationException("no farmable grassland on the test map");
    }

    private static StructDto Seen(ViewProjector p, Simulation sim, int viewer, TileCoord at) =>
        p.Project(sim, sim.Now, playerId: viewer, reveal: true).Structures.Single(s => s.X == at.X && s.Y == at.Y);

    [Fact]
    public void Farm_OutputPerDay_IsTheTicksRateOverADay_OwnOnly()
    {
        var (sim, projector) = MakeWorld();
        var farm = PlaceFarm(sim, owner: 0);
        var spec = farm.Spec;

        var idle = Seen(projector, sim, 0, farm.At);
        Assert.Equal(0, idle.OutputPerDay);
        Assert.False(idle.Producing);

        var u = sim.World.AddUnit(new Unit(9001, farm.At) { Role = UnitRole.Farmer, OwnerId = 0 });
        farm.Workers.Add(u.Id);
        farm.ArmIfDormant(sim);

        // One matching-role, castle-homed worker on fresh soil: the role-bonus
        // rate, untapered, times the periods in a day.
        var perPeriod = (long)spec.BaseRatePerWorker * spec.RoleBonusNumerator / spec.RoleBonusDenominator;
        var expected = perPeriod * Time.Day / spec.ProductionPeriodTicks;
        Assert.Equal(expected, ProductionRate.PerDay(sim.World, farm, sim.Now));

        var own = Seen(projector, sim, 0, farm.At);
        Assert.Equal(expected, own.OutputPerDay);
        Assert.True(own.Producing);

        var foreign = Seen(projector, sim, 1, farm.At);
        Assert.Equal(0, foreign.OutputPerDay);
        Assert.False(foreign.Producing);
    }

    [Fact]
    public void ProductionTick_SpendsExactlyTheReportedRate()
    {
        var (sim, _) = MakeWorld();
        var farm = PlaceFarm(sim, owner: 0);
        var u = sim.World.AddUnit(new Unit(9001, farm.At) { Role = UnitRole.Miner, OwnerId = 0 });
        farm.Workers.Add(u.Id);
        farm.ArmIfDormant(sim);

        var before = farm.Buffer;
        var promised = ProductionRate.PerPeriod(sim.World, farm, sim.Now + farm.Spec.ProductionPeriodTicks);
        sim.Run(until: sim.Now + farm.Spec.ProductionPeriodTicks);
        Assert.Equal(before + promised, farm.Buffer);
    }

    [Fact]
    public void FoodHomes_EatsPerDay_SplitsTheRealmBetweenCastleAndHouse()
    {
        var (sim, projector) = MakeWorld();
        var w = sim.World;
        var castle = FoodConsumption.FindCastleFor(w, 0)!;
        var mealsPerDay = FoodConsumptionConstants.FoodPerCitizenPerPeriod
                          * (Time.Day / FoodConsumptionConstants.FoodConsumptionPeriod);

        var allAtKeep = Seen(projector, sim, 0, castle.At).EatsPerDay;
        Assert.Equal(FoodConsumption.ResidentsOf(w, castle) * mealsPerDay, allAtKeep);
        Assert.True(allAtKeep > 0);

        // Move one of the castle's mouths into a house beside it: the house
        // eats one citizen's day, the castle one less. Same unit on both.
        var house = FreeTileNear(w, castle.At);
        w.AddStructure(new House(house) { OwnerId = 0 });
        var mouth = w.Units.Values.First(x => x.OwnerId == 0);
        Sim.Core.Population.Population.SetHome(sim, mouth, house);

        Assert.Equal(mealsPerDay, Seen(projector, sim, 0, house).EatsPerDay);
        Assert.Equal(allAtKeep - mealsPerDay, Seen(projector, sim, 0, castle.At).EatsPerDay);
        Assert.Equal(0, Seen(projector, sim, 1, house).EatsPerDay);
    }

    private static TileCoord FreeTileNear(GameWorld w, TileCoord c)
    {
        for (var r = 1; r < 8; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    var t = new TileCoord(c.X + dx, c.Y + dy);
                    if (t.X < 0 || t.Y < 0 || t.X >= w.Grid.Width || t.Y >= w.Grid.Height) continue;
                    if (w.Grid.BiomeAt(t) == Biome.Water || w.Structures.ContainsKey(t)) continue;
                    return t;
                }
        throw new InvalidOperationException("no free tile near the castle");
    }
}
