using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Engagement pin (CombatTrigger.PinBelligerents + no-progress guard).
// See docs/combat-engagement-pin.md.
//
// The pin must hold even when a hop is cheaper than RoundIntervalTicks —
// the exact condition under which the pre-pin trigger let units march
// through. Tests pave row 5 with max-condition road (hop cost = RoadHop,
// derived from the biome + road constants); RoundInterval is set above it
// so round 1 lands well after the hop the attacker would have taken
// without the pin.
public class CombatEngagementPinTests
{
    // Max-road grassland hop cost, derived so movement retunes don't
    // touch this file. The RoundInterval premise (hop < round) is
    // asserted in the helper below.
    private static readonly int RoadHop = System.Math.Max(RoadConstants.MIN_COST,
        Biomes.MoveCost(Biome.Grassland)
        - (int)((long)Biomes.MoveCost(Biome.Grassland) * RoadConstants.MAX_REDUCTION_PERCENT / 100L));
    private static readonly long RoundInterval = RoadHop * 3;   // round 1 well after a hop
    private const int BaseHealth = 10;
    private const int RoadRow = 5;
    private const int GridSize = 12;

    private static (Simulation sim, GameWorld world) MakeWorld()
    {
        var grid = new TileGrid(GridSize, GridSize, Biome.Grassland);
        var world = new GameWorld(
            grid,
            new DiplomacyConfig(),
            new CombatConfig(RoundIntervalTicks: RoundInterval));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        var sim = new Simulation(world, seed: 0xC0F);
        return (sim, world);
    }

    private static void PaveRow5(GameWorld world)
    {
        // Every east-west arc along the row (docs/roads-on-edges.md).
        for (var x = 0; x < world.Grid.Width - 1; x++)
            for (var s = 0; s < Sim.Core.Battlefields.Subtile.Size; s++) world.Roads[SubtileLink.FromOwner(new Sim.Core.Battlefields.WorldSubtile(x * Sim.Core.Battlefields.Subtile.Size + s, RoadRow * Sim.Core.Battlefields.Subtile.Size + 1), SubtileLink.Axis.East)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
    }

    [Fact]
    public void Twin_PinScenario_Deterministic()
    {
        // Twin-run hash check over the road-pin scenario. Pin
        // touches per-unit anchors + epochs; the hash catches any
        // nondeterminism the determinism contract would otherwise miss.
        Simulation Run()
        {
            var (sim, world) = MakeWorld();
            PaveRow5(world);
            world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
            world.AddUnit(new Unit(100, new TileCoord(5, RoadRow)) { Role = UnitRole.Builder, OwnerId = 0 });
            world.AddUnit(new Unit(200, new TileCoord(1, RoadRow)) { Role = UnitRole.Builder, OwnerId = 1 });
            sim.SubmitIntent(sim.Now, new MoveIntent(200, new TileCoord(9, RoadRow)) { PlayerId = 1 });
            sim.Run(until: 500);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }
}
