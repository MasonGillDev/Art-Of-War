using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Engagement pin (CombatTrigger.PinBelligerents + no-progress guard).
// See docs/combat-engagement-pin.md.
//
// The pin must hold even when a hop is cheaper than RoundIntervalTicks —
// the exact condition under which the pre-pin trigger let units march
// through. Tests pave row 5 with max-condition road so each hop costs 4
// ticks (grassland 10 × 34%); RoundInterval = 30 puts round 1 well after
// the hop the attacker would have taken without the pin.
public class CombatEngagementPinTests
{
    private const long RoundInterval = 30;
    private const int BaseHealth = 10;
    private const int RoadRow = 5;
    private const int GridSize = 12;

    private static (Simulation sim, GameWorld world) MakeWorld()
    {
        var grid = new TileGrid(GridSize, GridSize, Biome.Grassland);
        var world = new GameWorld(
            grid,
            new DiplomacyConfig(),
            new CombatConfig(RoundIntervalTicks: RoundInterval));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        var sim = new Simulation(world, seed: 0xC0F);
        return (sim, world);
    }

    private static void PaveRow5(GameWorld world)
    {
        for (var x = 0; x < world.Grid.Width; x++)
            world.Roads[new TileCoord(x, RoadRow)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
    }

    [Fact]
    public void MovingUnit_OnRoad_IsPinned_AndTakesDamage()
    {
        // Defender on (5,5); attacker walks from (1,5) toward (9,5) along
        // a paved row. Without the pin the attacker would walk through
        // (5,5) at tick 16 and reach (9,5) at tick 32 — long before
        // round 1 at tick 46.
        var (sim, world) = MakeWorld();
        PaveRow5(world);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.AddUnit(new Unit(100, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
        var attacker = world.AddUnit(new Unit(200, new TileCoord(1, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });

        sim.SubmitIntent(sim.Now, new MoveIntent(200, new TileCoord(9, RoadRow)) { PlayerId = 1 });
        // Run past round 2 (rounds at 46, 76). Each round deals 1 damage
        // to each side at BaseHealth=10 / BasePower=1.
        sim.Run(until: 100);

        Assert.Equal(new TileCoord(5, RoadRow), attacker.Position);
        Assert.Null(attacker.PathRemaining);
        Assert.Null(attacker.NextArrivalSeq);
        Assert.True(world.CombatStates.ContainsKey(new TileCoord(5, RoadRow)),
            "fight should still be active");
        Assert.True(attacker.Health < BaseHealth,
            $"expected attacker to take damage; Health={attacker.Health}");
        Assert.True(world.Units[100].Health < BaseHealth,
            $"expected defender to take damage; Health={world.Units[100].Health}");
    }

    [Fact]
    public void MovingGroup_OnRoad_IsPinned_AtBlockade()
    {
        // A two-unit owner-1 group moves along row 5 into a single
        // owner-0 defender on (5,5). The group's PathRemaining and
        // MovementEpoch must both be cleared so any queued GroupArrival
        // fences and the group stops on the contested tile.
        var (sim, world) = MakeWorld();
        PaveRow5(world);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.AddUnit(new Unit(100, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
        world.AddUnit(new Unit(200, new TileCoord(1, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });
        world.AddUnit(new Unit(201, new TileCoord(1, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });

        // Form the group at its current tile so it goes straight to Idle.
        sim.SubmitIntent(sim.Now, new FormGroupIntent(new[] { 200, 201 }, new TileCoord(1, RoadRow)) { PlayerId = 1 });
        sim.Run(until: 0);
        var gid = world.Units[200].GroupId!.Value;
        Assert.Equal(GroupState.Idle, world.Groups[gid].State);

        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(9, RoadRow)) { PlayerId = 1 });
        sim.Run(until: 100);

        var group = world.Groups[gid];
        Assert.Equal(new TileCoord(5, RoadRow), group.Position);
        Assert.Equal(GroupState.Idle, group.State);
        Assert.Null(group.PathRemaining);
        Assert.Null(group.NextArrivalSeq);
        Assert.Equal(new TileCoord(5, RoadRow), world.Units[200].Position);
        Assert.Equal(new TileCoord(5, RoadRow), world.Units[201].Position);
    }

    [Fact]
    public void Neutral_PassesThroughActiveCombat_Unpinned()
    {
        // Three owners. 0–1 hostile, 0–2 and 1–2 stay neutral. Pre-stage
        // a 0-vs-1 fight on (5,5) and walk a neutral owner-2 unit along
        // row 5 through the contested tile. The neutral must not be
        // pinned and must complete its path.
        var (sim, world) = MakeWorld();
        world.Players[2] = new Player(2);
        PaveRow5(world);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.AddUnit(new Unit(100, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
        world.AddUnit(new Unit(200, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });
        // Start combat directly so we can isolate the neutral's behavior.
        CombatTrigger.MaybeBeginCombatOnTile(sim, new TileCoord(5, RoadRow));
        Assert.True(world.CombatStates.ContainsKey(new TileCoord(5, RoadRow)));

        var neutral = world.AddUnit(new Unit(300, new TileCoord(1, RoadRow)) { Role = UnitRole.Builder, OwnerId = 2 });
        sim.SubmitIntent(sim.Now, new MoveIntent(300, new TileCoord(9, RoadRow)) { PlayerId = 2 });
        // 8 road hops × 4 ticks = 32 ticks to reach (9,5).
        sim.Run(until: 40);

        Assert.Equal(new TileCoord(9, RoadRow), neutral.Position);
        Assert.Null(neutral.PathRemaining);
        Assert.Equal(BaseHealth, neutral.Health);
    }

    [Fact]
    public void Reinforcement_OnRoad_IsPinned_DuringActiveCombat()
    {
        // 1 (A) vs 1 (B) on (5,5). A second A unit marches in along row 5
        // toward (9,5). The pin runs even though combat is already active
        // — the reinforcement must stop on the contested tile, not march
        // through.
        var (sim, world) = MakeWorld();
        PaveRow5(world);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.AddUnit(new Unit(100, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
        world.AddUnit(new Unit(200, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });
        CombatTrigger.MaybeBeginCombatOnTile(sim, new TileCoord(5, RoadRow));

        var reinforcement = world.AddUnit(new Unit(101, new TileCoord(0, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
        sim.SubmitIntent(sim.Now, new MoveIntent(101, new TileCoord(9, RoadRow)) { PlayerId = 0 });
        // 5 road hops × 4 ticks ≈ tick 20 (a little more once banded
        // crowding kicks in on the contested tile — still < round 2).
        sim.Run(until: 40);

        Assert.Equal(new TileCoord(5, RoadRow), reinforcement.Position);
        Assert.Null(reinforcement.PathRemaining);
        Assert.Null(reinforcement.NextArrivalSeq);
    }

    [Fact]
    public void TwoPower0Forces_NoProgressGuard_EndsCombat()
    {
        // Two hostile units with EffectivePower zeroed via a -1 buff.
        // Without the no-progress guard CombatRoundEvent would reschedule
        // forever (each round deals zero damage, post-check still finds a
        // hostile pair). With the guard combat ends on round 1.
        var (sim, world) = MakeWorld();
        var tile = new TileCoord(5, RoadRow);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        var a = world.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        var b = world.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        a.Buffs.Add(new Buff("test-disarmed", PowerModifier: -1, HealthModifier: 0, ExpiresAt: null));
        b.Buffs.Add(new Buff("test-disarmed", PowerModifier: -1, HealthModifier: 0, ExpiresAt: null));

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        Assert.True(world.CombatStates.ContainsKey(tile));

        // Round 1 fires at tick RoundInterval. Run several rounds' worth —
        // without the guard CombatStates would still be populated.
        sim.Run(until: RoundInterval * 5);

        Assert.False(world.CombatStates.ContainsKey(tile),
            "no-progress guard should have ended combat after round 1");
        Assert.Equal(BaseHealth, a.Health);
        Assert.Equal(BaseHealth, b.Health);
    }

    [Fact]
    public void Twin_PinScenario_Deterministic()
    {
        // Twin-run hash check over the road-pin scenario. Pin
        // touches per-unit anchors + epochs; the hash catches any
        // nondeterminism the determinism contract would otherwise miss.
        Simulation Run()
        {
            var (sim, world) = MakeWorld();
            PaveRow5(world);
            world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
            world.AddUnit(new Unit(100, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
            world.AddUnit(new Unit(200, new TileCoord(1, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });
            sim.SubmitIntent(sim.Now, new MoveIntent(200, new TileCoord(9, RoadRow)) { PlayerId = 1 });
            sim.Run(until: 500);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }
}
