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
}
