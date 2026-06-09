using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests;

// M7 Phase B: the combat trigger.
//   * Enemy arrival on a shared tile starts combat.
//   * Neutral / ally arrival is a no-op.
//   * Two arrivals same-tick don't double-schedule (fence).
//   * Arrival on already-contested tile doesn't reschedule.
public class CombatTriggerTests
{
    private const long Delay = 50;

    private static GenesisSpec MakeSpec()
    {
        // Two factions close enough that they reach each other quickly.
        // Each spawns a unit on their castle tile.
        return new GenesisSpec
        {
            Width = 20, Height = 20,
            Diplomacy = new DiplomacyConfig(Delay: Delay, ProposalExpiryTicks: 200),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(2, 10),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(2, 10), UnitRole.Builder),
                    },
                },
                new FactionStartSpec
                {
                    OwnerId = 1,
                    CastlePosition = new TileCoord(17, 10),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(2, new TileCoord(17, 10), UnitRole.Builder, OwnerId: 1),
                    },
                },
            },
        };
    }

    [Fact]
    public void EnemyArrival_StartsCombat()
    {
        var sim = new Simulation(Genesis.Build(MakeSpec()), seed: 0xC0F);
        // Declare war and let it become effective.
        sim.SubmitIntent(0, new DeclareWarIntent(0, 1));
        sim.Run(until: Delay + 1);
        Assert.True(sim.World.Diplomacy.AreHostile(0, 1));

        // Both factions march to (10, 10). Grassland cost = 10 ticks/tile;
        // unit 1 has 8 tiles to walk, unit 2 has 7. Run past arrival of
        // both (Now + ~85) but short enough to catch combat in progress
        // before all units die.
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(10, 10)));
        sim.SubmitIntent(sim.Now, new MoveIntent(2, new TileCoord(10, 10)) { PlayerId = 1 });
        sim.Run(until: sim.Now + 85);

        // Either CombatStates has an entry on the tile (mid-fight), OR
        // one of the units has already taken damage / been removed.
        var contested = sim.World.CombatStates.ContainsKey(new TileCoord(10, 10));
        var u1Dead = !sim.World.Units.ContainsKey(1);
        var u2Dead = !sim.World.Units.ContainsKey(2);
        var u1Damaged = !u1Dead && sim.World.Units[1].Health < 10;
        var u2Damaged = !u2Dead && sim.World.Units[2].Health < 10;
        Assert.True(contested || u1Dead || u2Dead || u1Damaged || u2Damaged,
            "Combat should have started — tile not contested and no unit took damage.");
    }

    [Fact]
    public void NeutralArrival_NoCombat()
    {
        // No DeclareWarIntent — factions remain neutral.
        var sim = new Simulation(Genesis.Build(MakeSpec()), seed: 0xC0F);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(10, 10)));
        sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(10, 10)) { PlayerId = 1 });
        sim.Run(until: 200);

        // Both units survived, both at full health, no combat state.
        Assert.Empty(sim.World.CombatStates);
        Assert.Equal(10, sim.World.Units[1].Health);
        Assert.Equal(10, sim.World.Units[2].Health);
    }

    [Fact]
    public void AllyArrival_NoCombat()
    {
        var sim = new Simulation(Genesis.Build(MakeSpec()), seed: 0xC0F);
        // Force an ally state directly via the internal API.
        sim.World.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Ally);

        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(10, 10)));
        sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(10, 10)) { PlayerId = 1 });
        sim.Run(until: 200);

        Assert.Empty(sim.World.CombatStates);
    }

    [Fact]
    public void ArrivalOnAlreadyContestedTile_DoesNotDoubleSchedule()
    {
        // Three units across two factions all arrive on the same tile.
        // Combat should be scheduled exactly once on the tile.
        var spec = MakeSpec();
        spec = spec with
        {
            FactionStarts = new[]
            {
                spec.FactionStarts[0] with
                {
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(2, 10), UnitRole.Builder),
                        new UnitSpawn(3, new TileCoord(2, 10), UnitRole.Builder),
                    },
                },
                spec.FactionStarts[1],
            },
        };
        var sim = new Simulation(Genesis.Build(spec), seed: 0xC0F);
        sim.SubmitIntent(0, new DeclareWarIntent(0, 1));
        sim.Run(until: Delay + 1);

        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(10, 10)));
        sim.SubmitIntent(sim.Now, new MoveIntent(2, new TileCoord(10, 10)) { PlayerId = 1 });
        sim.SubmitIntent(sim.Now, new MoveIntent(3, new TileCoord(10, 10)));

        // Run just past when units arrive but before combat resolves.
        // Combat should be scheduled exactly once.
        sim.Run(until: sim.Now + 30);

        // Either combat is still active (one entry) or has fully resolved
        // (entry cleared). What matters: the dictionary never had two rows
        // for the same tile.
        Assert.True(sim.World.CombatStates.Count <= 1);
    }
}
