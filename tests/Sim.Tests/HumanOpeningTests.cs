using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server;

namespace Sim.Tests;

// The human opening (docs/human-opening.md): the human seat's roster is the
// tutorial — a king, two builders, two haulers, two farmers, a lumberjack and
// eleven citizens beside an empty House, with no School. AI seats keep the
// two-of-each start beside a School.
public class HumanOpeningTests
{
    private static Simulation World(bool humanSeat) =>
        new(WorldFactory.Build(new ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = humanSeat }).Spec, seed: 1);

    private static int Count(Simulation s, int owner, UnitRole role) =>
        s.World.Units.Values.Count(u => u.OwnerId == owner && u.Role == role);

    private static IEnumerable<Structure> Owned(Simulation s, int owner, StructureKind kind) =>
        s.World.Structures.Values.Where(st => st.OwnerId == owner && st.Kind == kind);

    [Fact]
    public void TheHumanSeat_StartsWithTheTeachingRoster()
    {
        var sim = World(humanSeat: true);

        Assert.Equal(19, sim.World.Units.Values.Count(u => u.OwnerId == 0));
        Assert.Equal(1, Count(sim, 0, UnitRole.King));
        Assert.Equal(2, Count(sim, 0, UnitRole.Builder));
        Assert.Equal(2, Count(sim, 0, UnitRole.Hauler));
        Assert.Equal(2, Count(sim, 0, UnitRole.Farmer));
        Assert.Equal(1, Count(sim, 0, UnitRole.Lumberjack));
        Assert.Equal(11, Count(sim, 0, UnitRole.None));
        Assert.Equal(sim.World.Players[0].KingUnitId,
            sim.World.Units.Values.Single(u => u.OwnerId == 0 && u.Role == UnitRole.King).Id);
    }

    [Fact]
    public void TheHumanSeat_GetsAnEmptyHouse_AndNoSchool()
    {
        var sim = World(humanSeat: true);

        Assert.Empty(Owned(sim, 0, StructureKind.School));
        var house = (House)Assert.Single(Owned(sim, 0, StructureKind.House));
        // Empty: nobody calls it home, so nobody eats from its dry cache.
        Assert.Equal(0, house.ResidentCount);
        Assert.All(sim.World.Units.Values.Where(u => u.OwnerId == 0), u => Assert.Null(u.Home));
    }

    [Fact]
    public void AiSeats_KeepTwoOfEach_BesideASchool()
    {
        foreach (var sim in new[] { World(humanSeat: true), World(humanSeat: false) })
        {
            var owner = sim.World.Players[0].Progress is null ? 0 : 1;   // the AI-driven seat
            Assert.Equal(14, sim.World.Units.Values.Count(u => u.OwnerId == owner));
            Assert.Equal(1, Count(sim, owner, UnitRole.King));
            Assert.Equal(1, Count(sim, owner, UnitRole.Builder));   // the other wears the crown
            Assert.Equal(2, Count(sim, owner, UnitRole.Scout));
            Assert.Single(Owned(sim, owner, StructureKind.School));
            Assert.Empty(Owned(sim, owner, StructureKind.House));
        }
    }
}
