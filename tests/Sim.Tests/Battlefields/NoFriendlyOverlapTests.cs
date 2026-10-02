using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// The landing crash (2026-10-01, "Owner 1 has two units on (1,0)"): a full tile
// plus one friend too many. Friends may pass through each other while WALKING, but
// none may come to rest on a friend's subtile, and a board must never be built
// with two of one side on one subtile. A unit with no room on the tile stays off
// the board and walks to the nearest free place.
public class NoFriendlyOverlapTests
{
    private const int Blue = 0, Red = 1;
    private const long Turn = 60;
    private static readonly TileCoord West = new(2, 0), Field = new(3, 0), East = new(4, 0);

    private static (Simulation sim, GameWorld world) Make()
    {
        var world = new GameWorld(new TileGrid(9, 1, Biome.Grassland), new DiplomacyConfig(), new CombatConfig(RoundIntervalTicks: Turn));
        world.Players[Blue] = new Player(Blue);
        world.Players[Red] = new Player(Red);
        world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return (new Simulation(world, seed: 5), world);
    }

    private static Unit Put(GameWorld w, int id, int owner, TileCoord tile, Subtile at)
    {
        var u = new Unit(id, tile) { Role = UnitRole.Soldier, OwnerId = owner };
        w.AddUnit(u);
        u.Subtile = at;
        return u;
    }

    // Field's sixteen subtiles, each held by a standing Blue soldier (ids 1..16).
    private static void FillField(GameWorld w)
    {
        var id = 1;
        foreach (var s in Subtile.All()) Put(w, id++, Blue, Field, s);
    }

    // Simulation.Run(until) doesn't advance Now past the last event, so the test keeps
    // its own clock (BattlefieldEntryTests' pattern).
    private sealed class Clock { public long Now; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Simulation, Clock> Clocks = new();

    private static void Tick(Simulation sim, long count)
    {
        var clock = Clocks.GetOrCreateValue(sim);
        for (var i = 0L; i < count; i++) sim.Run(until: ++clock.Now);
    }

    private static void RunTurns(Simulation sim, int turns) => Tick(sim, turns * Turn);

    // No two non-hostile units STANDING (not walking) on one subtile anywhere.
    private static void AssertNoRestingOverlap(GameWorld w)
    {
        var resting = w.Units.Values.Where(u => !u.IsWalking && !u.IsEmbarked && u.Subtile is not null)
            .GroupBy(u => (u.Position, u.Subtile!.Value, u.OwnerId))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(",", g.Select(u => u.Id))}")
            .ToList();
        Assert.True(resting.Count == 0, "friends resting on one subtile: " + string.Join("; ", resting));
    }

    [Fact]
    public void AFullTile_WithAFriendStandingOnAFriend_OpensABoard_WithoutThrowing()
    {
        var (sim, w) = Make();
        FillField(w);
        var extra = Put(w, 50, Blue, Field, new Subtile(1, 1));   // on soldier 6's subtile
        Put(w, 60, Red, Field, new Subtile(2, 1));                // a raider walks in

        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        RunTurns(sim, 3);

        Assert.Null(extra.Board);   // no room on the board for it
        AssertNoRestingOverlap(w);
    }

    [Fact]
    public void AFriendPassingThroughAFullTile_WhenABoardOpens_StaysOffIt_AndWalksOn()
    {
        var (sim, w) = Make();
        FillField(w);
        // A Blue walker passing through Field from West to East, on soldier 5's subtile.
        var walker = Put(w, 50, Blue, West, new Subtile(3, 1));
        sim.SubmitIntent(sim.Now, new MoveIntent(walker.Id, East) { PlayerId = Blue });
        for (var i = 0; i < 10_000 && walker.Position != Field; i++) Tick(sim, 1);
        Assert.True(walker.IsWalking, "fixture: the walker should be passing through");

        Put(w, 60, Red, Field, new Subtile(2, 2));
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Field);
        RunTurns(sim, 3);

        Assert.Null(walker.Board);
        AssertNoRestingOverlap(w);
    }

    [Fact]
    public void AHaltedWalk_NeverLeavesAUnitStandingOnAFriend()
    {
        var (sim, w) = Make();
        FillField(w);
        var stray = Put(w, 50, Blue, Field, new Subtile(1, 1));   // as if halted mid-pass

        Walk.Halt(sim, stray);
        Tick(sim, 20 * Turn);

        AssertNoRestingOverlap(w);
        Assert.NotEqual(Field, stray.Position);   // the tile is full: it settled next door
    }
}
