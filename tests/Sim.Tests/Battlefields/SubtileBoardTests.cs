using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// M42 phase 4 (docs/subtile-movement.md): a board reads the subtiles units already
// stand on. Nothing is seated, sheltered or left waiting outside an edge.
public class SubtileBoardTests
{
    private const int Blue = 0, Red = 1;
    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord Field = new(5, 5);
    private static readonly TileCoord West = new(4, 5);

    private static GameWorld World()
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        w.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return w;
    }

    private static int _nextId = 11000;
    private static Unit Put(GameWorld w, int owner, TileCoord at, Subtile on, UnitRole role = UnitRole.Soldier)
    {
        var u = new Unit(_nextId++, at) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        u.Subtile = on;
        return u;
    }

    private static long RunUntil(Simulation sim, Func<bool> done, long limit = 3000)
    {
        for (var t = sim.Now + 1; t <= limit; t++)
        {
            sim.Run(until: t);
            if (done()) return t;
        }
        Assert.Fail("condition never held");
        return -1;
    }

    [Fact]
    public void ABattle_OpensWithEveryoneWhereTheyStood_EvenWhenTheyShareASubtile()
    {
        var w = World();
        var blue = Put(w, Blue, Field, new Subtile(2, 2));
        var red = Put(w, Red, Field, new Subtile(2, 2));       // already in one subtile: a duel from the start
        var bystander = Put(w, Blue, Field, new Subtile(0, 3), UnitRole.Archer);
        var sim = new Simulation(w, seed: 1);
        CombatTrigger.MaybeBeginCombatOnTile(sim, Field);
        Assert.True(w.Battlefields.ContainsKey(Field));
        Assert.Equal(new Subtile(2, 2), blue.Subtile);
        Assert.Equal(new Subtile(2, 2), red.Subtile);
        Assert.Equal(new Subtile(0, 3), bystander.Subtile);
        Assert.All(new[] { blue, red, bystander }, u => Assert.NotNull(u.Board));
    }
}
