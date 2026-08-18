using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server;

namespace Sim.Tests;

// Grave markers (Sim.Server.GraveTracker): a combat/starvation death that
// left loot on its tile mints a marker; looting the pile retires it; age
// deaths never mint one. The tracker is host presentation state driven off
// the unit-map diff + resolved-log exclusion, so these tests exercise it
// exactly as GameHost does — Harvest after Run, cursor left alone.
public class GraveTrackerTests
{
    private const long RoundInterval = 10;

    // Two factions pre-set as Enemy, `aUnits` of owner 0 vs one owner-1
    // victim hand-placed on `tile`; the victim carries `victimCargo` wood
    // (0 = empty-handed). Mirrors CombatResolutionTests' scenario builder.
    private static Simulation MakeContestedScenario(TileCoord tile, int aUnits, int victimCargo, ulong seed = 0xC0F)
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

        var nextId = 100;
        for (var i = 0; i < aUnits; i++)
            world.AddUnit(new Unit(nextId++, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        world.AddUnit(new Unit(nextId, tile)
        {
            Role = UnitRole.Builder,
            OwnerId = 1,
            CargoResource = victimCargo > 0 ? Resource.Wood : Resource.None,
            CargoAmount = victimCargo,
        });

        return new Simulation(world, seed: seed);
    }

    [Fact]
    public void CombatDeathWithCargo_MintsGrave_AndLootingRetiresIt()
    {
        // 2 v 1: the lone owner-1 victim dies carrying 4 wood (within a
        // Builder's DefaultCapacity, so one survivor load drains the pile).
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnits: 2, victimCargo: 4);
        var tracker = new GraveTracker();
        tracker.SnapshotUnits(sim.World);

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 200);
        Assert.Equal(0, sim.World.Units.Values.Count(u => u.OwnerId == 1));   // victim fell

        tracker.Harvest(sim, 0);
        var grave = Assert.Single(tracker.Graves);
        Assert.Equal(tile.X, grave.X);
        Assert.Equal(tile.Y, grave.Y);

        // A survivor standing on the tile loots the drop → the pile empties →
        // the next harvest retires the marker.
        var survivor = sim.World.Units.Values.First(u => u.OwnerId == 0);
        sim.SubmitIntent(201, new LoadCargoIntent(survivor.Id, Resource.Wood) { PlayerId = 0 });
        sim.Run(until: 210);
        Assert.False(sim.World.GroundResources.ContainsKey(tile));   // pile drained

        tracker.Harvest(sim, 0);
        Assert.Empty(tracker.Graves);
    }

    [Fact]
    public void EmptyHandedCombatDeaths_MintNoGraves()
    {
        // 1 v 1, nobody carrying anything: mutual attrition kills both, no
        // loot lands, so no markers — the grave is the pile's headstone.
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile, aUnits: 1, victimCargo: 0);
        var tracker = new GraveTracker();
        tracker.SnapshotUnits(sim.World);

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 200);
        Assert.Empty(sim.World.Units);

        tracker.Harvest(sim, 0);
        Assert.Empty(tracker.Graves);
    }

    [Fact]
    public void AgeDeath_MintsNoGrave_EvenWhenLootDrops()
    {
        // A cargo-laden unit dies OF AGE: the pipeline still drops its wood
        // on the tile, but the resolved-log exclusion (an applied
        // DeathByAgeEvent names its victim) keeps the marker from minting —
        // graves mark combat and starvation only.
        var tile = new TileCoord(5, 5);
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
            },
        };
        var world = Genesis.Build(spec);
        var unit = new Unit(100, tile)
        {
            Role = UnitRole.Builder,
            OwnerId = 0,
            CargoResource = Resource.Wood,
            CargoAmount = 4,
        };
        world.AddUnit(unit);
        var sim = new Simulation(world, seed: 0xC0F);

        var tracker = new GraveTracker();
        tracker.SnapshotUnits(sim.World);

        // Anchor + schedule exactly as lifespan rolling does, so the event's
        // (At, Seq) fence matches and it applies.
        unit.DeathTick = 50;
        unit.DeathSeq = sim.Schedule(50, new DeathByAgeEvent(100));
        sim.Run(until: 60);
        Assert.False(sim.World.Units.ContainsKey(100));                  // died of age
        Assert.True(sim.World.GroundResources.ContainsKey(tile));       // loot dropped

        tracker.Harvest(sim, 0);
        Assert.Empty(tracker.Graves);
    }
}
