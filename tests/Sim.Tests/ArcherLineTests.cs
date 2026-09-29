using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.World;

namespace Sim.Tests;

// 2026-09-24 (the user's archer rule, docs/combat-model.md): a ranged unit
// fights from behind its own side's line. On a contested tile it takes no
// damage while any non-ranged unit of its owner still stands there; once the
// line falls, the rest of that round's damage spills onto the archers. They
// deal their full power throughout.
public class ArcherLineTests
{
    private const long RoundInterval = 10;
    private static readonly TileCoord Tile = new(10, 10);

    private static Simulation Fight(params (int Owner, UnitRole Role)[] units)
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 20, Height = 20,
            Diplomacy = new DiplomacyConfig(Delay: 50, ProposalExpiryTicks: 200),
            Combat = new CombatConfig(RoundIntervalTicks: RoundInterval),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        });
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        var id = 100;
        foreach (var (owner, role) in units)
            world.AddUnit(new Unit(id++, Tile) { Role = role, OwnerId = owner });
        var sim = new Simulation(world, seed: 1);
        CombatTrigger.MaybeBeginCombatOnTile(sim, Tile);
        return sim;
    }

    private static int Hp(UnitRole role) => UnitCombatCatalog.Spec(role).BaseHealth;
    private static int Pw(UnitRole role) => UnitCombatCatalog.Spec(role).BasePower;

    private static IEnumerable<Unit> Of(Simulation sim, int owner, UnitRole role) =>
        sim.World.Units.Values.Where(u => u.OwnerId == owner && u.Role == role);

    [Fact]
    public void TheCatalog_MarksArchersRanged_AndNobodyElse()
    {
        Assert.True(UnitCombatCatalog.Spec(UnitRole.Archer).Ranged);
        foreach (var role in new[] { UnitRole.Soldier, UnitRole.Bandit, UnitRole.None, UnitRole.Farmer, UnitRole.King })
            Assert.False(UnitCombatCatalog.Spec(role).Ranged);
    }

    [Fact]
    public void WhileTheLineStands_TheArcherTakesNothing_AndStillHits()
    {
        // Soldier + archer against two soldiers: 6 damage a round lands on the line.
        var sim = Fight((0, UnitRole.Soldier), (0, UnitRole.Archer), (1, UnitRole.Soldier), (1, UnitRole.Soldier));
        sim.Run(until: RoundInterval);

        var soldier = Assert.Single(Of(sim, 0, UnitRole.Soldier));
        var archer = Assert.Single(Of(sim, 0, UnitRole.Archer));
        Assert.Equal(Hp(UnitRole.Soldier) - 2 * Pw(UnitRole.Soldier), soldier.Health);
        Assert.Equal(Hp(UnitRole.Archer), archer.Health);
        // And the archer's power landed: the enemy took soldier + archer power.
        var hurt = Of(sim, 1, UnitRole.Soldier).OrderBy(u => u.Health).First();
        Assert.Equal(Hp(UnitRole.Soldier) - Pw(UnitRole.Soldier) - Pw(UnitRole.Archer), hurt.Health);
    }

    [Fact]
    public void WhenTheLineFalls_TheRestOfTheRoundSpillsOntoTheArchers()
    {
        // A lone citizen (10 hp) in front of an archer, against enough enemy
        // power to kill the citizen and have some left in the same round.
        var enemy = new List<(int, UnitRole)>();
        for (var i = 0; i < 4; i++) enemy.Add((1, UnitRole.Soldier));   // 12 damage a round
        var sim = Fight(new[] { (0, UnitRole.None), (0, UnitRole.Archer) }.Concat(enemy).ToArray());
        sim.Run(until: RoundInterval);

        Assert.Empty(Of(sim, 0, UnitRole.None));
        var archer = Assert.Single(Of(sim, 0, UnitRole.Archer));
        var spill = 4 * Pw(UnitRole.Soldier) - Hp(UnitRole.None);
        Assert.Equal(Hp(UnitRole.Archer) - spill, archer.Health);
    }

    [Fact]
    public void ArchersAlone_HaveNoLine_AndTakeDamageAsBefore()
    {
        var sim = Fight((0, UnitRole.Archer), (1, UnitRole.Soldier));
        sim.Run(until: RoundInterval);
        Assert.Equal(Hp(UnitRole.Archer) - Pw(UnitRole.Soldier), Assert.Single(Of(sim, 0, UnitRole.Archer)).Health);
    }

    [Fact]
    public void FourSoldiersAndTwoArchers_NowBeatSixBandits_WithBothArchersStanding()
    {
        // The stack the old lowest-health-first order lost (the archers died
        // first). Bandits are hostile to everyone.
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 20, Height = 20,
            Combat = new CombatConfig(RoundIntervalTicks: RoundInterval),
            FactionStarts = new[] { new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) } },
        });
        world.Players[Sim.Core.Bandits.BanditConstants.OwnerId] = new Player(Sim.Core.Bandits.BanditConstants.OwnerId);
        var id = 100;
        for (var i = 0; i < 4; i++) world.AddUnit(new Unit(id++, Tile) { Role = UnitRole.Soldier, OwnerId = 0 });
        for (var i = 0; i < 2; i++) world.AddUnit(new Unit(id++, Tile) { Role = UnitRole.Archer, OwnerId = 0 });
        for (var i = 0; i < 6; i++) world.AddUnit(new Unit(id++, Tile) { Role = UnitRole.Bandit, OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId });
        var sim = new Simulation(world, seed: 1);
        CombatTrigger.MaybeBeginCombatOnTile(sim, Tile);

        sim.Run(until: 100 * RoundInterval);

        Assert.DoesNotContain(sim.World.Units.Values, u => u.Role == UnitRole.Bandit);
        Assert.Equal(2, Of(sim, 0, UnitRole.Archer).Count());
    }
}
