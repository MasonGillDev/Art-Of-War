using Sim.Core;
using Sim.Core.Bandits;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Bandits;
using Sim.Server.Sandbox;

namespace Sim.Tests.Battlefields;

// The battle sandbox (docs/battle-sandbox.md): a composition builds a small real
// world the way the game makes one, refuses what the game couldn't produce, and
// plays out the same way every time.
public class SandboxTests
{
    private const int Blue = Composition.Blue, Red = Composition.Red;

    private static CompositionUnit Soldier(int id, int faction, int dx, int dy, int doctrine = CompositionUnit.DefaultDoctrine) => new()
    {
        Id = id, Faction = faction, Role = (int)UnitRole.Soldier, Dx = dx, Dy = dy,
        Gear = [(int)Resource.BronzeSword, (int)Resource.Shield], Doctrine = doctrine,
    };

    // Blue's guard two tiles east of the castle; Red's party marches on it.
    private static Composition Skirmish() => new()
    {
        Units =
        [
            Soldier(1, Blue, 2, 0), Soldier(2, Blue, 2, 0),
            new CompositionUnit { Id = 3, Faction = Blue, Role = (int)UnitRole.Archer, Dx = 2, Dy = 0, Gear = [(int)Resource.Bow] },
            Soldier(4, Red, 6, 0, (int)DoctrineBehaviour.Advance), Soldier(5, Red, 6, 0, (int)DoctrineBehaviour.Advance),
        ],
        Marches =
        [
            new CompositionMarch { Unit = 4, Dx = 2, Dy = 0 },
            new CompositionMarch { Unit = 5, Dx = 2, Dy = 0 },
        ],
    };

    private static (Simulation Sim, List<PlacedPiece> Placed) Build(Composition c)
    {
        Assert.Null(c.Problem());
        var placed = new List<PlacedPiece>();
        var (build, setup) = SandboxWorld.Prepare(c, placed);
        var sim = new Simulation(build.Spec, SandboxWorld.Seed(c));
        setup(sim);
        return (sim, placed);
    }

    private static Unit Composed(Simulation sim, List<PlacedPiece> placed, int composedId) =>
        sim.World.Units[placed.Single(p => p.Id == composedId).SimIds.Single()];

    [Fact]
    public void AComposedSkirmish_OpensABattle_AndTheSidesMeetOnIt()
    {
        var (sim, _) = Build(Skirmish());
        var met = false;
        for (long t = 10; t <= 12 * Time.Hour && !met; t += 10)
        {
            sim.Run(until: t);
            met = sim.World.Units.Values
                .Where(u => u.Board is not null)
                .GroupBy(u => u.Board!.Tile)
                .Any(g => g.Select(u => u.OwnerId).Distinct().Count() > 1);
        }
        Assert.True(met, "the two sides never stood on a board together");
    }

    [Fact]
    public void TheSameComposition_PlaysOutTheSameWay()
    {
        static string Run()
        {
            var (sim, _) = Build(Skirmish());
            sim.Run(until: 16 * Time.Hour);
            return string.Join(";", sim.World.Units.Values.OrderBy(u => u.Id)
                .Select(u => $"{u.Id}@{u.Position.X},{u.Position.Y}/{u.Subtile}:{u.Health}"));
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void EveryComposedUnit_IsAtFullHealth_ItsRolesBasePlusItsGear()
    {
        var (sim, placed) = Build(Skirmish());
        var soldier = Composed(sim, placed, 1);
        Assert.Equal(BattlefieldHealth(soldier), soldier.Health);
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth
                     + Sim.Core.Equipment.EquipmentCatalog.Spec(Resource.Shield).HealthModifier, soldier.Health);
        var archer = Composed(sim, placed, 3);
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Archer).BaseHealth, archer.Health);
        Assert.Equal(2, soldier.Buffs.Count);
    }

    private static int BattlefieldHealth(Unit u) =>
        UnitCombatCatalog.Spec(u.Role).BaseHealth + u.Buffs.Sum(b => b.HealthModifier);

    [Fact]
    public void ImpossibleLoadouts_AreRefused_WithTheGamesReason()
    {
        var twoSwords = new Composition { Units = [new CompositionUnit { Id = 1, Role = (int)UnitRole.Soldier, Gear = [(int)Resource.BronzeSword, (int)Resource.BronzeSword] }] };
        Assert.Contains("already carries", twoSwords.Problem());   // M51: one sword per soldier
        var bowOnSoldier = new Composition { Units = [new CompositionUnit { Id = 1, Role = (int)UnitRole.Soldier, Gear = [(int)Resource.Bow] }] };
        Assert.Contains("cannot equip", bowOnSoldier.Problem());
    }

    [Fact]
    public void BlueCantBeScripted_ItTakesOrdersOnlyInPlay()
    {
        var c = Skirmish();
        c.Marches = [new CompositionMarch { Unit = 1, Dx = 5, Dy = 0 }];
        Assert.Contains("Blue", c.Problem());
    }

    [Fact]
    public void AUnitWithNoDoctrineSet_StandsOnItsRolesDefault()
    {
        var c = new Composition { Units = [Soldier(1, Red, 6, 0)] };
        var (sim, placed) = Build(c);
        var u = Composed(sim, placed, 1);
        Assert.Null(u.Doctrine);
        Assert.Equal(DoctrineBehaviour.Hold, BattleDoctrine.DefaultFor(u.Role).Behaviour);
    }

    [Fact]
    public void MoreThanATileHolds_SpillToTheNearestTileWithRoom_AndSaySo()
    {
        var c = new Composition
        {
            Units = Enumerable.Range(1, 20).Select(i => Soldier(i, Blue, 3, 3)).ToArray(),
        };
        var (sim, placed) = Build(c);
        var tiles = placed.Select(p => sim.World.Units[p.SimIds[0]].Position).ToList();
        foreach (var tile in tiles.Distinct())
            Assert.True(TileCapacity.SideCount(sim.World, tile, Blue) <= TileCapacity.For(sim.World, tile, Blue));
        Assert.Contains(placed, p => p.Moved);
        Assert.True(tiles.Distinct().Count() > 1);
    }

    [Fact]
    public void TheHour_IsWhereTickZeroFallsInTheLight()
    {
        var c = new Composition { Hour = 22 };
        var phase = Sim.Server.Atmosphere.WorldClock.Phase(0, SandboxWorld.Light(c));
        Assert.Equal(22 / 24.0, phase, 3);
        Assert.Equal(new Sim.Server.ServerOptions().LightCycleTicks, SandboxWorld.Light(c).TicksPerCycle);
    }

    [Fact]
    public void AComposedKing_IsTheRealmsCrown()
    {
        var c = new Composition { Units = [new CompositionUnit { Id = 7, Faction = Red, Role = (int)UnitRole.King, Dx = 5, Dy = 5 }] };
        var (sim, placed) = Build(c);
        Assert.Equal(Composed(sim, placed, 7).Id, Sim.Core.Royalty.Royalty.King(sim.World, Red)!.Id);
        var twoKings = new Composition
        {
            Units =
            [
                new CompositionUnit { Id = 1, Faction = Red, Role = (int)UnitRole.King },
                new CompositionUnit { Id = 2, Faction = Red, Role = (int)UnitRole.King },
            ],
        };
        Assert.Contains("already has a king", twoKings.Problem());
    }

    [Fact]
    public void APlacedBanditParty_IsTheDriversToRun_AndNoOtherPartySpawns()
    {
        var c = new Composition
        {
            Units = [Soldier(1, Blue, 2, 0)],
            Parties = [new CompositionParty { Id = 2, Dx = 5, Dy = 0, Size = 4 }],
        };
        var (sim, placed) = Build(c);
        var party = placed.Single(p => p.Id == 2).SimIds;
        Assert.Equal(4, party.Length);
        var driver = new BanditDriver(new BanditConfig { MaxLiveParties = 0, SpawnGraceTicks = 0 });
        var moved = false;
        var start = party.Select(id => sim.World.Units[id].Position).ToList();
        for (long t = Time.Hour; t <= 3 * Time.Day; t += Time.Hour)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            Assert.True(sim.World.Units.Values.Count(u => u.OwnerId == BanditConstants.OwnerId) <= 4);
            moved |= party.Any(id => sim.World.Units.TryGetValue(id, out var u) && (u.IsWalking || !start.Contains(u.Position)));
        }
        Assert.True(moved, "the bandit driver never moved the placed party");
    }

    [Fact]
    public void TheHost_RefusesABadComposition_AndKeepsItsWorld()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aow-sandbox-test-" + Guid.NewGuid().ToString("N"));
        using var host = new SandboxHost(dir);
        Assert.Null(host.Set(Skirmish()));
        var epoch = host.WorldEpoch;
        var bad = Skirmish();
        bad.Marches = [new CompositionMarch { Unit = 1, Dx = 1, Dy = 0 }];
        Assert.NotNull(host.Set(bad));
        Assert.Equal(epoch, host.WorldEpoch);
        Assert.Equal(5, host.Composition.Units.Length);
        Assert.True(host.Current.IsPaused);

        host.Save("first try");
        Assert.Contains("first try", host.Saved());
        host.New();
        Assert.Empty(host.Composition.Units);
        Assert.Null(host.Open("first try"));
        Assert.Equal(5, host.Composition.Units.Length);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void AnHourChange_IsAGenesisChange_AUnitChangeIsNot()
    {
        using var host = new SandboxHost(Path.Combine(Path.GetTempPath(), "aow-sandbox-test-" + Guid.NewGuid().ToString("N")));
        var genesis = host.GenesisEpoch;
        Assert.Null(host.Set(Skirmish()));
        Assert.Equal(genesis, host.GenesisEpoch);
        var later = Skirmish();
        later.Hour = 20;
        Assert.Null(host.Set(later));
        Assert.Equal(genesis + 1, host.GenesisEpoch);
    }
}
