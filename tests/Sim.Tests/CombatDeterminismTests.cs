using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M7 Phase F: views don't write combat state (the FogDeterminism pattern
// extended to combat).
public class CombatDeterminismTests
{
    private static Simulation MakeBattle(ulong seed)
    {
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            Combat = new CombatConfig(RoundIntervalTicks: 10),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(27, 27) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);

        var tile = new TileCoord(15, 15);
        for (var i = 0; i < 3; i++)
            world.AddUnit(new Unit(100 + i, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        for (var i = 0; i < 2; i++)
            world.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        var sim = new Simulation(world, seed: seed);
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        return sim;
    }

    [Fact]
    public void Views_DoNotAffectCombatState()
    {
        // Spam BuildPlayerView between every sim.Run; the combat hash
        // must be identical to a run that never called the view.
        var simNoView = MakeBattle(0xFEED);
        simNoView.Run(until: 500);
        var hashNoView = Snapshot.Hash(simNoView);

        var simWithView = MakeBattle(0xFEED);
        for (long t = 10; t <= 500; t += 10)
        {
            simWithView.Run(until: t);
            // Build all factions' views — these reads MUST be pure.
            _ = View.BuildPlayerView(simWithView.World, 0);
            _ = View.BuildPlayerView(simWithView.World, 1);
        }
        var hashWithView = Snapshot.Hash(simWithView);

        Assert.Equal(hashNoView, hashWithView);
    }
}
