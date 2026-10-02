using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Server.Groups;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M49 — group stance (docs/m49-group-stance-spec.md): three choices, defined in code.
// Defensive companies of an army come to a member's fight within AidRadius and go home
// after; Passive ones neither help nor stay to fight; Aggressive ones charge what their
// owner can see within EngageRadius, never past the leash.
public class GroupStanceTests
{
    private static (Simulation sim, GameWorld world) MakeWorld()
    {
        var world = new GameWorld(new TileGrid(30, 16, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return (new Simulation(world, seed: 11), world);
    }

    private static IntentOutcome Do(Simulation sim, Intent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(until: sim.Now);
        return sim.ResolvedLog.OfType<IntentEvent>().Last(e => e.Intent == intent).Outcome;
    }

    private static void RunUntil(Simulation sim, Func<bool> done, long budget = 50_000, GroupStanceDriver? driver = null)
    {
        var end = sim.Now + budget;
        for (var t = sim.Now + 1; t <= end && !done(); t++)
        {
            sim.Run(until: t);
            driver?.Think(sim, t);
        }
        Assert.True(done(), "timed out");
    }

    // A company of `n` soldiers (ids from `first`) formed up at `at`, under `parent`.
    private static int Company(Simulation sim, GameWorld world, string name, int first, int n, TileCoord at, int? parent = null)
    {
        var ids = Enumerable.Range(first, n).ToArray();
        foreach (var id in ids) world.AddUnit(new Unit(id, at) { Role = UnitRole.Soldier });
        Assert.False(Do(sim, new CreateGroupIntent(name, ids, parent)).IsRejected);
        var gid = world.NextGroupId - 1;
        Assert.False(Do(sim, new MusterGroupIntent(gid, at)).IsRejected);
        RunUntil(sim, () => world.Groups[gid].State == GroupState.Idle);
        return gid;
    }

    private static int Army(Simulation sim, GameWorld world)
    {
        Assert.False(Do(sim, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true)).IsRejected);
        return world.NextGroupId - 1;
    }

    // An enemy that will fight (Advance) walks onto `tile`.
    private static Unit Attack(Simulation sim, GameWorld world, int id, TileCoord tile)
    {
        var foe = world.AddUnit(new Unit(id, tile) { Role = UnitRole.Soldier, OwnerId = 1 });
        foe.Doctrine = BattleDoctrine.Advance;
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        Assert.True(world.Battlefields.ContainsKey(tile));
        return foe;
    }

    [Fact]
    public void Stance_DecidesHowMembersFight_TheirOwnDoctrineStillWins()
    {
        var (sim, world) = MakeWorld();
        var gid = Company(sim, world, "Guard", 1, 2, new TileCoord(5, 5));
        var soldier = world.Units[1];
        Assert.Equal(GroupStance.Defensive, world.Groups[gid].Stance);   // the default
        Assert.Equal(DoctrineBehaviour.Hold, BattleDoctrine.Effective(world, soldier).Behaviour);

        Do(sim, new SetGroupStanceIntent(gid, GroupStance.Aggressive));
        Assert.Equal(DoctrineBehaviour.Advance, BattleDoctrine.Effective(world, soldier).Behaviour);
        Do(sim, new SetGroupStanceIntent(gid, GroupStance.Passive));
        Assert.Equal(DoctrineBehaviour.Withdraw, BattleDoctrine.Effective(world, soldier).Behaviour);

        soldier.Doctrine = BattleDoctrine.Hold;   // the player's own word for this unit
        Assert.Equal(DoctrineBehaviour.Hold, BattleDoctrine.Effective(world, soldier).Behaviour);

        // A dismissed group's members are free individuals: role defaults.
        Do(sim, new DismissGroupIntent(gid));
        Assert.Equal(BattleDoctrine.DefaultFor(UnitRole.Soldier), BattleDoctrine.Effective(world, world.Units[2]));
    }

    [Fact]
    public void SettingAnArmysStance_SetsEveryCompanyUnderIt()
    {
        var (sim, world) = MakeWorld();
        var army = Army(sim, world);
        var a = Company(sim, world, "A", 1, 2, new TileCoord(5, 5), army);
        var b = Company(sim, world, "B", 3, 2, new TileCoord(9, 5), army);
        Assert.False(Do(sim, new SetGroupStanceIntent(army, GroupStance.Aggressive)).IsRejected);
        Assert.All(new[] { army, a, b }, g => Assert.Equal(GroupStance.Aggressive, world.Groups[g].Stance));
    }

    [Fact]
    public void Defensive_TheArmyComesToAMembersFight_AndGoesHomeAfter()
    {
        var (sim, world) = MakeWorld();
        var army = Army(sim, world);
        var attacked = Company(sim, world, "Attacked", 1, 3, new TileCoord(10, 5), army);
        var near = Company(sim, world, "Near", 10, 3, new TileCoord(14, 5), army);
        var far = Company(sim, world, "Far", 20, 3, new TileCoord(22, 5), army);
        var idle = Company(sim, world, "Passive", 30, 3, new TileCoord(12, 9), army);
        Do(sim, new SetGroupStanceIntent(idle, GroupStance.Passive));

        Attack(sim, world, 90, new TileCoord(10, 5));

        Assert.Equal(new TileCoord(14, 5), world.Groups[near].ReturnTo);
        Assert.Equal(GroupState.Moving, world.Groups[near].State);
        Assert.Null(world.Groups[far].ReturnTo);        // beyond AidRadius
        Assert.Null(world.Groups[idle].ReturnTo);       // Passive never comes
        Assert.Equal(GroupState.Idle, world.Groups[far].State);

        RunUntil(sim, () => !world.Units.ContainsKey(90) && world.Battlefields.Count == 0);
        RunUntil(sim, () => world.Groups[near].ReturnTo is null && world.Groups[near].State == GroupState.Idle);
        Assert.Equal(new TileCoord(14, 5), world.Groups[near].Position);
        Assert.All(world.Groups[near].Members, id => Assert.Equal(new TileCoord(14, 5), world.Units[id].Position));
    }

    [Fact]
    public void Aggressive_ChargesWhatItCanSee_ButNotPastTheLeash()
    {
        var (sim, world) = MakeWorld();
        var gid = Company(sim, world, "Hunters", 1, 3, new TileCoord(5, 5));
        Do(sim, new SetGroupStanceIntent(gid, GroupStance.Aggressive));
        var driver = new GroupStanceDriver(thinkPeriodTicks: 1);

        // Out of the leash (7 tiles from where the group stands): left alone.
        var far = world.AddUnit(new Unit(80, new TileCoord(12, 5)) { Role = UnitRole.Soldier, OwnerId = 1 });
        Assert.NotNull(GroupStances.ChargeRefusal(world, world.Groups[gid], far, 80));
        driver.Think(sim, sim.Now);
        sim.Run(until: sim.Now);
        Assert.Null(world.Groups[gid].ReturnTo);

        // Within EngageRadius and in sight: charged.
        var near = world.AddUnit(new Unit(81, new TileCoord(7, 5)) { Role = UnitRole.Soldier, OwnerId = 1 });
        near.Doctrine = BattleDoctrine.Hold;
        driver.Think(sim, sim.Now + 1);
        sim.Run(until: sim.Now + 1);
        Assert.Equal(new TileCoord(5, 5), world.Groups[gid].ReturnTo);
        RunUntil(sim, () => world.Battlefields.ContainsKey(new TileCoord(7, 5)), driver: driver);
    }

    [Fact]
    public void ChargeIsRefused_ForAGroupThatIsNotAggressive()
    {
        var (sim, world) = MakeWorld();
        var gid = Company(sim, world, "Guard", 1, 2, new TileCoord(5, 5));
        world.AddUnit(new Unit(80, new TileCoord(6, 5)) { Role = UnitRole.Soldier, OwnerId = 1 });
        Assert.True(Do(sim, new ChargeGroupIntent(gid, 80)).IsRejected);
    }

    private static Simulation Scene()
    {
        var (sim, world) = MakeWorld();
        var army = Army(sim, world);
        Company(sim, world, "Attacked", 1, 3, new TileCoord(10, 5), army);
        Company(sim, world, "Near", 10, 3, new TileCoord(14, 5), army);
        Attack(sim, world, 90, new TileCoord(10, 5));
        return sim;
    }

    [Fact]
    public void TwinRun_AndMidFightRestore_EndTheSame()
    {
        var a = Scene(); var end = a.Now + 20_000; a.Run(until: end);
        var b = Scene(); b.Run(until: end);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));

        var mid = Scene();
        mid.Run(until: mid.Now + 120);
        Assert.Contains(mid.World.Groups.Values, g => g.ReturnTo is not null);
        var restored = Snapshot.Restore(Snapshot.Serialize(mid), seed: 11);
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
        mid.Run(until: end);
        restored.Run(until: end);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(mid));
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
    }
}
