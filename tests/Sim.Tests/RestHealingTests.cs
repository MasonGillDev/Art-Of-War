using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Healing;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Rest healing (docs/unit-healing.md): a wounded unit standing on its OWN
// Castle, House or Barracks, off any battlefield, heals RestConstants.HealPerPeriod
// at the end of every completed period of uninterrupted rest, up to
// CombatRules.MaxHealth. Free. Every number derives from RestConstants and the
// catalog.
public class RestHealingTests
{
    private const int Blue = 0, Red = 1;
    private const long Period = RestConstants.PeriodTicks;
    private const int Heal = RestConstants.HealPerPeriod;
    private static readonly TileCoord Home = new(2, 2), Field = new(3, 2);

    private static Simulation MakeSim()
    {
        var world = new GameWorld(new TileGrid(6, 6, Biome.Grassland));
        world.Players[Blue] = new Player(Blue);
        world.Players[Red] = new Player(Red);
        return new Simulation(world, seed: 7);
    }

    private static Unit AddUnit(Simulation sim, int id, TileCoord at, int owner = Blue, UnitRole role = UnitRole.Soldier)
    {
        var u = new Unit(id, at) { Role = role, OwnerId = owner };
        sim.World.AddUnit(u);
        Placement.Seat(sim.World, u);
        return u;
    }

    // A soldier `wound` below full on its own shelter at Home, clock started.
    private static Unit WoundedAtHome(Simulation sim, StructureKind shelter = StructureKind.House, int wound = 10)
    {
        sim.World.AddStructure(Build(shelter, Home, Blue));
        var u = AddUnit(sim, 1, Home);
        u.Health -= wound;
        Rest.ArmIfDormant(sim, u);
        return u;
    }

    private static Structure Build(StructureKind kind, TileCoord at, int owner) => kind switch
    {
        StructureKind.Castle => new Castle(at) { OwnerId = owner },
        StructureKind.House => new House(at) { OwnerId = owner },
        StructureKind.Barracks => new Barracks(at) { OwnerId = owner },
        StructureKind.Stockpile => new Stockpile(at) { OwnerId = owner },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    // One tick at a time until `done`; the tick it became true.
    private static long Until(Simulation sim, Func<bool> done)
    {
        for (var t = sim.Now + 1; t < 10_000; t++)
        {
            AdvanceTo(sim, t);
            if (done()) return t;
        }
        throw new InvalidOperationException("condition never held");
    }

    [Theory]
    [InlineData(StructureKind.Castle)]
    [InlineData(StructureKind.House)]
    [InlineData(StructureKind.Barracks)]
    public void Wounded_OnOwnShelter_HealsAFlatAmount_EachCompletedPeriod(StructureKind shelter)
    {
        var sim = MakeSim();
        var u = WoundedAtHome(sim, shelter);
        var start = u.Health;

        AdvanceTo(sim, Period - 1);
        Assert.Equal(start, u.Health);              // nothing before the period completes
        AdvanceTo(sim, Period);
        Assert.Equal(start + Heal, u.Health);
        AdvanceTo(sim, 3 * Period);
        Assert.Equal(start + 3 * Heal, u.Health);
    }

    [Fact]
    public void Healing_StopsAtMaxHealth_AndGoesDormant()
    {
        var sim = MakeSim();
        var max = UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
        var u = WoundedAtHome(sim, wound: Heal);

        AdvanceTo(sim, Period);
        Assert.Equal(max, u.Health);
        Assert.Null(u.NextRestHealTick);             // full: nothing queued
        AdvanceTo(sim, 10 * Period);
        Assert.Equal(max, u.Health);
    }

    [Fact]
    public void MaxHealth_IncludesAShield()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Barracks(Home) { OwnerId = Blue });
        var u = AddUnit(sim, 1, Home);
        u.Buffs.Add(new Buff("shield", PowerModifier: 0, HealthModifier: 10, ExpiresAt: null));
        var max = CombatRules.MaxHealth(u, sim.Now);
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth + 10, max);

        u.Health = max - 2 * Heal;
        Rest.ArmIfDormant(sim, u);
        AdvanceTo(sim, 5 * Period);
        Assert.Equal(max, u.Health);
    }

    [Fact]
    public void AtFullHealth_NothingIsQueued()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(Home) { OwnerId = Blue });
        var u = AddUnit(sim, 1, Home);
        Rest.ArmIfDormant(sim, u);
        Assert.Null(u.NextRestHealTick);
    }

    [Fact]
    public void NoHealing_OffAShelter_OnANonShelter_OrOnSomeoneElsesShelter()
    {
        var sim = MakeSim();
        var w = sim.World;
        w.AddStructure(new Stockpile(Home) { OwnerId = Blue });
        w.AddStructure(new House(Field) { OwnerId = Red });

        var open = AddUnit(sim, 1, new TileCoord(0, 0));
        var stockpile = AddUnit(sim, 2, Home);
        var enemyHouse = AddUnit(sim, 3, Field);
        foreach (var u in new[] { open, stockpile, enemyHouse })
        {
            u.Health -= 10;
            Rest.ArmIfDormant(sim, u);
            Assert.Null(u.NextRestHealTick);
            Assert.False(Rest.IsResting(w, u, sim.Now));
        }

        AdvanceTo(sim, 5 * Period);
        Assert.All(new[] { open, stockpile, enemyHouse },
            u => Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth - 10, u.Health));
    }

    [Fact]
    public void LeavingBeforeThePeriodEnds_EarnsNothing_AndAReturnStartsAFreshPeriod()
    {
        var sim = MakeSim();
        var u = WoundedAtHome(sim);
        var start = u.Health;

        Assert.True(new MoveIntent(u.Id, Field) { PlayerId = Blue }.Resolve(sim).IsApplied);
        var left = Until(sim, () => u.Position == Field);
        Assert.True(left < Period, "fixture: the walk off should take less than a period");
        Assert.Null(u.NextRestHealTick);             // the rest was broken

        AdvanceTo(sim, Period + 10);
        Assert.Equal(start, u.Health);                // the period never completed
        Until(sim, () => !u.IsWalking);

        Assert.True(new MoveIntent(u.Id, Home) { PlayerId = Blue }.Resolve(sim).IsApplied);
        var back = Until(sim, () => u.Position == Home);
        Assert.Equal(back + Period, u.NextRestHealTick);
        AdvanceTo(sim, back + Period);
        Assert.Equal(start + Heal, u.Health);
    }

    [Fact]
    public void NoHealing_OnABattlefield_AndSurvivorsHealOnceItCloses()
    {
        var sim = MakeSim();
        var w = sim.World;
        w.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        var u = WoundedAtHome(sim);
        var start = u.Health;
        var raider = AddUnit(sim, 2, Home, owner: Red);

        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, Home);
        Assert.True(w.Battlefields.ContainsKey(Home), "fixture: the board did not open");
        Assert.Null(u.NextRestHealTick);
        Assert.False(Rest.IsResting(w, u, sim.Now));

        // The raider is gone before the first beat; that beat closes the board.
        CombatRules.OnUnitDeath(sim, raider);
        var closed = Until(sim, () => !w.Battlefields.ContainsKey(Home));
        Assert.Equal(start, u.Health);                // nothing healed while the board stood
        Assert.Equal(closed + Period, u.NextRestHealTick);

        AdvanceTo(sim, closed + Period);
        Assert.Equal(start + Heal, u.Health);
    }

    [Fact]
    public void AShelterFinishing_UnderItsWounded_StartsThemHealing()
    {
        var sim = MakeSim();
        var site = (ConstructionSite)sim.World.AddStructure(new ConstructionSite(Home, StructureKind.House) { OwnerId = Blue });
        var u = AddUnit(sim, 1, Home);
        u.Health -= 10;
        Rest.ArmIfDormant(sim, u);
        Assert.Null(u.NextRestHealTick);              // a site is no shelter

        Construction.Complete(sim, site);
        Assert.Equal(sim.Now + Period, u.NextRestHealTick);
    }

    [Fact]
    public void ADeadUnitsQueuedHeal_IsANoOp()
    {
        var sim = MakeSim();
        var u = WoundedAtHome(sim);
        CombatRules.OnUnitDeath(sim, u);
        AdvanceTo(sim, 2 * Period);
        Assert.False(sim.World.Units.ContainsKey(1));
    }

    [Fact]
    public void IsResting_IsAPureRead()
    {
        var sim = MakeSim();
        var u = WoundedAtHome(sim);
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            Rest.IsResting(sim.World, u, sim.Now);
            CombatRules.MaxHealth(u, sim.Now);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    [Fact]
    public void TwinRun_IsDeterministic()
    {
        string Run()
        {
            var sim = MakeSim();
            WoundedAtHome(sim, StructureKind.Barracks, wound: 7);
            AdvanceTo(sim, 12 * Period);
            return Snapshot.Hash(sim);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void MidRest_SnapshotRestore_HealsIdentically()
    {
        var sim = MakeSim();
        var u = WoundedAtHome(sim);
        var start = u.Health;
        AdvanceTo(sim, Period + Period / 2);         // one heal landed, the next is in flight
        Assert.NotNull(u.NextRestHealTick);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 7);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(u.NextRestHealTick, restored.World.Units[1].NextRestHealTick);
        Assert.Equal(u.NextRestHealSeq, restored.World.Units[1].NextRestHealSeq);

        AdvanceTo(sim, 6 * Period);
        AdvanceTo(restored, 6 * Period);
        Assert.Equal(start + 6 * Heal, u.Health);
        Assert.Equal(u.Health, restored.World.Units[1].Health);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
