using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// Siege from the board (docs/structure-footprints.md, Update 2026-10-01): a unit
// hostile to the tile's structure that stands still and fights nobody works on it.
// A defender on the wall keeps the board open but no longer keeps the castle whole.
public class BoardSiegeTests
{
    private const int Blue = 0, Red = 1;
    private const long Turn = 60;
    private static readonly BattleConfig Cfg = new();
    private static readonly TileCoord Keep = new(10, 10);

    private static bool RedBesieges(int owner) => owner == Red;

    private static int SoldierHp => UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
    private static BoardUnit Soldier(int id, int owner, int x, int y, int damage = 3) =>
        new(id, owner, new Subtile(x, y), SoldierHp, damage, false);
    private static BoardUnit Archer(int id, int owner, int x, int y) =>
        new(id, owner, new Subtile(x, y), UnitCombatCatalog.Spec(UnitRole.Archer).BaseHealth, 5, true);

    private static SubtileLayer CastleLayer() => Footprints.For(new Castle(Keep) { OwnerId = Blue });
    private static BoardState Board(params BoardUnit[] units) => new(CastleLayer(), units, (a, b) => a != b);
    private static readonly Dictionary<int, PlannedStep> NoSteps = new();

    // ---- the rule, on the board ----------------------------------------------------------

    [Fact]
    public void AnIdleBesieger_WorksOnTheStructure_OneInADuelDoesNot()
    {
        // Blue holds the west wall; Red 2 stands idle in the courtyard, Red 3 duels Blue on the wall.
        var b = Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 1, 1, damage: 4), Soldier(3, Red, 0, 1));
        var r = TurnResolver.Resolve(b, NoSteps, Cfg, RedBesieges);
        Assert.Equal(new[] { 2 }, r.Besiegers);
        Assert.Equal(4, r.StructureDamage);
        Assert.Equal(2, r.Hits.Count);   // the duel still fights
    }

    [Fact]
    public void ABesiegerThatMoved_DidNoWorkThisTurn()
    {
        var b = Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 1, 1));
        var steps = new Dictionary<int, PlannedStep> { [2] = new(new Subtile(2, 1)) };
        var r = TurnResolver.Resolve(b, steps, Cfg, RedBesieges);
        Assert.Empty(r.Besiegers);
        Assert.Equal(0, r.StructureDamage);
    }

    [Fact]
    public void AnArcherWithATarget_Shoots_WithoutOne_Works()
    {
        var shooting = Board(Soldier(1, Blue, 0, 1), Archer(2, Red, 1, 1));   // the wall is beside it
        var r1 = TurnResolver.Resolve(shooting, NoSteps, Cfg, RedBesieges);
        Assert.Single(r1.Hits);
        Assert.Empty(r1.Besiegers);

        var alone = Board(Soldier(1, Blue, 3, 3), Archer(2, Red, 1, 1));      // the far corner tower: out of reach
        var r2 = TurnResolver.Resolve(alone, NoSteps, Cfg, RedBesieges);
        Assert.Empty(r2.Hits);
        Assert.Equal(new[] { 2 }, r2.Besiegers);
        Assert.Equal(5, r2.StructureDamage);
    }

    [Fact]
    public void TheDefenderNeverWorksOnItsOwnWalls_AndNothingToBesiegeMeansNoWork()
    {
        var b = Board(Soldier(1, Blue, 1, 1), Soldier(2, Red, 2, 2));
        Assert.Equal(new[] { 2 }, TurnResolver.Resolve(b, NoSteps, Cfg, RedBesieges).Besiegers);
        Assert.Empty(TurnResolver.Resolve(b, NoSteps, Cfg).Besiegers);
        Assert.Equal(0, TurnResolver.Resolve(b, NoSteps, Cfg).StructureDamage);
    }

    [Fact]
    public void AStandingBesieger_IsWork_SoTheBoardDoesNotSuspend()
    {
        var b = Board(Soldier(1, Blue, 0, 1), Soldier(2, Red, 2, 2));
        Assert.False(TurnResolver.HasWork(b, NoSteps));
        Assert.True(TurnResolver.HasWork(b, NoSteps, RedBesieges));
    }

    // ---- in the world ------------------------------------------------------------------------

    private static Simulation CastleWorld(out Unit blue, out Unit red)
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Combat = new CombatConfig(RoundIntervalTicks: Turn),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        world.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        blue = new Unit(1, Keep) { Role = UnitRole.Soldier, OwnerId = Blue };
        red = new Unit(2, new TileCoord(Keep.X, Keep.Y - 1)) { Role = UnitRole.Soldier, OwnerId = Red };   // north: the gate side
        world.AddUnit(blue);
        world.AddUnit(red);
        var sim = new Simulation(world, seed: 5);
        sim.SubmitIntent(0, new MoveIntent(2, Keep) { PlayerId = Red });
        return sim;
    }

    private static void Order(Simulation sim, int id, int owner, BattleOrderKind kind, Subtile target = default) =>
        sim.SubmitIntent(sim.Now, new SetBattleOrderIntent(id, (byte)kind, target) { PlayerId = owner });

    // Red walks in through the gap; Blue climbs the west wall and holds there; Red holds in
    // the courtyard. Nobody fights, and the castle loses Red's power every turn.
    private static Simulation Besieged(out Unit blue, out Unit red, out Battlefield bf)
    {
        var sim = CastleWorld(out blue, out red);
        for (var t = 1; t <= 400 && sim.World.Battlefields.Count == 0; t++) sim.Run(until: t);
        bf = Assert.Single(sim.World.Battlefields.Values);
        Order(sim, 1, Blue, BattleOrderKind.MoveTo, new Subtile(0, 1));
        Order(sim, 2, Red, BattleOrderKind.Hold);
        // Let Blue reach the wall (at most two beats from anywhere in the courtyard).
        var settle = Sim.Core.Battlefields.Battlefields.NextBeat(sim.World, sim.Now) + 2 * Turn;
        sim.Run(until: settle);
        Assert.Equal(new Subtile(0, 1), blue.Subtile);
        return sim;
    }

    [Fact]
    public void AtACastle_ADefenderOnTheWall_KeepsTheBoardOpen_ButNoLongerKeepsTheCastleWhole()
    {
        var sim = Besieged(out _, out var red, out var bf);
        var castle = sim.World.Structures[Keep];
        var before = castle.Health;
        var work = CombatRules.EffectivePower(sim.World, red, sim.Now);
        Assert.True(work > 0);

        var beat = Sim.Core.Battlefields.Battlefields.NextBeat(sim.World, sim.Now);
        sim.Run(until: beat);
        Assert.Equal(before - work, castle.Health);
        Assert.False(bf.Suspended);                       // a besieger standing is work
        Assert.Equal(new[] { red.Id }, bf.LastTurn!.Result.Besiegers);
        sim.Run(until: beat + Turn);
        Assert.Equal(before - 2 * work, castle.Health);
        Assert.True(sim.World.Battlefields.ContainsKey(Keep));   // still contested: the defender is on the wall
    }

    [Fact]
    public void WhenTheBesiegersBringItDown_TheCastleIsRubble_AndItsOwnerDefeated()
    {
        var sim = Besieged(out _, out var red, out _);
        var castle = sim.World.Structures[Keep];
        castle.Health = CombatRules.EffectivePower(sim.World, red, sim.Now);   // one more turn's work
        var beat = Sim.Core.Battlefields.Battlefields.NextBeat(sim.World, sim.Now);
        sim.Run(until: beat + 1);
        Assert.IsType<Rubble>(sim.World.Structures[Keep]);
        Assert.True(sim.World.Players[Blue].Defeated);
    }
}
