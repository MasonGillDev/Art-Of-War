using Sim.Core.Equipment;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Persistence;

namespace Sim.Persistence.Tests;

// Durable-name + payload round-trips for the military-milestone intents.
// The type-name strings are frozen forever once shipped (IntentJson
// registry contract).
public class IntentJsonTests
{
    [Fact]
    public void CraftEquipmentIntent_RoundTrips()
    {
        var intent = new CraftEquipmentIntent(new TileCoord(3, 7), Resource.Sword) { PlayerId = 2 };

        var (typeName, payload) = IntentJson.Serialize(intent);
        Assert.Equal("CraftEquipmentIntent", typeName);

        var replay = Assert.IsType<CraftEquipmentIntent>(IntentJson.Deserialize(typeName, payload));
        Assert.Equal(new TileCoord(3, 7), replay.BarracksTile);
        Assert.Equal(Resource.Sword, replay.Item);
        Assert.Equal(2, replay.PlayerId);
    }

    [Fact]
    public void PlaceSiteIntent_WithClaimTiles_RoundTrips()
    {
        // M15: the optional claim list must survive the durable JSON
        // round-trip (content AND order), and omitted claims stay null
        // (the server-side auto-select signal).
        var claims = new List<TileCoord> { new(2, 1), new(1, 2), new(3, 2) };
        var intent = new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.LumberCamp,
            claimTiles: claims) { PlayerId = 1 };

        var (typeName, payload) = IntentJson.Serialize(intent);
        Assert.Equal("PlaceSiteIntent", typeName);

        var replay = Assert.IsType<PlaceSiteIntent>(IntentJson.Deserialize(typeName, payload));
        Assert.Equal(claims, replay.ClaimTiles);
        Assert.Equal(1, replay.PlayerId);

        var bare = new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.LumberCamp);
        var (tn2, p2) = IntentJson.Serialize(bare);
        var replay2 = Assert.IsType<PlaceSiteIntent>(IntentJson.Deserialize(tn2, p2));
        Assert.Null(replay2.ClaimTiles);
    }

    [Fact]
    public void EquipUnitIntent_RoundTrips()
    {
        var intent = new EquipUnitIntent(unitId: 42, Resource.Shield) { PlayerId = 1 };

        var (typeName, payload) = IntentJson.Serialize(intent);
        Assert.Equal("EquipUnitIntent", typeName);

        var replay = Assert.IsType<EquipUnitIntent>(IntentJson.Deserialize(typeName, payload));
        Assert.Equal(42, replay.UnitId);
        Assert.Equal(Resource.Shield, replay.Item);
        Assert.Equal(1, replay.PlayerId);
    }

    [Fact]
    public void BanditIntents_RoundTrip()
    {
        // M16 — server-internal but durable: recovery replays bandit
        // spawns/despawns from the log like any other intent.
        var spawn = new Sim.Core.Bandits.SpawnBanditPartyIntent(new TileCoord(40, 41), size: 4)
            { PlayerId = Sim.Core.Bandits.BanditConstants.OwnerId };
        var (tn, payload) = IntentJson.Serialize(spawn);
        Assert.Equal("SpawnBanditPartyIntent", tn);
        var replaySpawn = Assert.IsType<Sim.Core.Bandits.SpawnBanditPartyIntent>(
            IntentJson.Deserialize(tn, payload));
        Assert.Equal(new TileCoord(40, 41), replaySpawn.At);
        Assert.Equal(4, replaySpawn.Size);
        Assert.Equal(Sim.Core.Bandits.BanditConstants.OwnerId, replaySpawn.PlayerId);

        var despawn = new Sim.Core.Bandits.DespawnBanditPartyIntent(new[] { 7, 8, 9 })
            { PlayerId = Sim.Core.Bandits.BanditConstants.OwnerId };
        var (tn2, p2) = IntentJson.Serialize(despawn);
        Assert.Equal("DespawnBanditPartyIntent", tn2);
        var replayDespawn = Assert.IsType<Sim.Core.Bandits.DespawnBanditPartyIntent>(
            IntentJson.Deserialize(tn2, p2));
        Assert.Equal(new[] { 7, 8, 9 }, replayDespawn.UnitIds);
        Assert.Equal(Sim.Core.Bandits.BanditConstants.OwnerId, replayDespawn.PlayerId);
    }

    [Fact]
    public void EngageUnitIntent_RoundTrips()
    {
        // M29 — the whole chase unfolds from this one row on replay, so every
        // field has to survive: a lost leash would turn a bounded patrol
        // response into an unbounded one on recovery.
        var intent = new Sim.Core.Combat.EngageUnitIntent(
            unitId: 7, targetUnitId: 12, new TileCoord(20, 5), leashRadius: 9) { PlayerId = 3 };

        var (typeName, payload) = IntentJson.Serialize(intent);
        Assert.Equal("EngageUnitIntent", typeName);

        var replay = Assert.IsType<Sim.Core.Combat.EngageUnitIntent>(
            IntentJson.Deserialize(typeName, payload));
        Assert.Equal(7, replay.UnitId);
        Assert.Equal(12, replay.TargetUnitId);
        Assert.Equal(new TileCoord(20, 5), replay.LeashTile);
        Assert.Equal(9, replay.LeashRadius);
        Assert.Equal(3, replay.PlayerId);
    }

    [Fact]
    public void OrderIntents_RoundTrip()
    {
        // The whole order definition — trigger clauses, selector, crew,
        // routine circuit — must survive the durable JSON round-trip
        // exactly, or a replayed SetOrderIntent rebuilds a DIFFERENT
        // automation than the one the player installed. (The get-only
        // collection trap lives here: System.Text.Json cannot populate a
        // get-only list, so an order's crew and trigger would silently come
        // back EMPTY — a supply line that replays as a no-op.)
        var definition = new Sim.Core.Automation.Order
        {
            Priority = 3,
            SubjectKind = Sim.Core.Automation.SubjectKind.Structure,
            SubjectTile = new TileCoord(4, 4),
            Program = Sim.Core.Automation.ProgramKind.Maintain,
            Recipe = Sim.Core.Automation.RecipeKind.Haul,
            Target = 300,
            SourceTile = new TileCoord(7, 1),
            Resource = Resource.Wood,
            CrewMode = Sim.Core.Automation.CrewMode.Named,
            Selector = Sim.Core.Automation.Selector.InAgeWindow(
                new TileCoord(4, 4), radius: 9, minAgeYears: 18, maxAgeYears: 45),
            Trigger = Sim.Core.Automation.Trigger.When(
                Sim.Core.Automation.Predicate.StockBelow(new TileCoord(4, 4), Resource.Wood, 20),
                Sim.Core.Automation.Predicate.RoleCountBelow(UnitRole.Hauler, 6)),
            Steps =
            {
                new Sim.Core.Automation.RoutineStep
                {
                    Tile = new TileCoord(7, 1),
                    Action = Sim.Core.Automation.RoutineAction.Load,
                    Resource = Resource.Wood,
                    DepartWhen =
                    {
                        Sim.Core.Automation.Predicate.StockAtLeast(new TileCoord(7, 1), Resource.Wood, 25),
                    },
                },
            },
        };
        definition.NamedCrew.Add(3);
        definition.NamedCrew.Add(5);
        var set = new Sim.Core.Automation.SetOrderIntent(definition) { PlayerId = 2 };

        var (tn, payload) = IntentJson.Serialize(set);
        Assert.Equal("SetOrderIntent", tn);
        var replay = Assert.IsType<Sim.Core.Automation.SetOrderIntent>(
            IntentJson.Deserialize(tn, payload));
        var d = replay.Definition;

        Assert.Equal(2, replay.PlayerId);
        Assert.Equal(3, d.Priority);
        Assert.Equal(Sim.Core.Automation.RecipeKind.Haul, d.Recipe);
        Assert.Equal(new TileCoord(7, 1), d.SourceTile);
        Assert.Equal(Resource.Wood, d.Resource);
        Assert.Equal(300, d.Target);
        Assert.Equal(new List<int> { 3, 5 }, d.NamedCrew);
        Assert.Equal(45, d.Selector.MaxAgeYears);
        var clause = Assert.Single(d.Trigger.Any);
        Assert.Equal(2, clause.All.Count);
        Assert.Equal(Sim.Core.Automation.PredicateKind.StockBelow, clause.All[0].Kind);
        var step = Assert.Single(d.Steps);
        Assert.Equal(Sim.Core.Automation.RoutineAction.Load, step.Action);
        Assert.Single(step.DepartWhen);

        var clear = new Sim.Core.Automation.ClearOrderIntent(orderId: 7) { PlayerId = 2 };
        var (tn2, p2) = IntentJson.Serialize(clear);
        Assert.Equal("ClearOrderIntent", tn2);
        var replayClear = Assert.IsType<Sim.Core.Automation.ClearOrderIntent>(
            IntentJson.Deserialize(tn2, p2));
        Assert.Equal(7, replayClear.OrderId);
        Assert.Equal(2, replayClear.PlayerId);

        // M29 — patrol posture. A radius lost in the round-trip would replay
        // as a PACIFIST circuit: the patrol still walks, still looks right on
        // the dashboard, and simply never defends anything.
        var patrol = new Sim.Core.Automation.Order
        {
            SubjectKind = Sim.Core.Automation.SubjectKind.Structure,
            SubjectTile = new TileCoord(4, 4),
            Program = Sim.Core.Automation.ProgramKind.Routine,
            CrewMode = Sim.Core.Automation.CrewMode.Named,
            EngageRadius = 6,
            LeashRadius = 11,
            Steps = { new Sim.Core.Automation.RoutineStep { Tile = new TileCoord(9, 9) } },
        };
        patrol.NamedCrew.Add(4);
        var (tnP, pP) = IntentJson.Serialize(
            new Sim.Core.Automation.SetOrderIntent(patrol) { PlayerId = 1 });
        var replayPatrol = Assert.IsType<Sim.Core.Automation.SetOrderIntent>(
            IntentJson.Deserialize(tnP, pP));
        Assert.Equal(6, replayPatrol.Definition.EngageRadius);
        Assert.Equal(11, replayPatrol.Definition.LeashRadius);

        // Server-internal but durable: status + claim moves replay too.
        var status = new Sim.Core.Automation.OrderStatusIntent(
            7, Sim.Core.Automation.OrderStatusOp.AdvanceStep, expectedStep: 1) { PlayerId = 2 };
        var (tn3, p3) = IntentJson.Serialize(status);
        var replayStatus = Assert.IsType<Sim.Core.Automation.OrderStatusIntent>(
            IntentJson.Deserialize(tn3, p3));
        Assert.Equal(Sim.Core.Automation.OrderStatusOp.AdvanceStep, replayStatus.Op);
        Assert.Equal(1, replayStatus.ExpectedStep);
    }
}
