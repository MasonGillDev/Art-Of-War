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
    public void StandingOrderIntents_RoundTrip()
    {
        // M18 — the order definition (steps with condition/action atoms)
        // must survive the durable JSON round-trip exactly: a replayed
        // SetStandingOrderIntent rebuilds the identical order.
        var set = new Sim.Core.Automation.SetStandingOrderIntent(
            Sim.Core.Automation.OrderKind.SupplyLine,
            Sim.Core.Automation.LoopMode.Loop,
            claimedUnits: new[] { 3, 5 },
            steps: new List<Sim.Core.Automation.OrderStep>
            {
                new()
                {
                    Conditions = new List<Sim.Core.Automation.ConditionSpec>
                    {
                        Sim.Core.Automation.ConditionSpec.StoreBelow(new TileCoord(4, 4), Resource.Wood, 20),
                        Sim.Core.Automation.ConditionSpec.CargoEmpty(3),
                        Sim.Core.Automation.ConditionSpec.ElapsedTicks(250),
                    },
                    Action = Sim.Core.Automation.ActionSpec.HaulTrip(
                        3, new TileCoord(7, 1), new TileCoord(4, 4), Resource.Wood),
                },
                new()
                {
                    Action = Sim.Core.Automation.ActionSpec.Train(5, UnitRole.Archer),
                },
            })
        { PlayerId = 2 };

        var (tn, payload) = IntentJson.Serialize(set);
        Assert.Equal("SetStandingOrderIntent", tn);
        var replay = Assert.IsType<Sim.Core.Automation.SetStandingOrderIntent>(
            IntentJson.Deserialize(tn, payload));
        Assert.Equal(set.Kind, replay.Kind);
        Assert.Equal(set.Loop, replay.Loop);
        Assert.Equal(set.ClaimedUnits, replay.ClaimedUnits);
        Assert.Equal(set.PlayerId, replay.PlayerId);
        Assert.Equal(set.Steps.Count, replay.Steps.Count);
        for (var i = 0; i < set.Steps.Count; i++)
        {
            Assert.Equal(set.Steps[i].Conditions, replay.Steps[i].Conditions);
            Assert.Equal(set.Steps[i].Action, replay.Steps[i].Action);
        }

        var clear = new Sim.Core.Automation.ClearStandingOrderIntent(orderId: 7) { PlayerId = 2 };
        var (tn2, p2) = IntentJson.Serialize(clear);
        Assert.Equal("ClearStandingOrderIntent", tn2);
        var replayClear = Assert.IsType<Sim.Core.Automation.ClearStandingOrderIntent>(
            IntentJson.Deserialize(tn2, p2));
        Assert.Equal(7, replayClear.OrderId);
        Assert.Equal(2, replayClear.PlayerId);
    }
}
