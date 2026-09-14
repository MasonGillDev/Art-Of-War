using Sim.Core.Engine;
using Sim.Core.Royalty;
using Sim.Core.World;
using Sim.Server;

namespace Sim.Tests;

// M31 follow-up — King and Heir as real UnitRoles.
//
// Royalty.cs argued AGAINST storing this: "a stored IsRoyal flag would need
// exactly that sweep and would be a second source of truth about the same fact."
// Making them roles makes that second source real, so these tests exist to hold
// the two sources equal. THE INVARIANT TEST IS THE POINT — the rest are the
// specific promises.
public class RoyalRoleTests
{
    private static (Simulation sim, WorldBuild build) MakeWorld(int size = 64)
    {
        var opts = new ServerOptions { MapWidth = size, MapHeight = size, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        return (new Simulation(build.Spec, seed: 0xA117), build);
    }

    /// The stored roles must equal the derived line, always. If this ever fails,
    /// the game is telling the player one thing and fighting by another.
    private static void AssertRolesMatchTheLine(Simulation sim)
    {
        var world = sim.World;
        foreach (var ownerId in world.Players.Keys)
        {
            var king = Royalty.King(world, ownerId);
            var heir = Royalty.HeirApparent(world, ownerId);

            if (king is not null)
                Assert.Equal(UnitRole.King, king.Role);
            if (heir is not null)
                Assert.Equal(UnitRole.Heir, heir.Role);

            // And nobody ELSE wears either role.
            foreach (var u in world.Units.Values.Where(u => u.OwnerId == ownerId))
            {
                if (u.Role == UnitRole.King)
                    Assert.Equal(king?.Id, u.Id);
                if (u.Role == UnitRole.Heir)
                    Assert.Equal(heir?.Id, u.Id);
            }
        }
    }

    [Fact]
    public void EveryFactionIsCrownedAtGenesis_AndTheKingWearsTheRole()
    {
        var (sim, _) = MakeWorld();
        AssertRolesMatchTheLine(sim);

        // The PLAYER factions are crowned. Not every row in Players is a realm —
        // the bandit faction is an owner id with no dynasty and never has been.
        var player = Royalty.King(sim.World, 0);
        Assert.NotNull(player);
        Assert.Equal(UnitRole.King, player!.Role);

        foreach (var ownerId in sim.World.Players.Keys.Where(id => id >= 0))
        {
            var king = Royalty.King(sim.World, ownerId);
            if (king is null) continue;   // a faction whose start was skipped
            Assert.Equal(UnitRole.King, king.Role);
        }
    }

    // WHICH worker the crown costs.
    //
    // As a role the king cannot build and cannot be retrained, so crowning a
    // founder costs the realm that founder's trade. The second SCOUT is the slot
    // chosen: exploration is the one genesis job with no downstream dependency.
    // The alternative — an extra body carrying the title — kept all fourteen
    // workers and broke three AI balance baselines, because an extra mouth is an
    // extra mouth.
    [Fact]
    public void TheCrownCostsABuilderAndNotATrainableBody()
    {
        var (sim, _) = MakeWorld();
        var mine = sim.World.Units.Values.Where(u => u.OwnerId == 0).ToList();

        // The realm still fields exactly one king, and the roster is still 14.
        Assert.Equal(1, mine.Count(u => u.Role == UnitRole.King));
        Assert.Equal(14, mine.Count);

        // The crown comes out of the BUILDERS, and that is the cheap slot rather
        // than the obvious one: TrainRung's pool already excludes Builders, so a
        // crowned builder costs the AI no TRAINABLE body — where a crowned scout
        // would, and did, starve a faction outright.
        Assert.Equal(1, mine.Count(u => u.Role == UnitRole.Builder));

        // Every trainable pair is intact.
        Assert.Equal(2, mine.Count(u => u.Role == UnitRole.Farmer));
        Assert.Equal(2, mine.Count(u => u.Role == UnitRole.Hauler));
        Assert.Equal(2, mine.Count(u => u.Role == UnitRole.Lumberjack));
        Assert.Equal(2, mine.Count(u => u.Role == UnitRole.Quarryman));
        Assert.Equal(2, mine.Count(u => u.Role == UnitRole.Miner));
        Assert.Equal(2, mine.Count(u => u.Role == UnitRole.Scout));
    }

    // Royalty is for life, in BOTH directions.
    [Fact]
    public void RoyaltyCannotBeTrainedIntoOrOutOf()
    {
        var (sim, _) = MakeWorld();
        var king = Royalty.King(sim.World, 0)!;
        var castle = sim.World.Structures.Values.First(
            s => s.OwnerId == 0 && s.Kind == StructureKind.Castle);

        // OUT OF: the king may not be laundered into a trade.
        var outOf = Sim.Core.Population.TrainingRules.Blocker(sim, king, UnitRole.Farmer);
        Assert.NotNull(outOf);
        Assert.Contains("royalty is not a trade", outOf!);

        // INTO: no citizen is trained into a crown. RoleTrainerCatalog maps both
        // royal roles to no trainer, which is what makes this free.
        var citizen = sim.World.Units.Values.First(
            u => u.OwnerId == 0 && u.Role == UnitRole.Farmer);
        Assert.Null(Sim.Core.Population.RoleTrainerCatalog.TrainerFor(UnitRole.King));
        Assert.Null(Sim.Core.Population.RoleTrainerCatalog.TrainerFor(UnitRole.Heir));

        var into = Sim.Core.Population.TrainingRules.Blocker(sim, citizen, UnitRole.King);
        Assert.NotNull(into);
        Assert.Contains("not trainable", into!);

        _ = castle;
    }

    // The crown's body is an ordinary one. Giving the monarch better stats would
    // stack a second, invisible advantage on top of the aura the design tunes.
    [Fact]
    public void TheCrownIsNotACombatUpgrade()
    {
        var citizen = Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.Farmer);
        var king = Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.King);
        var heir = Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.Heir);

        Assert.Equal(citizen.BaseHealth, king.BaseHealth);
        Assert.Equal(citizen.BasePower, king.BasePower);
        Assert.Equal(citizen.BaseHealth, heir.BaseHealth);
        Assert.Equal(citizen.BasePower, heir.BasePower);
    }

    // Every role must have a combat spec — the catalogue THROWS on a missing one,
    // so a new role that skipped it would crash the first time it fought.
    [Fact]
    public void EveryRoleHasACombatSpec()
    {
        foreach (var role in Enum.GetValues<UnitRole>())
        {
            if (role == UnitRole.None) continue;
            var spec = Sim.Core.Combat.UnitCombatCatalog.Spec(role);
            Assert.Equal(role, spec.Role);
        }
    }

    // Succession moves the crown AND the role, and the new king's eldest living
    // child takes up the heir's role in the same instant.
    [Fact]
    public void SuccessionMovesTheRoleWithTheCrown()
    {
        var (sim, _) = MakeWorld();
        var world = sim.World;
        var king = Royalty.King(world, 0)!;

        // Give the king two children so there is a line to pass to.
        var elder = MakeChild(world, king, bornTick: 10, id: 9001);
        var younger = MakeChild(world, king, bornTick: 20, id: 9002);
        Succession.SyncRoles(world, 0);

        Assert.Equal(UnitRole.Heir, elder.Role);
        Assert.Equal(UnitRole.None, younger.Role);
        AssertRolesMatchTheLine(sim);

        // The king falls.
        world.Units.Remove(king.Id);
        Succession.OnRoyalRemoved(sim, king);

        Assert.Equal(elder.Id, world.Players[0].KingUnitId);
        Assert.Equal(UnitRole.King, elder.Role);
        AssertRolesMatchTheLine(sim);
    }

    // The heir dying promotes the next in line rather than leaving a stale role.
    [Fact]
    public void AnHeirsDeathPromotesTheNextChild()
    {
        var (sim, _) = MakeWorld();
        var world = sim.World;
        var king = Royalty.King(world, 0)!;

        var elder = MakeChild(world, king, bornTick: 10, id: 9001);
        var younger = MakeChild(world, king, bornTick: 20, id: 9002);
        Succession.SyncRoles(world, 0);
        Assert.Equal(UnitRole.Heir, elder.Role);

        world.Units.Remove(elder.Id);
        Succession.OnRoyalRemoved(sim, elder);
        Succession.SyncRoles(world, 0);

        Assert.Equal(UnitRole.Heir, younger.Role);
        AssertRolesMatchTheLine(sim);
    }

    private static Unit MakeChild(GameWorld world, Unit king, long bornTick, int id)
    {
        var child = new Unit(id, king.Position)
        {
            Role = UnitRole.None,
            OwnerId = king.OwnerId,
            BornTick = bornTick,
            ParentAId = king.Id,
            ParentBId = 0,
        };
        world.AddUnit(child);
        return child;
    }
}
