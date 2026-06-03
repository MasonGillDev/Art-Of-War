using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M7 Phase C: multi-round, proportional, deterministic combat resolution.
// THE CRUX of the milestone — including the mid-flight snapshot test
// that closes the M4 regen pattern over combat.
public class CombatResolutionTests
{
    private const long RoundInterval = 10;
    private const int BaseHealth = 10;

    // Builds two factions, pre-sets them as Enemy (skipping the M6 Delay
    // for test brevity), and hand-places `aUnitsOnTile` units of owner 0
    // and `bUnitsOnTile` units of owner 1 on `tile`.
    private static Simulation MakeContestedScenario(TileCoord tile, int aUnitsOnTile, int bUnitsOnTile, ulong seed = 0xC0F)
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Diplomacy = new DiplomacyConfig(Delay: 50, ProposalExpiryTicks: 200),
            Combat = new CombatConfig(RoundIntervalTicks: RoundInterval),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        var world = Genesis.Build(spec);
        // Force enemy state directly — skip the Delay window for test setup.
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);

        var nextId = 100;
        for (var i = 0; i < aUnitsOnTile; i++)
            world.AddUnit(new Unit(nextId++, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        for (var i = 0; i < bUnitsOnTile; i++)
            world.AddUnit(new Unit(nextId++, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        return new Simulation(world, seed: seed);
    }

    // The trigger fires on arrival, not on co-placement at genesis. The
    // cleanest way to start combat from a pre-arranged tile in a test is
    // to invoke the trigger directly.
    private static void StartCombatHere(Simulation sim, TileCoord tile) =>
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);

    [Fact]
    public void EqualForces_BothTakeDamage_BothEventuallyDie()
    {
        // 1 vs 1; each power 1, each HP 10; each side takes 1 damage/round.
        // Both hit 0 HP on round 10 simultaneously.
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnitsOnTile: 1, bUnitsOnTile: 1);
        StartCombatHere(sim, tile);

        sim.Run(until: 200);
        // Both units dead; combat state cleared.
        Assert.Empty(sim.World.Units);
        Assert.Empty(sim.World.CombatStates);
    }

    [Fact]
    public void BiggerForceWinsPredictably()
    {
        // 2 vs 1. A power 2, B power 1.
        // Per round: A takes 1 damage, B takes 2.
        // B dies at round 5 (1 unit × 10 HP, 2 dmg/round → 0 at round 5).
        // A's lowest-HP unit has taken 5 damage by then → at 5 HP.
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnitsOnTile: 2, bUnitsOnTile: 1);
        StartCombatHere(sim, tile);

        sim.Run(until: 200);

        // A's two units both alive; B's unit dead.
        var aUnits = sim.World.Units.Values.Where(u => u.OwnerId == 0).ToList();
        var bUnits = sim.World.Units.Values.Where(u => u.OwnerId == 1).ToList();
        Assert.Equal(2, aUnits.Count);
        Assert.Empty(bUnits);
        // Combat resolved cleanly.
        Assert.Empty(sim.World.CombatStates);
        // The lowest-HP A unit took 5 damage; the other is untouched
        // (lowest-Health-first concentrates damage on one unit until it dies).
        Assert.Equal(new[] { 5, 10 }, aUnits.Select(u => u.Health).OrderBy(h => h));
    }

    [Fact]
    public void MultiRoundAttrition_EndsWhenOneSideRemains()
    {
        // 3 vs 1 — B should die quickly; combat then ends.
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnitsOnTile: 3, bUnitsOnTile: 1);
        StartCombatHere(sim, tile);

        sim.Run(until: 500);
        // Combat ended; A keeps all units; B has none.
        Assert.Empty(sim.World.CombatStates);
        Assert.Equal(3, sim.World.Units.Values.Count(u => u.OwnerId == 0));
        Assert.Equal(0, sim.World.Units.Values.Count(u => u.OwnerId == 1));
    }

    [Fact]
    public void SameTickContention_Deterministic()
    {
        // Twin-run: two enemy arrivals on the same tile at the same tick.
        // Identical hashes prove same-tick contention is deterministic.
        Simulation Run()
        {
            var tile = new TileCoord(10, 10);
            var sim = MakeContestedScenario(tile, aUnitsOnTile: 2, bUnitsOnTile: 2, seed: 0xC0F);
            StartCombatHere(sim, tile);
            sim.Run(until: 300);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    [Fact]
    public void Twin_FullBattle_Deterministic()
    {
        Simulation Run()
        {
            var tile = new TileCoord(10, 10);
            var sim = MakeContestedScenario(tile, aUnitsOnTile: 5, bUnitsOnTile: 3, seed: 0xBEEF);
            StartCombatHere(sim, tile);
            sim.Run(until: 1000);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    // ====== THE CRUX ======
    [Fact]
    public void MidFight_SnapshotRoundTrip_Identical()
    {
        // Setup: 1 vs 1 takes 10 rounds (each side takes 1/round).
        // Snapshot mid-fight at sim tick ~50 (round 5, both units at 5 HP).
        // Restore and run to completion. Hash must match uninterrupted run.
        var tile = new TileCoord(10, 10);

        Simulation BuildScenario(ulong seed)
        {
            var sim = MakeContestedScenario(tile, aUnitsOnTile: 1, bUnitsOnTile: 1, seed: seed);
            StartCombatHere(sim, tile);
            return sim;
        }

        const ulong Seed = 0xC0F;
        const long MidTick = 55;  // After ~5 rounds (rounds at tick 10, 20, ..., 50).
        const long EndTick = 200;

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
        Assert.True(restored.World.CombatStates.ContainsKey(tile),
            "restored sim should preserve the contested-tile anchor");
        restored.Run(until: EndTick);

        Assert.Equal(hashA, Snapshot.Hash(restored));
    }

    [Fact]
    public void Reinforcement_MidFight_ChangesOutcome()
    {
        // 1 (A) vs 1 (B). At round 3, hand-add a second A unit on the tile.
        // The re-gather picks them up → A's power doubles → A wins.
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnitsOnTile: 1, bUnitsOnTile: 1);
        StartCombatHere(sim, tile);

        // Run a few rounds (round 3 fires at tick 30).
        sim.Run(until: 35);

        // Reinforcement: a second A unit appears on the tile (modeling an
        // arrival that completed mid-fight — the trigger fences since
        // combat is already active, but the next round's gather sees the
        // new unit).
        sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 0 });

        sim.Run(until: 500);
        Assert.Empty(sim.World.CombatStates);
        // A should win — they had the bigger force after reinforcement.
        Assert.True(sim.World.Units.Values.Any(u => u.OwnerId == 0));
        Assert.Empty(sim.World.Units.Values.Where(u => u.OwnerId == 1));
    }

    [Fact]
    public void Retreat_MidFight_StopsParticipation()
    {
        // 1 (A) vs 1 (B). At round 3, move A off the tile. B's next-round
        // gather has no enemy → combat ends, B alive.
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnitsOnTile: 1, bUnitsOnTile: 1);
        StartCombatHere(sim, tile);

        sim.Run(until: 35);
        // A's unit was id 100 (first added).
        var aUnit = sim.World.Units.Values.First(u => u.OwnerId == 0);
        sim.SubmitIntent(sim.Now, new MoveIntent(aUnit.Id, new TileCoord(0, 0)));

        sim.Run(until: 500);
        // Combat ended; both units alive (A retreated, B held the tile).
        Assert.Empty(sim.World.CombatStates);
        Assert.True(sim.World.Units.ContainsKey(aUnit.Id));
        Assert.True(sim.World.Units.Values.Any(u => u.OwnerId == 1));
    }
}
