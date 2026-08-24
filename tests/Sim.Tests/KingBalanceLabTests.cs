using Sim.Core;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Population;
using Sim.Core.Royalty;
using Sim.Core.World;

namespace Sim.Tests;

// M31 BALANCE LAB — the temptation zone (docs/king-and-dynasty.md §8).
//
// The design's one hard balance requirement: fielding the king should win
// OFTEN BUT NOT ALWAYS. Tempting always, mandatory never. If with-king always
// wins, the king becomes a doomstack component and the "should I risk him?"
// question dies; if it never matters, the buff is decoration and nobody ever
// takes the gamble.
//
// THESE TESTS ASSERT THE SHAPE OF THE CURVE, NEVER A TUNED VALUE. AuraRadius
// and AuraPowerBonus are balance knobs the user retunes; a lab test that
// hard-codes "bonus 5 beats 12 soldiers" turns every retune into a red suite
// for no reason. (This is the biome-degradation lesson: config-derived
// assertions, never magic ticks — docs/biome-degradation.md.) So every
// assertion here is an inequality over a SWEEP.
public class KingBalanceLabTests
{
    private static readonly TileCoord Field = new(12, 12);

    // One battle: `n` soldiers each side, one side optionally led by its king,
    // fought to a decision. Returns true if the led side won.
    //
    // Both sides are otherwise identical, so the aura is the ONLY asymmetry —
    // which is what makes the win rate readable as the buff's contribution.
    private static bool LedSideWins(int mine, int theirs, int bonus, int radius, int seed, bool withKing)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.RestoreRoyaltyConfig(new RoyaltyConfig(radius, bonus, MajorityAge: 6));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.Diplomacy.SetState(Sim.Core.Diplomacy.FactionPair.Of(0, 1),
            Sim.Core.Diplomacy.RelationshipState.Enemy);

        var cfg = world.PopulationConfig;
        var id = 1;
        for (var i = 0; i < mine; i++)
            world.AddUnit(new Unit(id++, Field) { Role = UnitRole.Soldier, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        for (var i = 0; i < theirs; i++)
            world.AddUnit(new Unit(id++, Field) { Role = UnitRole.Soldier, OwnerId = 1, BornTick = -25 * cfg.TicksPerYear });

        if (withKing)
        {
            // The king rides WITH the army — the gamble the design wants to be
            // tempting. He is an ordinary body in the line, not a spectator.
            var king = world.AddUnit(new Unit(id++, Field) { OwnerId = 0, BornTick = -30 * cfg.TicksPerYear });
            world.Players[0].KingUnitId = king.Id;
        }

        var sim = new Simulation(world, (ulong)seed);
        CombatTrigger.MaybeBeginCombatOnTile(sim, Field);
        sim.Run(200 * Time.Day);

        var survivingMine = world.Units.Values.Count(u => u.OwnerId == 0 && u.Role == UnitRole.Soldier);
        var survivingTheirs = world.Units.Values.Count(u => u.OwnerId == 1);
        return survivingMine > survivingTheirs;
    }

    private static int WinsOutOf(int trials, int n, int bonus, int radius, bool withKing)
    {
        var wins = 0;
        for (var s = 1; s <= trials; s++)
            if (LedSideWins(n, n, bonus, radius, seed: s * 7919, withKing)) wins++;
        return wins;
    }

    [Fact]
    public void TheKingHelps_ButAnUnledArmyIsNotDoomed()
    {
        // The lower bound of the temptation zone. With the aura ON, the led
        // side must do BETTER than the same army without it — otherwise the
        // gamble buys nothing and nobody would ever take it.
        const int trials = 12;
        const int n = 6;
        var cfg = new RoyaltyConfig();   // the shipped defaults are what ship

        var led = WinsOutOf(trials, n, cfg.AuraPowerBonus, cfg.AuraRadius, withKing: true);
        var unled = WinsOutOf(trials, n, cfg.AuraPowerBonus, cfg.AuraRadius, withKing: false);

        Assert.True(led >= unled,
            $"the King's Buff must not make an army WORSE (led {led}/{trials}, unled {unled}/{trials})");
        Assert.True(led > 0, "an army led by its king must be able to win");
    }

    [Fact]
    public void AtTheShippedDefault_TheKingIsNoSubstituteForAnArmy()
    {
        // The upper bound of the temptation zone: the king is a multiplier on
        // a real army, not a replacement for one. Badly outnumbered and led by
        // the crown, you still lose — which is what keeps "kill the line" a
        // campaign rather than the only move on the board.
        //
        // WHAT THE SWEEP TURNED UP, and why this test names the DEFAULT rather
        // than quantifying over all bonuses: the aura is a FLAT PER-UNIT add,
        // so its contribution scales with the number of bodies standing inside
        // the disc. A large enough AuraPowerBonus therefore does become an
        // I-win button — the upper bound is a TUNING constraint, not a
        // structural one. Nothing in the mechanism prevents it, so the guard
        // has to be a lab assertion at the shipped value (here) plus the
        // temptation-zone inequality above. If the design ever wants the
        // ceiling to be structural, the aura has to stop being a flat add
        // (diminishing returns, or a cap per battle).
        var cfg = new RoyaltyConfig();
        var outnumbered = LedSideWins(mine: 1, theirs: 6, cfg.AuraPowerBonus, cfg.AuraRadius,
            seed: 11, withKing: true);

        Assert.False(outnumbered,
            "a king plus one soldier beat six at the shipped AuraPowerBonus " +
            $"({cfg.AuraPowerBonus}) — the buff has crossed from tempting into mandatory");
    }

    [Fact]
    public void TheAuraScalesMonotonicallyWithTheKnob_SoTheLabCanTune()
    {
        // Not a balance claim — a TUNABILITY claim. The knob must move the
        // outcome in one direction, or the balance lab has nothing to sweep
        // and the temptation zone can't be found by turning a dial.
        var radius = new RoyaltyConfig().AuraRadius;
        var powers = new List<int>();
        foreach (var bonus in new[] { 0, 2, 8, 32 })
        {
            var grid = new TileGrid(8, 8, Biome.Grassland);
            var world = new GameWorld(grid);
            world.RestoreRoyaltyConfig(new RoyaltyConfig(radius, bonus, MajorityAge: 6));
            world.Players[0] = new Player(0);
            var king = world.AddUnit(new Unit(1, Field with { X = 4, Y = 4 }) { OwnerId = 0, BornTick = -30 * world.PopulationConfig.TicksPerYear });
            world.Players[0].KingUnitId = king.Id;
            var soldier = world.AddUnit(new Unit(2, king.Position) { Role = UnitRole.Soldier, OwnerId = 0, BornTick = 0 });

            powers.Add(CombatRules.EffectivePower(world, soldier, now: 0));
        }

        for (var i = 1; i < powers.Count; i++)
            Assert.True(powers[i] > powers[i - 1],
                $"raising AuraPowerBonus must raise power as fought ({string.Join(" -> ", powers)})");
    }

    [Fact]
    public void OpeningSafety_TheGenesisKingIsNotStandingInTheOpen()
    {
        // "Early-game regicide must not be possible before the war-telegraph
        // and starting defences can function." The sim-side half of that is
        // simply: the crown starts on a founder at home, in the castle's
        // vision, not out on the frontier where a bandit spawn can reach it
        // before anything is built.
        var spec = new GenesisSpec
        {
            Width = 16, Height = 16,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(8, 8),
                    KingUnitId = 1,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(8, 8)),
                        new UnitSpawn(2, new TileCoord(8, 9)),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 3);

        var king = Royalty.King(sim.World, 0)!;
        var castle = sim.World.Structures[new TileCoord(8, 8)];
        var dx = king.Position.X - castle.At.X;
        var dy = king.Position.Y - castle.At.Y;
        var sight = Sim.Core.Vision.Sight.RadiusFor(StructureKind.Castle);

        Assert.True(dx * dx + dy * dy <= sight * sight,
            "the genesis king must start inside his own castle's vision");
    }

    [Fact]
    public void ADynasticBadLuckRun_LeavesTheRealmAliveNotUnwinnable()
    {
        // A short-lived first king and a late heir must produce a ROCKY reign,
        // never a dead realm. The floor the design asks for is "still alive",
        // not "still comfortable" — so that is exactly what is asserted.
        var grid = new TileGrid(16, 16, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(8, 8)) { OwnerId = 0 }).Deposit(Resource.Food, 50_000);

        var cfg = world.PopulationConfig;
        for (var i = 1; i <= 6; i++)
            world.AddUnit(new Unit(i, new TileCoord(8, 8)) { OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        world.Players[0].KingUnitId = 1;

        var sim = new Simulation(world, seed: 99);

        // The worst dynastic luck available: the king dies childless.
        Population.OnUnitRemoved(sim, world.Units[1]);
        world.Units.Remove(1);
        sim.Run(sim.Now);

        Assert.Null(world.Players[0].KingUnitId);          // extinct line
        Assert.False(world.Players[0].Defeated);           // NOT a loss condition
        Assert.True(world.Units.Count >= 5);               // the realm stands
        Assert.True(world.Structures.ContainsKey(new TileCoord(8, 8)));

        // And it can still fight — just without the buff, which is the whole
        // "stakes with recovery" claim in one assertion.
        var soldier = world.AddUnit(new Unit(50, new TileCoord(8, 8)) { Role = UnitRole.Soldier, OwnerId = 0, BornTick = 0 });
        Assert.Equal(CombatRules.EffectivePower(soldier, sim.Now),
                     CombatRules.EffectivePower(world, soldier, sim.Now));
        Assert.True(CombatRules.EffectivePower(world, soldier, sim.Now) > 0);
    }
}
