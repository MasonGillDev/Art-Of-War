using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// M24 Phase C — siege damage in combat rounds. Pins:
//   1. Combat-start broadens: a lone attacker on a tile holding a hostile
//      destructible structure (no defending units) triggers combat.
//   2. Round damages the structure when no defender of the structure's
//      owner was on the tile at ROUND START (defender's death this round
//      still shields).
//   3. Defenders shield: a unit owned by the structure's owner on the tile
//      means the structure takes ZERO damage that round even if they die.
//   4. Structure HP → 0 swaps it to a Rubble pile (with owner sentinel -3).
//   5. Indestructible kinds (Cache) are NEVER siege targets — the round
//      doesn't reschedule purely on their presence.
//
// Castle's catalog HP is 1000 (gameplay balance); to pin mechanics in
// finite test time the tests dial Castle.Health down by hand. The damage
// math is identical at 5 HP as at 1000 HP — only the round count shrinks.
public class SiegeDamageTests
{
    private const long RoundInterval = 10;

    private static Simulation MakeTwoFactionWorld()
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
        // Skip the Delay window — these tests start with the factions
        // already at war (same pattern as CombatResolutionTests).
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return new Simulation(world, seed: 0xC0F);
    }

    [Fact]
    public void LoneAttacker_OnHostileCastle_StartsCombat()
    {
        var sim = MakeTwoFactionWorld();
        var castleTile = new TileCoord(19, 19);   // owner 1's castle

        // One attacker from owner 0 standing on owner 1's castle, no defenders.
        sim.World.AddUnit(new Unit(100, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, castleTile);

        Assert.True(sim.World.CombatStates.ContainsKey(castleTile));
    }

    [Fact]
    public void UndefendedCastle_TakesDamageOverRounds_ThenRazedToRubble()
    {
        var sim = MakeTwoFactionWorld();
        var castleTile = new TileCoord(19, 19);
        var castle = (Castle)sim.World.Structures[castleTile];
        castle.Health = 5;  // dial down to finish in a handful of rounds

        // 5 Builder attackers, power 1 each → 5 siege damage per round.
        for (var i = 0; i < 5; i++)
            sim.World.AddUnit(new Unit(100 + i, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, castleTile);

        // Run far enough for round 1 to fire (at tick RoundInterval = 10).
        sim.Run(until: RoundInterval + 5);

        // Castle is gone — Rubble in its place, owned by the destroyed sentinel.
        var occupant = sim.World.Structures[castleTile];
        Assert.IsType<Rubble>(occupant);
        Assert.Equal(SiegeConstants.RubbleOwnerId, occupant.OwnerId);
        Assert.Equal(0, occupant.Health);
        // Combat ended (no hostile pair, no hostile siege — only attackers + rubble).
        Assert.False(sim.World.CombatStates.ContainsKey(castleTile));
    }

    [Fact]
    public void DefenderShields_StructureTakesNoDamage_WhileDefenderAlive()
    {
        var sim = MakeTwoFactionWorld();
        var castleTile = new TileCoord(19, 19);
        var castle = (Castle)sim.World.Structures[castleTile];
        castle.Health = 50;  // big margin so the test pins shielding, not destruction

        // 1 defender of owner 1, 1 attacker of owner 0. Equal power → both
        // take 1 damage per round; the defender dies on round 10.
        sim.World.AddUnit(new Unit(200, castleTile) { Role = UnitRole.Builder, OwnerId = 1 });
        sim.World.AddUnit(new Unit(100, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, castleTile);

        // After one round the defender is wounded but alive. Castle untouched.
        sim.Run(until: RoundInterval + 1);
        Assert.Equal(50, castle.Health);
        Assert.True(sim.World.Units.ContainsKey(200));
    }

    [Fact]
    public void DefenderDies_SiegeBegins_NextRound()
    {
        var sim = MakeTwoFactionWorld();
        var castleTile = new TileCoord(19, 19);
        var castle = (Castle)sim.World.Structures[castleTile];
        castle.Health = 50;

        // Defender on its last legs (HP 1) so it dies in round 1 cleanly —
        // attacker (HP 10) easily survives to siege from round 2 onward.
        var defender = sim.World.AddUnit(
            new Unit(200, castleTile) { Role = UnitRole.Builder, OwnerId = 1 });
        defender.Health = 1;
        sim.World.AddUnit(new Unit(100, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, castleTile);

        // Round 1 (tick 10): defender absorbs the round, dies. Castle still
        // untouched — the defender's death "cost a round" of shielding.
        sim.Run(until: RoundInterval + 1);
        Assert.False(sim.World.Units.ContainsKey(200));
        Assert.Equal(50, castle.Health);

        // Round 2 (tick 20): no defender at start → siege damage. Power 1
        // attacker → 1 HP off the castle.
        sim.Run(until: 2 * RoundInterval + 1);
        Assert.Equal(49, castle.Health);
    }

    [Fact]
    public void IndestructibleKind_OnTile_IsNotASiegeTarget()
    {
        var sim = MakeTwoFactionWorld();
        var tile = new TileCoord(5, 5);

        // Cache has BaseHealth = 0 (indestructible). The catalog still wants
        // Cache placed via the genesis scatter, but a manual add is OK for
        // this unit test — what we're pinning is the siege gate's behavior.
        sim.World.AddStructure(new Cache(tile) { OwnerId = Sim.Core.Caches.CacheConstants.OwnerId });

        sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);

        // No combat starts: a Cache has Health == 0 so SiegeableStructureOn
        // returns null, and there's no hostile unit pair (only owner 0).
        Assert.False(sim.World.CombatStates.ContainsKey(tile));
    }
}
