using Sim.Core.Bandits;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M16 Phase 1 — the bandit faction plumbing (docs/m16-bandits-spec.md):
// a reserved OwnerId registered at genesis, hard-wired hostile to all,
// immune to diplomacy, with no remembered map. Bandit UNITS are ordinary
// units; everything bandit-shaped hangs off the id.
public class BanditFactionTests
{
    private static Simulation MakeSim(int playerUnits = 1)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < playerUnits; i++)
            spawns.Add(new UnitSpawn(i + 1, new TileCoord(1, 1), UnitRole.Builder, OwnerId: 0));
        var spec = new GenesisSpec
        {
            Width = 16, Height = 16,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(1, 1),
                    UnitSpawns = spawns,
                },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(14, 14) },
            },
        };
        return new Simulation(spec, seed: 0xBAD1);
    }

    [Fact]
    public void Genesis_RegistersBanditFaction_EmptyRow()
    {
        var sim = MakeSim();
        Assert.True(sim.World.Players.ContainsKey(BanditConstants.OwnerId));
        Assert.Equal(0, sim.World.Players[BanditConstants.OwnerId].PopulationCount);
        // No castle, no structures, no units for the bandit owner.
        Assert.DoesNotContain(sim.World.Structures.Values,
            s => s.OwnerId == BanditConstants.OwnerId);
        Assert.DoesNotContain(sim.World.Units.Values,
            u => u.OwnerId == BanditConstants.OwnerId);
    }

    [Fact]
    public void Genesis_RejectsReservedOwnerId()
    {
        var spec = new GenesisSpec
        {
            Width = 8, Height = 8,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = BanditConstants.OwnerId,
                    CastlePosition = new TileCoord(1, 1),
                },
            },
        };
        Assert.Throws<InvalidOperationException>(() => new Simulation(spec, seed: 1));
    }

    [Fact]
    public void AreHostile_BanditVsEveryone_Always_NoRowsNeeded()
    {
        var sim = MakeSim();
        var d = sim.World.Diplomacy;
        // Hostile to every registered faction, both directions, with zero
        // relationship rows.
        Assert.True(d.AreHostile(BanditConstants.OwnerId, 0));
        Assert.True(d.AreHostile(0, BanditConstants.OwnerId));
        Assert.True(d.AreHostile(BanditConstants.OwnerId, 1));
        Assert.False(d.AreHostile(BanditConstants.OwnerId, BanditConstants.OwnerId));
        Assert.Empty(d.Relationships);
        // Players 0 and 1 are unaffected: default Neutral.
        Assert.False(d.AreHostile(0, 1));
    }

    [Fact]
    public void Diplomacy_NamingBandits_Rejects_BothDirections()
    {
        var sim = MakeSim();

        var declareOn = new DeclareWarIntent(0, BanditConstants.OwnerId);
        Assert.True(declareOn.Resolve(sim).IsRejected);
        var declareAs = new DeclareWarIntent(BanditConstants.OwnerId, 0);
        Assert.True(declareAs.Resolve(sim).IsRejected);

        var proposeTo = new ProposeRelationshipIntent(0, BanditConstants.OwnerId, RelationshipState.Ally);
        Assert.True(proposeTo.Resolve(sim).IsRejected);
        var proposeAs = new ProposeRelationshipIntent(BanditConstants.OwnerId, 0, RelationshipState.Neutral);
        Assert.True(proposeAs.Resolve(sim).IsRejected);

        // Nothing leaked into diplomacy state.
        Assert.Empty(sim.World.Diplomacy.Relationships);
        Assert.Empty(sim.World.Diplomacy.Proposals);
    }

    [Fact]
    public void BanditUnit_FightsPlayerUnit_OnCoLocation_ViaExistingCombat()
    {
        // A bandit-owned unit is an ordinary unit: walking onto a player's
        // tile triggers the existing M7 hostile-co-location combat with NO
        // war declaration — AreHostile's bandit case is the whole gate.
        var sim = MakeSim(playerUnits: 1);
        var bandit = sim.World.AddUnit(new Unit(50, new TileCoord(3, 1))
        {
            Role = UnitRole.Soldier, OwnerId = BanditConstants.OwnerId, BornTick = 0,
        });

        sim.SubmitIntent(sim.Now, new MoveIntent(50, new TileCoord(1, 1))
            { PlayerId = BanditConstants.OwnerId });
        sim.Run(until: sim.Now + 10_000);

        // Soldier (30 HP / 3 pwr) vs Builder (10 HP / 1 pwr): the builder
        // dies; exact survivor stats are CombatConfig-derived, so pin only
        // the existential outcome.
        Assert.DoesNotContain(sim.World.Units.Values,
            u => u.OwnerId == 0 && u.Id == 1);
        Assert.True(sim.World.Units.ContainsKey(50));
    }

    [Fact]
    public void BanditMovement_WritesNoExploredMap()
    {
        var sim = MakeSim();
        sim.World.AddUnit(new Unit(50, new TileCoord(8, 8))
        {
            Role = UnitRole.Scout, OwnerId = BanditConstants.OwnerId, BornTick = 0,
        });

        sim.SubmitIntent(sim.Now, new MoveIntent(50, new TileCoord(12, 8))
            { PlayerId = BanditConstants.OwnerId });
        sim.Run(until: sim.Now + 10_000);

        Assert.Equal(new TileCoord(12, 8), sim.World.Units[50].Position);
        // Movement worked, but no remembered map accrued for the faction.
        Assert.False(sim.World.Explored.ContainsKey(BanditConstants.OwnerId));
        Assert.False(sim.World.RememberedBiome.ContainsKey(BanditConstants.OwnerId));
        // Live sight still works for bandits (the driver's read path).
        var visible = Sim.Core.Vision.View.VisibleTiles(sim.World, BanditConstants.OwnerId);
        Assert.Contains(new TileCoord(12, 8), visible);
    }

    [Fact]
    public void PlayerView_OmitsBanditFaction_ButShowsBanditUnits()
    {
        var sim = MakeSim();
        // Park a bandit inside player 0's castle vision.
        sim.World.AddUnit(new Unit(50, new TileCoord(2, 2))
        {
            Role = UnitRole.Soldier, OwnerId = BanditConstants.OwnerId, BornTick = 0,
        });

        var view = Sim.Core.Vision.View.BuildPlayerView(sim.World, playerId: 0, now: sim.Now);
        Assert.DoesNotContain(view.Factions, f => f.Id == BanditConstants.OwnerId);
        Assert.Contains(view.VisibleUnits, u => u.OwnerId == BanditConstants.OwnerId);
    }

    [Fact]
    public void Snapshot_RoundTrips_BanditRow_AndBanditUnits()
    {
        var sim = MakeSim();
        sim.World.AddUnit(new Unit(50, new TileCoord(8, 8))
        {
            Role = UnitRole.Soldier, OwnerId = BanditConstants.OwnerId, BornTick = 0,
            CargoResource = Resource.Wood, CargoAmount = 7,
        });

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xBAD1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.True(restored.World.Players.ContainsKey(BanditConstants.OwnerId));
        Assert.Equal(1, restored.World.Players[BanditConstants.OwnerId].PopulationCount);
        var unit = restored.World.Units[50];
        Assert.Equal(BanditConstants.OwnerId, unit.OwnerId);
        Assert.Equal(7, unit.CargoAmount);
    }
}
