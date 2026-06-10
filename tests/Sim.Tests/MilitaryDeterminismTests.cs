using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.Vision;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Military milestone closure gates: the M7 determinism contract
// re-proven in a buffed world. Twin runs hash-equal; a mid-fight
// snapshot of EQUIPPED forces recovers to the identical outcome; views
// never perturb state.
public class MilitaryDeterminismTests
{
    private const long RoundInterval = 10;

    private static Simulation MakeWarSim(ulong seed)
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Diplomacy = new DiplomacyConfig(Delay: 50, ProposalExpiryTicks: 200),
            Combat = new CombatConfig(RoundIntervalTicks: RoundInterval),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        // Adult citizen standing on the (to-be-added) Barracks.
                        new UnitSpawn(1, new TileCoord(2, 2), UnitRole.None,
                            OwnerId: 0, StartingAgeYears: 30),
                    },
                },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return new Simulation(world, seed: seed);
    }

    [Fact]
    public void Twin_CraftTrainEquipBattle_IdenticalHash()
    {
        // The whole new pipeline as submitted intents: craft a sword at
        // the Barracks, train the citizen to Soldier, equip, march onto
        // the enemy — the arrival trigger starts combat, the sworded
        // Soldier wins. Two identical runs must hash identically.
        Simulation Run()
        {
            var sim = MakeWarSim(seed: 0xD0D);
            var barracks = (Barracks)sim.World.AddStructure(
                new Barracks(new TileCoord(2, 2)) { OwnerId = 0 });
            foreach (var (r, n) in EquipmentCatalog.Spec(Resource.Sword).CraftCost)
                barracks.Deposit(r, n);
            // Enemy victim a few tiles away.
            sim.World.AddUnit(new Unit(50, new TileCoord(5, 2)) { Role = UnitRole.Builder, OwnerId = 1 });

            sim.SubmitIntent(0, new CraftEquipmentIntent(barracks.At, Resource.Sword));
            sim.SubmitIntent(0, new TrainUnitIntent(1, UnitRole.Soldier));
            sim.SubmitIntent(0, new EquipUnitIntent(1, Resource.Sword));
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(5, 2)));
            sim.Run(until: 5000);
            return sim;
        }

        var a = Run();
        var b = Run();

        // Sanity: the pipeline actually happened — soldier alive and
        // equipped, enemy dead, combat resolved.
        Assert.True(a.World.Units.ContainsKey(1));
        Assert.Equal(UnitRole.Soldier, a.World.Units[1].Role);
        Assert.Single(a.World.Units[1].Buffs);
        Assert.False(a.World.Units.ContainsKey(50));
        Assert.Empty(a.World.CombatStates);

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // ====== THE MILESTONE HEADLINE ======
    [Fact]
    public void MidFight_EquippedForces_SnapshotRoundTrip_Identical()
    {
        // Model: CombatResolutionTests.MidFight_SnapshotRoundTrip_Identical,
        // with both forces carrying full loadouts. Buffs ride the unit
        // snapshot and the combat anchor regenerates the round event, so
        // recovery must land on the identical battle outcome.
        var tile = new TileCoord(10, 10);

        Simulation BuildScenario(ulong seed)
        {
            var sim = MakeWarSim(seed);
            var sword = EquipmentCatalog.Spec(Resource.Sword);
            var shield = EquipmentCatalog.Spec(Resource.Shield);
            var nextId = 100;
            for (var owner = 0; owner <= 1; owner++)
            {
                for (var i = 0; i < 2; i++)
                {
                    var u = sim.World.AddUnit(new Unit(nextId++, tile)
                        { Role = UnitRole.Soldier, OwnerId = owner });
                    u.Buffs.Add(new Buff(sword.BuffKind, sword.PowerModifier, sword.HealthModifier, null));
                    u.Buffs.Add(new Buff(shield.BuffKind, shield.PowerModifier, shield.HealthModifier, null));
                    u.Health += sword.HealthModifier + shield.HealthModifier;
                }
            }
            CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
            return sim;
        }

        const ulong Seed = 0xFAB;
        const long MidTick = 35;  // mid-battle, rounds at 10, 20, 30, ...
        const long EndTick = 2000;

        // Path A: uninterrupted.
        var simA = BuildScenario(Seed);
        simA.Run(until: EndTick);
        var hashA = Snapshot.Hash(simA);

        // Path B: snapshot mid-fight, restore, continue.
        var simB = BuildScenario(Seed);
        simB.Run(until: MidTick);
        Assert.True(simB.World.CombatStates.ContainsKey(tile),
            "expected combat still active at MidTick");
        var bytes = Snapshot.Serialize(simB);
        var restored = Snapshot.Restore(bytes, seed: Seed);
        restored.Run(until: EndTick);

        Assert.Equal(hashA, Snapshot.Hash(restored));
    }

    [Fact]
    public void Views_DoNotAffectMilitaryState()
    {
        // Building player views mid-battle 100× must not perturb the sim
        // (the pure-read wall over the new Power/Buffs projections).
        var tile = new TileCoord(10, 10);
        var sim = MakeWarSim(seed: 0xE1);
        var sword = EquipmentCatalog.Spec(Resource.Sword);
        for (var owner = 0; owner <= 1; owner++)
        {
            var u = sim.World.AddUnit(new Unit(100 + owner, tile)
                { Role = UnitRole.Soldier, OwnerId = owner });
            u.Buffs.Add(new Buff(sword.BuffKind, sword.PowerModifier, sword.HealthModifier, null));
        }
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 25); // mid-battle

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            View.BuildPlayerView(sim.World, 0, sim.Now);
            View.BuildPlayerView(sim.World, 1, sim.Now);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
