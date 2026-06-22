using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// M24 Phase D — castle destruction → player defeat → game over. Pins:
//   1. When a Castle's siege HP hits zero it is razed (Phase C) AND a
//      PlayerDefeatedEvent fires at the same tick, marking the owner.
//   2. A defeated player's intents reject at the IntentEvent gate — no
//      per-intent code change required, so the whole intent surface is
//      muted for the loser.
//   3. With only one undefeated player left in the roster, a GameOverEvent
//      is emitted naming the winner.
//   4. Player.Defeated round-trips through Snapshot so the defeat survives
//      restore (the IntentEvent gate still mutes the loser).
public class PlayerDefeatTests
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
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return new Simulation(world, seed: 0xC0F);
    }

    [Fact]
    public void RazingCastle_MarksOwnerDefeated_AndFiresGameOver()
    {
        var sim = MakeTwoFactionWorld();
        var castleTile = new TileCoord(19, 19);
        var castle = (Castle)sim.World.Structures[castleTile];
        castle.Health = 5;   // finish in round 1

        // Five attackers, power 1 each → 5 siege damage round 1 → razed.
        for (var i = 0; i < 5; i++)
            sim.World.AddUnit(new Unit(100 + i, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });

        CombatTrigger.MaybeBeginCombatOnTile(sim, castleTile);
        sim.Run(until: RoundInterval + 5);

        Assert.IsType<Rubble>(sim.World.Structures[castleTile]);
        Assert.True(sim.World.Players[1].Defeated);
        // Winner (id 0) still alive.
        Assert.False(sim.World.Players[0].Defeated);

        // PlayerDefeatedEvent + GameOverEvent both landed in the resolved log.
        Assert.Contains(sim.ResolvedLog, e => e is PlayerDefeatedEvent pd && pd.OwnerId == 1);
        var gameOver = (GameOverEvent?)sim.ResolvedLog.FirstOrDefault(e => e is GameOverEvent);
        Assert.NotNull(gameOver);
        Assert.Equal(0, gameOver!.WinnerId);
    }

    [Fact]
    public void DefeatedPlayer_IntentsReject_NoMutation()
    {
        var sim = MakeTwoFactionWorld();
        // Force defeat directly — the razing path is pinned by the test
        // above; this test isolates the IntentEvent gate.
        sim.World.Players[1].Defeated = true;

        // Try to place a Stockpile as the defeated player. SubmitIntent's
        // first arg is the TICK; PlayerId is set on the intent.
        sim.SubmitIntent(sim.Now, new PlaceSiteIntent(new TileCoord(10, 10), StructureKind.Stockpile) { PlayerId = 1 });
        sim.Run();

        Assert.False(sim.World.Structures.ContainsKey(new TileCoord(10, 10)));
        var last = sim.ResolvedLog[^1];
        Assert.True(last.Outcome.IsRejected);
        Assert.Contains("defeated", last.Outcome.Reason);
    }

    [Fact]
    public void DefeatedFlag_RoundTripsThroughSnapshot()
    {
        var sim = MakeTwoFactionWorld();
        sim.World.Players[1].Defeated = true;

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xC0F);

        Assert.True(restored.World.Players[1].Defeated);
        Assert.False(restored.World.Players[0].Defeated);
        // Hash invariant holds despite the flag (because we DID serialize it).
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void NoGameOver_WhileTwoUndefeatedPlayersRemain()
    {
        // Razing one of three factions' castles still leaves two players
        // undefeated → no GameOverEvent. (Phase D's count-undefeated
        // logic, not the binary 2-faction case above.)
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            Diplomacy = new DiplomacyConfig(Delay: 50, ProposalExpiryTicks: 200),
            Combat = new CombatConfig(RoundIntervalTicks: RoundInterval),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(29, 29) },
                new FactionStartSpec { OwnerId = 2, CastlePosition = new TileCoord(15, 0) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.Diplomacy.SetState(FactionPair.Of(0, 2), RelationshipState.Enemy);
        var sim = new Simulation(world, seed: 0xC0F);

        var castleTile = new TileCoord(29, 29);   // player 1's castle
        var castle = (Castle)sim.World.Structures[castleTile];
        castle.Health = 5;
        for (var i = 0; i < 5; i++)
            sim.World.AddUnit(new Unit(100 + i, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });
        CombatTrigger.MaybeBeginCombatOnTile(sim, castleTile);
        sim.Run(until: RoundInterval + 5);

        Assert.True(sim.World.Players[1].Defeated);
        Assert.False(sim.World.Players[0].Defeated);
        Assert.False(sim.World.Players[2].Defeated);
        // Two undefeated remain — no GameOverEvent in the log.
        Assert.DoesNotContain(sim.ResolvedLog, e => e is GameOverEvent);
    }
}
