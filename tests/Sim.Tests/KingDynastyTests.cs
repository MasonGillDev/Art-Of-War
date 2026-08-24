using Sim.Core;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.Royalty;
using Sim.Core.World;

namespace Sim.Tests;

// M31 — KING & DYNASTY (docs/king-and-dynasty.md).
//
// The design's load-bearing claim is that the dynasty is a crown on machinery
// that already exists: a stored king id, a DERIVED heir, succession fired from
// the death events every path already converges on, and an aura that is a pure
// read over two positions. These tests pin that — especially the derivations,
// because a derived fact that drifts is worse than a stored one.
public class KingDynastyTests
{
    private static readonly TileCoord Keep = new(10, 10);

    private static readonly RoyaltyConfig Royal = new(AuraRadius: 3, AuraPowerBonus: 5, MajorityAge: 6);

    // A realm with a crowned king (unit 1) and a spare adult (unit 2).
    private static Simulation BuildRealm(int kingAge = 30, RoyaltyConfig? royalty = null)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.RestoreRoyaltyConfig(royalty ?? Royal);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 }).Deposit(Resource.Food, 100_000);

        var cfg = world.PopulationConfig;
        world.AddUnit(new Unit(1, Keep) { OwnerId = 0, BornTick = -(long)kingAge * cfg.TicksPerYear });
        world.AddUnit(new Unit(2, Keep) { OwnerId = 0, BornTick = -30 * cfg.TicksPerYear });
        world.Players[0].KingUnitId = 1;
        world.NextUnitId = 3;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 31);
    }

    // Add a child of the king, born `ageYears` ago.
    private static Unit AddRoyalChild(Simulation sim, int id, int ageYears, int otherParent = 2)
    {
        var cfg = sim.World.PopulationConfig;
        var child = sim.World.AddUnit(new Unit(id, Keep)
        {
            OwnerId = 0,
            BornTick = sim.Now - (long)ageYears * cfg.TicksPerYear,
            ParentAId = 1,
            ParentBId = otherParent,
        });
        if (sim.World.NextUnitId <= id) sim.World.NextUnitId = id + 1;
        return child;
    }

    // ---- Phase A: the line -------------------------------------------------

    [Fact]
    public void TheHeirIsTheEldestLivingChild_AndTheDerivationIsOrderIndependent()
    {
        // Insert the children in two different orders; the heir must not care.
        static int HeirOf(int[] insertionOrder)
        {
            var sim = BuildRealm();
            var ages = new Dictionary<int, int> { [10] = 5, [11] = 12, [12] = 9 };
            foreach (var id in insertionOrder) AddRoyalChild(sim, id, ages[id]);
            return Royalty.HeirApparent(sim.World, 0)!.Id;
        }

        Assert.Equal(11, HeirOf(new[] { 10, 11, 12 }));   // 11 is the eldest (12 years)
        Assert.Equal(11, HeirOf(new[] { 12, 11, 10 }));
        Assert.Equal(11, HeirOf(new[] { 11, 10, 12 }));
    }

    [Fact]
    public void TheHeirSkipsTheDead_WithNoInterveningCall()
    {
        var sim = BuildRealm();
        AddRoyalChild(sim, 10, ageYears: 12);
        AddRoyalChild(sim, 11, ageYears: 9);
        Assert.Equal(10, Royalty.HeirApparent(sim.World, 0)!.Id);

        Population.OnUnitRemoved(sim, sim.World.Units[10]);
        sim.World.Units.Remove(10);

        // Nothing was told about the heir. It is derived, so it just moved.
        Assert.Equal(11, Royalty.HeirApparent(sim.World, 0)!.Id);
    }

    [Fact]
    public void RoyaltyIsDerived_SoTheNarrowLineHoldsAcrossThreeGenerations()
    {
        var sim = BuildRealm();
        var g2a = AddRoyalChild(sim, 10, ageYears: 20);   // heir
        var g2b = AddRoyalChild(sim, 11, ageYears: 18);   // spare
        Assert.True(Royalty.IsRoyal(sim.World, g2a));
        Assert.True(Royalty.IsRoyal(sim.World, g2b));

        // Generation 3: children of the heir, NOT royal while grandpa reigns.
        var cfg = sim.World.PopulationConfig;
        var g3 = sim.World.AddUnit(new Unit(20, Keep)
        {
            OwnerId = 0, BornTick = sim.Now - 2 * cfg.TicksPerYear,
            ParentAId = 10, ParentBId = 11,
        });
        Assert.False(Royalty.IsRoyal(sim.World, g3));

        // The king dies; the heir is crowned.
        Population.OnUnitRemoved(sim, sim.World.Units[1]);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);
        Assert.Equal(10, sim.World.Players[0].KingUnitId);

        // The narrow line, for free: the new king's child is royal, and the
        // former king's OTHER child has lapsed to commoner — no sweep ran.
        Assert.True(Royalty.IsRoyal(sim.World, g3));
        Assert.False(Royalty.IsRoyal(sim.World, g2b));
    }

    // ---- Phase B: succession ----------------------------------------------

    [Fact]
    public void TheCrownPassesToTheHeir_AndSaysSo()
    {
        var sim = BuildRealm();
        AddRoyalChild(sim, 10, ageYears: 20);

        Population.OnUnitRemoved(sim, sim.World.Units[1]);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);

        Assert.Equal(10, sim.World.Players[0].KingUnitId);
        Assert.Contains(sim.ResolvedLog, e =>
            e is SuccessionEvent se && se.FormerKingUnitId == 1 && se.NewKingUnitId == 10);
    }

    [Fact]
    public void EveryDeathPath_CrownsTheSameHeir()
    {
        // Old age, starvation and combat all converge on OnUnitRemoved, which
        // is the reason succession needed no plumbing of its own. If a fourth
        // path ever bypasses it, this is the test that should notice.
        static int? CrownAfter(Action<Simulation> killTheKing)
        {
            var sim = BuildRealm();
            AddRoyalChild(sim, 10, ageYears: 20);
            killTheKing(sim);
            sim.Run(sim.Now);
            return sim.World.Players[0].KingUnitId;
        }

        var byHand = CrownAfter(sim =>
        {
            Population.OnUnitRemoved(sim, sim.World.Units[1]);
            sim.World.Units.Remove(1);
        });
        var byCombat = CrownAfter(sim => CombatRules.OnUnitDeath(sim, sim.World.Units[1]));

        Assert.Equal(10, byHand);
        Assert.Equal(byCombat, byHand);
    }

    [Fact]
    public void AChildMonarchIsCrowned_ButProjectsNothing_AndHasNoHeir()
    {
        // The emergent double crisis from the design doc, which falls out of
        // the position-based definitions with no extra rule: a child king has
        // no children, so BOTH the king's buff and any heir are absent.
        var sim = BuildRealm();
        AddRoyalChild(sim, 10, ageYears: 2);        // under MajorityAge

        Population.OnUnitRemoved(sim, sim.World.Units[1]);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);

        Assert.Equal(10, sim.World.Players[0].KingUnitId);
        Assert.True(Royalty.IsMinor(sim.World, sim.World.Units[10], sim.Now));
        Assert.Null(Royalty.ProjectingKing(sim.World, 0, sim.Now));
        Assert.Null(Royalty.HeirApparent(sim.World, 0));
        Assert.Contains(sim.ResolvedLog, e => e is SuccessionEvent { IsMinority: true });
    }

    [Fact]
    public void TheChildKingGrowsUp_AndTheBuffReturnsOnItsOwn()
    {
        var sim = BuildRealm();
        AddRoyalChild(sim, 10, ageYears: 2);
        Population.OnUnitRemoved(sim, sim.World.Units[1]);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);
        Assert.Null(Royalty.ProjectingKing(sim.World, 0, sim.Now));

        // Majority is a pure AGE GATE: nothing is scheduled and nothing has to
        // fire, so the test asks the same question at a later `now` rather
        // than running a clock forward. (Run() only advances Now to event
        // ticks, and a realm with an empty queue never moves — asserting
        // through Run here would have tested the queue, not the rule.)
        // Config-derived, never a hard-coded tick.
        var cfg = sim.World.PopulationConfig;
        var majority = sim.World.RoyaltyConfig.MajorityAge;
        var grownUp = sim.Now + (long)(majority + 1) * cfg.TicksPerYear;

        Assert.NotNull(Royalty.ProjectingKing(sim.World, 0, grownUp));
    }

    [Fact]
    public void ExtinctionLeavesTheRealmStandingAndBuffless()
    {
        var sim = BuildRealm();
        Population.OnUnitRemoved(sim, sim.World.Units[1]);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);

        Assert.Null(sim.World.Players[0].KingUnitId);
        Assert.Null(Royalty.King(sim.World, 0));
        // The realm persists: its remaining citizen and its castle are fine.
        Assert.True(sim.World.Units.ContainsKey(2));
        Assert.True(sim.World.Structures.ContainsKey(Keep));
        Assert.Contains(sim.ResolvedLog, e => e is SuccessionEvent { NewKingUnitId: null });
    }

    [Fact]
    public void ADeadRoyalChildIsNotASuccession()
    {
        var sim = BuildRealm();
        AddRoyalChild(sim, 10, ageYears: 20);
        AddRoyalChild(sim, 11, ageYears: 15);

        Population.OnUnitRemoved(sim, sim.World.Units[10]);
        sim.World.Units.Remove(10);
        sim.Run(sim.Now);

        Assert.Equal(1, sim.World.Players[0].KingUnitId);
        Assert.DoesNotContain(sim.ResolvedLog, e => e is SuccessionEvent);
        Assert.Equal(11, Royalty.HeirApparent(sim.World, 0)!.Id);
    }

    // ---- Phase C: the aura -------------------------------------------------

    [Fact]
    public void TheAuraIsADisc_OnAndOffByDistance_DiagonalsodIncluded()
    {
        var sim = BuildRealm();
        var world = sim.World;
        var r = world.RoyaltyConfig.AuraRadius;
        var bonus = world.RoyaltyConfig.AuraPowerBonus;

        // On the axis at exactly r: inside. At r+1: outside.
        var inside = world.AddUnit(new Unit(20, new TileCoord(Keep.X + r, Keep.Y)) { OwnerId = 0, BornTick = 0 });
        var outside = world.AddUnit(new Unit(21, new TileCoord(Keep.X + r + 1, Keep.Y)) { OwnerId = 0, BornTick = 0 });
        // Diagonal at (r, r) is OUTSIDE a Euclidean disc even though a
        // Chebyshev square would include it — this is the assertion that keeps
        // "radius" meaning the same thing it means to Sight.Reveal.
        var diagonal = world.AddUnit(new Unit(22, new TileCoord(Keep.X + r, Keep.Y + r)) { OwnerId = 0, BornTick = 0 });

        Assert.Equal(bonus, CombatRules.KingAuraBonus(world, inside, sim.Now));
        Assert.Equal(0, CombatRules.KingAuraBonus(world, outside, sim.Now));
        Assert.Equal(0, CombatRules.KingAuraBonus(world, diagonal, sim.Now));
    }

    [Fact]
    public void TheAuraIsOwnerScoped()
    {
        var sim = BuildRealm();
        var world = sim.World;
        world.Players[1] = new Player(1);
        var enemy = world.AddUnit(new Unit(20, Keep) { OwnerId = 1, BornTick = 0 });

        Assert.Equal(0, CombatRules.KingAuraBonus(world, enemy, sim.Now));
    }

    [Fact]
    public void ThreeDifferentRulesProduceTheSameZero()
    {
        var cfg = new PopulationConfig();

        // (a) interregnum — no king at all.
        var a = BuildRealm();
        a.World.Players[0].KingUnitId = null;
        Assert.Equal(0, CombatRules.KingAuraBonus(a.World, a.World.Units[2], a.Now));

        // (b) minority — crowned, but a child.
        var b = BuildRealm(kingAge: 2);
        Assert.Equal(0, CombatRules.KingAuraBonus(b.World, b.World.Units[2], b.Now));

        // (c) embarked — off the tile grid and invisible to combat, so the
        // aura must not leak through the hull.
        var c = BuildRealm();
        c.World.Units[1].EmbarkedOn = 99;
        Assert.Equal(0, CombatRules.KingAuraBonus(c.World, c.World.Units[2], c.Now));
    }

    [Fact]
    public void TheRollupsThatDecideFightsIncludeTheAura()
    {
        var sim = BuildRealm();
        var world = sim.World;
        var bonus = world.RoyaltyConfig.AuraPowerBonus;

        var soldier = world.AddUnit(new Unit(20, Keep) { Role = UnitRole.Soldier, OwnerId = 0, BornTick = 0 });
        var own = CombatRules.EffectivePower(soldier, sim.Now);
        var fought = CombatRules.EffectivePower(world, soldier, sim.Now);

        Assert.Equal(own + bonus, fought);
        // ForcePower is what CombatRoundEvent and the siege read.
        Assert.Equal(
            CombatRules.EffectivePower(world, world.Units[1], sim.Now) + fought,
            CombatRules.ForcePower(world, 0, Keep, sim.Now) - CombatRules.EffectivePower(world, world.Units[2], sim.Now));
    }

    [Fact]
    public void TheAuraDiesWithTheKing_AndReturnsWithHisSuccessor()
    {
        var sim = BuildRealm();
        var world = sim.World;
        var soldier = world.AddUnit(new Unit(20, Keep) { Role = UnitRole.Soldier, OwnerId = 0, BornTick = 0 });
        AddRoyalChild(sim, 10, ageYears: 20);
        var withKing = CombatRules.EffectivePower(world, soldier, sim.Now);

        Population.OnUnitRemoved(sim, world.Units[1]);
        world.Units.Remove(1);
        sim.Run(sim.Now);

        // Crowned instantly — no interregnum gap when an adult heir exists.
        Assert.Equal(10, world.Players[0].KingUnitId);
        Assert.Equal(withKing, CombatRules.EffectivePower(world, soldier, sim.Now));

        // Now kill the line: the realm dims.
        Population.OnUnitRemoved(sim, world.Units[10]);
        world.Units.Remove(10);
        sim.Run(sim.Now);
        Assert.Equal(withKing - world.RoyaltyConfig.AuraPowerBonus,
            CombatRules.EffectivePower(world, soldier, sim.Now));
    }

    // ---- persistence + determinism ----------------------------------------

    [Fact]
    public void TheDynastyRoundTripsThroughASnapshot()
    {
        var sim = BuildRealm();
        AddRoyalChild(sim, 10, ageYears: 20);
        AddRoyalChild(sim, 11, ageYears: 15);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 31);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        Assert.Equal(1, restored.World.Players[0].KingUnitId);
        Assert.Equal(1, restored.World.Units[10].ParentAId);
        Assert.Equal(10, Royalty.HeirApparent(restored.World, 0)!.Id);
        Assert.Equal(sim.World.RoyaltyConfig, restored.World.RoyaltyConfig);
    }

    [Fact]
    public void AnInterregnumRoundTrips()
    {
        var sim = BuildRealm();
        Population.OnUnitRemoved(sim, sim.World.Units[1]);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 31);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Null(restored.World.Players[0].KingUnitId);
    }

    [Fact]
    public void TwinRun_AcrossASuccession_IsIdentical()
    {
        static (int? crown, string hash) Run()
        {
            var sim = BuildRealm();
            AddRoyalChild(sim, 10, ageYears: 20);
            AddRoyalChild(sim, 11, ageYears: 15);
            Population.OnUnitRemoved(sim, sim.World.Units[1]);
            sim.World.Units.Remove(1);
            sim.Run(sim.Now + 5 * Time.Day);
            return (sim.World.Players[0].KingUnitId, Snapshot.Hash(sim));
        }

        Assert.Equal(Run(), Run());
    }

    // ---- genesis -----------------------------------------------------------

    [Fact]
    public void GenesisCrownsTheFounder()
    {
        var spec = new GenesisSpec
        {
            Width = 12, Height = 12,
            Royalty = Royal,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(2, 2),
                    KingUnitId = 1,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(2, 2)),
                        new UnitSpawn(2, new TileCoord(2, 2)),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 5);

        Assert.Equal(1, sim.World.Players[0].KingUnitId);
        Assert.True(Royalty.IsRoyal(sim.World, sim.World.Units[1]));
        Assert.False(Royalty.IsRoyal(sim.World, sim.World.Units[2]));
        Assert.Equal(Royal, sim.World.RoyaltyConfig);
    }

    [Fact]
    public void GenesisRejectsACrownThatNamesNobody()
    {
        // A kingless faction has to be an explicit choice, and a crown on a
        // unit that doesn't exist has to be a loud failure rather than a
        // silently buffless realm.
        var spec = new GenesisSpec
        {
            Width = 12, Height = 12,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(2, 2),
                    KingUnitId = 77,
                    UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(2, 2)) },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() => new Simulation(spec, seed: 5));
    }

    // ---- the line as a target ----------------------------------------------

    [Fact]
    public void BreedingProducesARoyalChild_SoTheLineGrowsThroughTheNormalSystem()
    {
        // The dynasty rides M8 breeding and M30's goal shaping — no royal
        // breeding path of its own. Fire one BeginBreedingIntent at the king
        // and a partner, and the child comes out royal.
        var sim = BuildRealm();
        var world = sim.World;
        var houseAt = new TileCoord(11, 10);
        var house = new House(houseAt) { OwnerId = 0 };
        world.AddStructure(house);
        house.Deposit(Resource.Food, world.PopulationConfig.BirthFoodCost);

        sim.SubmitIntent(0, new BeginBreedingIntent(houseAt, 1, 2) { PlayerId = 0 });
        sim.Run(world.PopulationConfig.GestationTicks * 2);

        var child = Assert.Single(world.Units.Values.Where(u => u.ParentAId == 1 || u.ParentBId == 1));
        Assert.True(Royalty.IsRoyal(world, child));
        Assert.Equal(child.Id, Royalty.HeirApparent(world, 0)!.Id);
    }
}
