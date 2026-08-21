using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Tests;

// A DEAD WORKER MUST COME OFF THE PAYROLL.
//
// Found in play: a farmer was trained, assigned to a farm, starved on shift —
// and the farm went on listing them forever. Population dropped, no grave was
// left (no cargo, so no loot to mark), and the building could never be worked
// again. Three separate consequences, all from one stale roster entry:
//
//   * Extractor.Workers.Count is checked against WorkerCap at assign time, so
//     the corpse permanently consumes a work slot;
//   * the WorkersBelow predicate reads the same count, so a Staff order sees a
//     fully-manned building and never re-staffs it;
//   * ProductionTickEvent reschedules while Workers.Count > 0, so the dead
//     building keeps ticking at zero rate forever.
//
// The MOVE path always released assignments (MoveIntent.CleanUpAssignment);
// the DEATH path never did. Both now go through WorkAssignment.Release.
public class GhostWorkerTests
{
    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord FarmAt = new(15, 10);
    private static readonly TileCoord HouseAt = new(16, 10);

    // A rich castle, a farm, and a house with an EMPTY larder beside it — the
    // shape that kills, because assigning a worker to the farm re-homes them
    // to that house ("home follows work") and it has nothing to feed them.
    private static Simulation BuildWorld(out Extractor farm, out House house)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 5000);

        farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 };
        world.AddStructure(farm);
        house = new House(HouseAt) { OwnerId = 0 };
        world.AddStructure(house);

        var cfg = new PopulationConfig();
        world.AddUnit(new Unit(1, FarmAt)
        {
            Role = UnitRole.Farmer, OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
            DeathTick = 70L * cfg.TicksPerYear, DeathSeq = 1,
        });
        // A second citizen, safe at the castle. Not decoration: without them
        // the starving farmer is the realm's LAST unit, so their death is
        // extinction, extinction is defeat (docs/sieges-and-conquest.md), and
        // a defeated player's intents are all rejected at the wrapper — which
        // silently turned "the slot is reusable" into "the player no longer
        // exists". The fixture predates extinction-implies-defeat.
        world.AddUnit(new Unit(9, Keep)
        {
            Role = UnitRole.None, OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
            DeathTick = 70L * cfg.TicksPerYear, DeathSeq = 2,
        });
        world.NextUnitId = 10;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 5);
    }

    [Fact]
    public void AWorkerWhoDiesOnShift_ComesOffTheExtractorsRoster()
    {
        var sim = BuildWorld(out var farm, out _);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.Single(farm.Workers);

        // Starve them: famine begins within a period, first death 3 days later.
        sim.Run(6 * Time.Day);

        Assert.False(sim.World.Units.ContainsKey(1), "the fixture needs the worker to actually die");
        Assert.Empty(farm.Workers);          // ← the fix: no ghost on the payroll
    }

    [Fact]
    public void TheWorkSlotIsReusableAfterADeath()
    {
        // The consequence that actually ends a kingdom: a farm whose slots are
        // held by corpses can never be worked again, by hand or by automation.
        var sim = BuildWorld(out var farm, out _);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        sim.Run(6 * Time.Day);
        Assert.False(sim.World.Units.ContainsKey(1));

        // A replacement can take the vacated slot.
        var cfg = new PopulationConfig();
        sim.World.AddUnit(new Unit(2, FarmAt)
        {
            Role = UnitRole.Farmer, OwnerId = 0,
            BornTick = sim.Now - 25 * cfg.TicksPerYear,
        });
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(FarmAt, new[] { 2 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.Contains(2, farm.Workers);
        Assert.Equal(Activity.Working, sim.World.Units[2].Activity);
    }

    [Fact]
    public void UnassignPurgesAGhost_SoAnOldSaveCanBeRepaired()
    {
        // The repair path, for worlds saved before the release fix (and as
        // defence in depth for any future route that forgets).
        var sim = BuildWorld(out var farm, out _);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        // Forge the ghost directly: remove the unit WITHOUT the death pipeline,
        // exactly the state an old save carries.
        sim.World.Units.Remove(1);
        Assert.Contains(1, farm.Workers);

        sim.SubmitIntent(sim.Now, new UnassignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.Empty(farm.Workers);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        Assert.False(ev.Outcome.IsRejected);
    }

    [Fact]
    public void HomeFollowsWork_MovesAWorkerOntoAnUnstockedLarder()
    {
        // NOT a bug — the documented M19 rule — but pinned because it is the
        // trap behind the report: assigning a worker to a building silently
        // moves their food source from a full castle to whatever house is
        // within HomeAssignRadius, stocked or not. A house with no supply line
        // is a death sentence for everyone re-homed into it.
        var sim = BuildWorld(out _, out var house);
        Assert.Null(sim.World.Units[1].Home);                 // castle-fed

        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        Assert.Equal(HouseAt, sim.World.Units[1].Home);        // now house-fed
        sim.Run(Time.Day);
        Assert.True(FoodConsumption.CurrentLevel(house, sim.World, sim.Now) < 0,
            "the re-homed worker is now eating from an empty larder");
        Assert.True(((Castle)sim.World.Structures[Keep]).AmountOf(Resource.Food) > 4000,
            "...while the castle three tiles away is still full");
    }
}
