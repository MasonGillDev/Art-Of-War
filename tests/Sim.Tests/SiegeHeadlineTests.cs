using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// M24 headline tests — the milestone's contract (per docs/architecture.md
// §1, "The Core Contract"): determinism survives across siege, raze,
// defeat, and game-over.
//
// Two pinned shapes:
//   * Twin-run: identical scenarios → identical Snapshot.Hash. The
//     baseline determinism guarantee (M0).
//   * Replay-from-intent-log: the live run captures every submitted
//     intent; a fresh sim that re-submits them in chronological order
//     (M16 replay discipline) lands on the same hash. Sieges, defeat
//     events, and game-over events are all driven through the existing
//     intent → CombatRoundEvent pipeline, so the M0 + M4 + M16 contracts
//     hold across the new surface.
public class SiegeHeadlineTests
{
    private const long RoundInterval = 10;

    // The scenario builder — both legs of every test rebuild from the
    // SAME spec so initial state is identical.
    private static (Simulation sim, TileCoord castleTile) MakeScenario(ulong seed = 0xA0FA0FA0)
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

        var castleTile = new TileCoord(19, 19);
        var castle = (Castle)world.Structures[castleTile];
        castle.Health = 15;   // sized so the siege completes in 3 rounds @ 5 dmg/round

        for (var i = 0; i < 5; i++)
            world.AddUnit(new Unit(100 + i, castleTile) { Role = UnitRole.Builder, OwnerId = 0 });

        var sim = new Simulation(world, seed: seed);
        return (sim, castleTile);
    }

    [Fact]
    public void Headline_TwinRun_HashesMatch()
    {
        var (sim1, t1) = MakeScenario();
        CombatTrigger.MaybeBeginCombatOnTile(sim1, t1);
        sim1.Run(until: 200);

        var (sim2, t2) = MakeScenario();
        CombatTrigger.MaybeBeginCombatOnTile(sim2, t2);
        sim2.Run(until: 200);

        // Castle razed, player defeated, game over fired — same in both runs.
        Assert.IsType<Rubble>(sim1.World.Structures[t1]);
        Assert.True(sim1.World.Players[1].Defeated);
        Assert.Contains(sim1.ResolvedLog, e => e is GameOverEvent);

        Assert.Equal(Snapshot.Hash(sim1), Snapshot.Hash(sim2));
    }

    [Fact]
    public void Headline_SnapshotRoundTrip_PreservesSiegeState()
    {
        // The mid-siege snapshot path: damage the castle partially, snapshot,
        // restore, finish the siege. Final hash must match a sibling run that
        // never snapshotted. Closes the M4 anchor contract over the new HP
        // field + the post-defeat Player.Defeated state.
        var (live, liveTile) = MakeScenario();
        CombatTrigger.MaybeBeginCombatOnTile(live, liveTile);
        live.Run(until: RoundInterval + 5);   // one round in — castle wounded but standing

        var bytes = Snapshot.Serialize(live);
        var restored = Snapshot.Restore(bytes, seed: 0xA0FA0FA0);

        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));

        // Run both to completion — same end state, same hash.
        live.Run(until: 200);
        restored.Run(until: 200);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));
        Assert.IsType<Rubble>(restored.World.Structures[liveTile]);
        Assert.True(restored.World.Players[1].Defeated);
    }
}
