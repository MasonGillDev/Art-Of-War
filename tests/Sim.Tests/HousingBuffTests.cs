using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Logistics;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Tests;

// Housing buffs (docs/housing-buffs.md): a unit homed at a FED own House is
// settled and works and fights harder. Castle-homed units get nothing; a
// resident of a starving house gets nothing and is NOT penalised (user
// decision). Every number derives from HousingConstants and the catalog.
public class HousingBuffTests
{
    private static readonly TileCoord Keep = new(0, 0);

    private static Simulation MakeWorld(StructureKind kind = StructureKind.LumberCamp, Biome biome = Biome.Forest)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var spec = StructureCatalog.Spec(kind);
        var r = Math.Max(spec.ClaimRange, 0);
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var t = new TileCoord(5 + dx, 5 + dy);
                if (grid.InBounds(t)) grid.SetBiome(t, biome);
            }
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        return new Simulation(world, seed: 3);
    }

    private static House AddHouse(Simulation sim, TileCoord at, int food)
    {
        var house = (House)sim.World.AddStructure(new House(at) { OwnerId = 0 });
        if (food > 0) house.Deposit(Resource.Food, food);
        return house;
    }

    private static Unit AddUnit(Simulation sim, int id, TileCoord at, UnitRole role = UnitRole.None)
    {
        // No BornTick: a null birth is "adult of unknown age" in hand-built
        // worlds; BornTick = 0 would make every worker an infant and fail the
        // training-age gate in WorkAssignment.
        var u = new Unit(id, at) { OwnerId = 0, Role = role };
        sim.World.AddUnit(u);
        return u;
    }

    [Fact]
    public void Settled_IsFedOwnHouse_NotCastle_NotStarving_NoPenalty()
    {
        var sim = MakeWorld();
        var world = sim.World;
        var fed = AddHouse(sim, new TileCoord(3, 3), food: 50);
        var red = AddHouse(sim, new TileCoord(4, 3), food: 0);

        var settled = AddUnit(sim, 1, Keep);
        var castleMouth = AddUnit(sim, 2, Keep);
        var starving = AddUnit(sim, 3, Keep);
        Population.SetHome(sim, settled, fed.At);
        Population.SetHome(sim, starving, red.At);
        red.FoodDebt = 5;   // the field CatchUp accrues; CurrentLevel goes negative

        var basePower = CombatRules.EffectivePower(castleMouth, sim.Now);
        Assert.Equal(basePower + HousingConstants.SettledPowerBonus,
            CombatRules.EffectivePower(world, settled, sim.Now));
        Assert.Equal(basePower, CombatRules.EffectivePower(world, castleMouth, sim.Now));
        // No penalty: a starving resident fights exactly like an unhoused one.
        Assert.Equal(basePower, CombatRules.EffectivePower(world, starving, sim.Now));

        // Feeding the red house settles its resident with no other change.
        red.FoodDebt = 0;
        red.Deposit(Resource.Food, 10);
        Assert.True(Housing.IsSettled(world, starving, sim.Now));
    }

    [Fact]
    public void Settled_IsOwnerScoped()
    {
        var sim = MakeWorld();
        var world = sim.World;
        world.Players[1] = new Player(1);
        var theirs = (House)world.AddStructure(new House(new TileCoord(3, 3)) { OwnerId = 1 });
        theirs.Deposit(Resource.Food, 50);
        var u = AddUnit(sim, 1, Keep);
        // A stale Home pointing at a house that changed hands never settles.
        u.Home = theirs.At;
        Assert.False(Housing.IsSettled(world, u, sim.Now));
    }

    [Fact]
    public void SettledWorker_ProducesTheBonus_EachPeriod()
    {
        var post = new TileCoord(5, 5);

        var a = MakeWorld();
        var exA = (Extractor)a.World.AddStructure(new Extractor(StructureKind.LumberCamp, post) { OwnerId = 0 });
        var houseA = AddHouse(a, new TileCoord(6, 5), food: 500);
        var wa = AddUnit(a, 1, post, UnitRole.Lumberjack);
        Population.SetHome(a, wa, houseA.At);
        a.SubmitIntent(0, new AssignWorkersIntent(post, new[] { 1 }));

        var b = MakeWorld();
        var exB = (Extractor)b.World.AddStructure(new Extractor(StructureKind.LumberCamp, post) { OwnerId = 0 });
        AddUnit(b, 1, post, UnitRole.Lumberjack);   // castle-homed
        b.SubmitIntent(0, new AssignWorkersIntent(post, new[] { 1 }));

        var spec = exA.Spec;
        const int periods = 3;
        a.Run(until: spec.ProductionPeriodTicks * periods);
        b.Run(until: spec.ProductionPeriodTicks * periods);

        var matched = spec.BaseRatePerWorker * spec.RoleBonusNumerator / spec.RoleBonusDenominator;
        Assert.Equal(matched * periods, exB.Buffer);
        Assert.Equal((matched + HousingConstants.SettledWorkBonusPerWorker) * periods, exA.Buffer);
    }

    [Fact]
    public void StarvingWorker_ProducesBaseRate_NotLess()
    {
        var post = new TileCoord(5, 5);
        var sim = MakeWorld();
        var ex = (Extractor)sim.World.AddStructure(new Extractor(StructureKind.LumberCamp, post) { OwnerId = 0 });
        var red = AddHouse(sim, new TileCoord(6, 5), food: 0);
        var w = AddUnit(sim, 1, post, UnitRole.Lumberjack);
        Population.SetHome(sim, w, red.At);
        red.FoodDebt = 50;
        sim.SubmitIntent(0, new AssignWorkersIntent(post, new[] { 1 }));

        var spec = ex.Spec;
        sim.Run(until: spec.ProductionPeriodTicks * 2);
        var matched = spec.BaseRatePerWorker * spec.RoleBonusNumerator / spec.RoleBonusDenominator;
        Assert.Equal(matched * 2, ex.Buffer);
    }
}
