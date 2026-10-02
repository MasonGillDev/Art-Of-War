using Sim.Core.Bandits;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Bandits;

namespace Sim.Tests;

// M43 (docs/fix-combat-m43.md) — the bandit brain and the battlefield: it leaves a fight to
// the board (no marches for a unit on one, doctrine set once), never sends an order that
// didn't take twice, and joins a fight at its prize only at fair odds.
public class BanditBrainBoardTests
{
    private static readonly TileCoord Here = new(20, 20), Prize = new(22, 20);
    private const long Think = 10;

    private static Simulation Make(out GameWorld world)
    {
        world = new GameWorld(new TileGrid(64, 64, Biome.Grassland), new DiplomacyConfig(), new CombatConfig(RoundIntervalTicks: 600));
        world.Players[0] = new Player(0);
        world.Players[BanditConstants.OwnerId] = new Player(BanditConstants.OwnerId);
        world.AddStructure(new Castle(new TileCoord(5, 5)) { OwnerId = 0 });
        var stock = world.AddStructure(new Stockpile(Prize) { OwnerId = 0 });
        stock.Deposit(Resource.Wood, 10_000);
        return new Simulation(world, seed: 0xB1D);
    }

    private static BanditDriver Driver() => new(new BanditConfig
    {
        StructuresPerParty = 1000, ThinkPeriodTicks = Think, SpawnGraceTicks = long.MaxValue / 4, Seed = 3,
    });

    private static Unit Bandit(GameWorld w, int id, TileCoord at)
    {
        var u = new Unit(id, at) { Role = UnitRole.Bandit, OwnerId = BanditConstants.OwnerId, BornTick = 0 };
        w.AddUnit(u);
        return u;
    }

    private static Unit Soldier(GameWorld w, int id, TileCoord at)
    {
        var u = new Unit(id, at) { Role = UnitRole.Soldier, OwnerId = 0 };
        w.AddUnit(u);
        return u;
    }

    // One tick at a time, thinking at each driver period.
    private static long Drive(Simulation sim, BanditDriver driver, long from, long until)
    {
        for (var t = from; t <= until; t++)
        {
            sim.Run(until: t);
            if (t % Think == 0) driver.Think(sim, t);
        }
        return until;
    }

    private static List<string> Log(Simulation sim, string contains) =>
        sim.ResolvedLog.Select(e => e.Describe()).Where(d => d.Contains(contains)).ToList();

    [Fact]
    public void Driver_DoesNotOrderUnitsOnABoard()
    {
        var sim = Make(out var w);
        var bandit = Bandit(w, 100, Here);
        Soldier(w, 1, Here);
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Here);   // a fight on the tile the bandit stands on
        Assert.NotNull(bandit.Board);

        Drive(sim, Driver(), 0, 4 * Think);

        Assert.Empty(Log(sim, "MoveIntent(unit=100"));                       // the board has it: no march overrides its doctrine
    }

    [Fact]
    public void Driver_SetsDoctrineOncePerUnit_WhileItStaysOnABoard()
    {
        var sim = Make(out var w);
        var bandit = Bandit(w, 100, Here);
        Soldier(w, 1, Here);
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Here);

        Drive(sim, Driver(), 0, 5 * Think);

        Assert.NotNull(bandit.Board);                                        // still fighting: the board had no turn yet
        Assert.Single(Log(sim, "SetBattleDoctrine(unit=100"));               // once, not every think
        Assert.Equal(DoctrineBehaviour.Advance, bandit.Doctrine!.Value.Behaviour);
        Assert.True(bandit.Doctrine.Value.WithdrawBelow > 0);                // with the brain's withdraw threshold
    }

    [Fact]
    public void Raid_DoesNotJoinAFightAtBadOdds_ButDoesWhenTheOddsAreFair()
    {
        // Four soldiers hold the prize tile against one bandit: a board is open there.
        var sim = Make(out var w);
        for (var i = 0; i < 4; i++) Soldier(w, 1 + i, Prize);
        Bandit(w, 200, Prize);
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Prize);
        Assert.True(w.Battlefields.ContainsKey(Prize));
        Bandit(w, 100, Here);
        Bandit(w, 101, Here);

        Drive(sim, Driver(), 0, 4 * Think);

        Assert.Empty(Log(sim, "MoveIntent(unit=100 -> 22,20"));              // two bandits don't walk onto four soldiers
        Assert.Empty(Log(sim, "MoveIntent(unit=101 -> 22,20"));
    }

    [Fact]
    public void Raid_JoinsAFightAtItsPrize_WhenThePartyIsStrongEnough()
    {
        var sim = Make(out var w);
        Soldier(w, 1, Prize);
        Bandit(w, 200, Prize);
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Prize);
        Assert.True(w.Battlefields.ContainsKey(Prize));
        for (var i = 0; i < 4; i++) Bandit(w, 100 + i, Here);                // four bandits against one soldier

        Drive(sim, Driver(), 0, 4 * Think);

        Assert.NotEmpty(Log(sim, "MoveIntent(unit=100 -> 22,20"));
    }

    [Fact]
    public void Driver_DoesNotSendTheSameOrderTwice_WhenThePlaceHasNoRoom()
    {
        var sim = Make(out var w);
        // Sixteen bandits stand on the prize tile: a bandit's side is full there, so a walk to
        // stand on it finds no free subtile and does nothing.
        for (var i = 0; i < Subtile.Count; i++) Bandit(w, 300 + i, Prize);
        Bandit(w, 100, Here);
        Bandit(w, 101, Here);

        Drive(sim, Driver(), 0, 4 * Think);

        Assert.True(Log(sim, "MoveIntent(unit=100 -> 22,20").Count <= 1, "the same order went out again after it didn't take");
        Assert.True(Log(sim, "MoveIntent(unit=101 -> 22,20").Count <= 1);
    }
}
