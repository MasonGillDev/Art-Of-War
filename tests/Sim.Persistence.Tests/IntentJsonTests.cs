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
    // ---- the client's own payload shapes ---------------------------------
    //
    // The Unity client hand-writes its JSON (JsonUtility cannot omit a field, so
    // an optional parameter gets its own payload class there). These two tests
    // are the contract between that hand-written shape and this deserializer:
    // they feed the EXACT bytes the client sends, so a rename on either side
    // fails here rather than in a running game as a silently ignored order.

    [Fact]
    public void TrainUnitIntent_FromTheClientsWireShape_CarriesTheTrainerTile()
    {
        // TrainUnitAtPayload, verbatim.
        const string payload =
            "{\"UnitId\":7,\"NewRole\":1,\"TrainerTile\":{\"X\":4,\"Y\":12},\"PlayerId\":0}";

        var replay = Assert.IsType<Sim.Core.Population.TrainUnitIntent>(
            IntentJson.Deserialize("TrainUnitIntent", payload));

        Assert.Equal(7, replay.UnitId);
        Assert.Equal(UnitRole.Builder, replay.NewRole);
        Assert.Equal(new TileCoord(4, 12), replay.TrainerTile);
    }

    [Fact]
    public void TrainUnitIntent_WithoutATrainerTile_KeepsTheStandingOnItContract()
    {
        // TrainUnitPayload, verbatim: the field is ABSENT, not null. That is how
        // the client says "no opinion", and it must land as null rather than as
        // an instruction to train at (0,0).
        const string payload = "{\"UnitId\":7,\"NewRole\":9,\"PlayerId\":0}";

        var replay = Assert.IsType<Sim.Core.Population.TrainUnitIntent>(
            IntentJson.Deserialize("TrainUnitIntent", payload));

        Assert.Equal(UnitRole.Soldier, replay.NewRole);
        Assert.Null(replay.TrainerTile);
    }

    [Fact]
    public void TheClientsErrandPayloads_DeserializeVerbatim()
    {
        // The Unity client hand-writes these, and JsonUtility cannot omit a
        // field — so each optional target gets its own payload class there, and
        // an always-present tile of (0,0) would read here as a real instruction
        // to walk to the map corner. These are the EXACT bytes it sends.

        var equip = Assert.IsType<EquipUnitIntent>(IntentJson.Deserialize("EquipUnitIntent",
            "{\"UnitId\":7,\"Item\":5,\"StoreTile\":{\"X\":14,\"Y\":4},\"PlayerId\":0}"));
        Assert.Equal(Resource.Sword, equip.Item);
        Assert.Equal(new TileCoord(14, 4), equip.StoreTile);

        var loot = Assert.IsType<Sim.Core.Caches.LootCacheIntent>(
            IntentJson.Deserialize("LootCacheIntent",
                "{\"UnitId\":3,\"Resource\":1,\"CacheTile\":{\"X\":4,\"Y\":14},\"PlayerId\":0}"));
        Assert.Equal(Resource.Wood, loot.Resource);
        Assert.Equal(new TileCoord(4, 14), loot.CacheTile);

        var embark = Assert.IsType<Sim.Core.Boats.EmbarkIntent>(
            IntentJson.Deserialize("EmbarkIntent",
                "{\"BoatId\":50,\"UnitIds\":[1,2],\"DockTile\":{\"X\":9,\"Y\":4},\"PlayerId\":0}"));
        Assert.Equal(new TileCoord(9, 4), embark.DockTile);
        Assert.Equal(new[] { 1, 2 }, embark.UnitIds);
    }

    [Fact]
    public void Footprints_ABuildOrderCarriesItsFacing_OrNone()
    {
        // The client's PlaceSite payload, with and without the Facing field
        // (docs/structure-footprints.md): absent = -1 (work it out).
        var turned = Assert.IsType<Sim.Core.Logistics.PlaceSiteIntent>(IntentJson.Deserialize("PlaceSiteIntent",
            "{\"Tile\":{\"X\":4,\"Y\":5},\"Kind\":9,\"Facing\":2,\"PlayerId\":0}"));
        Assert.Equal(2, turned.Facing);
        var plain = Assert.IsType<Sim.Core.Logistics.PlaceSiteIntent>(IntentJson.Deserialize("PlaceSiteIntent",
            "{\"Tile\":{\"X\":4,\"Y\":5},\"Kind\":9,\"PlayerId\":0}"));
        Assert.Equal(-1, plain.Facing);
        var (name, payload) = IntentJson.Serialize(turned);
        Assert.Equal(2, Assert.IsType<Sim.Core.Logistics.PlaceSiteIntent>(IntentJson.Deserialize(name, payload)).Facing);

        // The combined place-and-staff order the client actually sends.
        var build = Assert.IsType<Sim.Core.Logistics.BuildIntent>(IntentJson.Deserialize("BuildIntent",
            "{\"Tile\":{\"X\":4,\"Y\":5},\"Kind\":9,\"BuilderId\":0,\"WorkerToManId\":0,\"Facing\":3,\"PlayerId\":0}"));
        Assert.Equal(3, build.Facing);
    }

    [Fact]
    public void M41_TheClientsBattleOrderPayloads_Parse()
    {
        // The EXACT bytes Unity's JsonUtility writes for the prod client's battle
        // orders (Net/HttpServerLink IntentFactory.BattleOrder / BattleRoute /
        // BattleDoctrine).
        var move = Assert.IsType<Sim.Core.Battlefields.SetBattleOrderIntent>(IntentJson.Deserialize("SetBattleOrderIntent",
            "{\"UnitId\":5000,\"Kind\":2,\"Target\":{\"X\":1,\"Y\":2},\"Waypoints\":[],\"SwapWith\":0,\"PlayerId\":1}"));
        Assert.Equal(5000, move.UnitId);
        Assert.Equal(2, move.Kind);
        Assert.Equal(new Sim.Core.Battlefields.Subtile(1, 2), move.Target);
        Assert.Equal(1, move.PlayerId);

        var route = Assert.IsType<Sim.Core.Battlefields.SetBattleOrderIntent>(IntentJson.Deserialize("SetBattleOrderIntent",
            "{\"UnitId\":7,\"Kind\":3,\"Target\":{\"X\":0,\"Y\":0},\"Waypoints\":[{\"X\":0,\"Y\":1},{\"X\":1,\"Y\":1}],\"SwapWith\":0,\"PlayerId\":0}"));
        Assert.Equal(2, route.Waypoints.Count);
        Assert.Equal(new Sim.Core.Battlefields.Subtile(1, 1), route.Waypoints[1]);

        var doctrine = Assert.IsType<Sim.Core.Battlefields.SetBattleDoctrineIntent>(IntentJson.Deserialize("SetBattleDoctrineIntent",
            "{\"UnitId\":7,\"Behaviour\":1,\"WithdrawBelow\":2,\"PlayerId\":0}"));
        Assert.Equal(1, doctrine.Behaviour);
        Assert.Equal(2, doctrine.WithdrawBelow);

        // And the round trip the durable log relies on.
        var (name, payload) = IntentJson.Serialize(route);
        var back = Assert.IsType<Sim.Core.Battlefields.SetBattleOrderIntent>(IntentJson.Deserialize(name, payload));
        Assert.Equal(route.Waypoints, back.Waypoints);
    }

    [Fact]
    public void M42_ASubtileRoutePayload_ParsesAndRoundTrips()
    {
        // A drawn route names each step on the whole map (tile × 4 + subtile).
        var route = Assert.IsType<Sim.Core.Battlefields.SubtileRouteIntent>(IntentJson.Deserialize("SubtileRouteIntent",
            "{\"UnitId\":12,\"Route\":[{\"X\":23,\"Y\":21},{\"X\":24,\"Y\":21},{\"X\":24,\"Y\":22}],\"PlayerId\":1}"));
        Assert.Equal(12, route.UnitId);
        Assert.Equal(3, route.Route.Count);
        Assert.Equal(new Sim.Core.Battlefields.WorldSubtile(24, 22), route.Route[2]);
        Assert.Equal(1, route.PlayerId);

        var (name, payload) = IntentJson.Serialize(route);
        Assert.Equal("SubtileRouteIntent", name);
        var back = Assert.IsType<Sim.Core.Battlefields.SubtileRouteIntent>(IntentJson.Deserialize(name, payload));
        Assert.Equal(route.Route, back.Route);

        // An empty route (cancel) survives too.
        var cancel = Assert.IsType<Sim.Core.Battlefields.SubtileRouteIntent>(IntentJson.Deserialize("SubtileRouteIntent",
            "{\"UnitId\":12,\"Route\":[],\"PlayerId\":1}"));
        Assert.Empty(cancel.Route);
    }

    [Fact]
    public void M40_TheClientsSalvagePayload_Parses()
    {
        // The EXACT bytes the prod client sends for a salvage (SetHaulJob, kind 3,
        // no resource, target = the crew).
        var job = Assert.IsType<Sim.Core.Hauling.SetHaulJobIntent>(IntentJson.Deserialize("SetHaulJobIntent",
            "{\"Source\":{\"X\":5,\"Y\":30},\"Dest\":{\"X\":5,\"Y\":5},\"Resource\":0,\"Kind\":3,\"Target\":2,\"PlayerId\":0}"));
        Assert.Equal(Sim.Core.Hauling.HaulJobKind.Salvage, job.Kind);
        Assert.Equal(Resource.None, job.Resource);
        Assert.Equal(2, job.Target);
        Assert.Equal(new TileCoord(5, 30), job.Source);
    }

    [Fact]
    public void M38_TheClientsScoutAndIdolPayloads_Parse()
    {
        // The EXACT bytes the prod client sends (Net/HttpServerLink IntentFactory).
        var dispatch = Assert.IsType<Sim.Core.Scouting.DispatchScoutIntent>(IntentJson.Deserialize("DispatchScoutIntent",
            "{\"ScoutUnitId\":5,\"Waypoints\":[{\"X\":10,\"Y\":20},{\"X\":30,\"Y\":40}],\"ReturnRule\":1,\"ElapsedLimitTicks\":0,\"PlayerId\":0}"));
        Assert.Equal(5, dispatch.ScoutUnitId);
        Assert.Equal(new[] { new TileCoord(10, 20), new TileCoord(30, 40) }, dispatch.Waypoints);
        Assert.Equal(Sim.Core.Scouting.ScoutReturnRule.WaypointsExhausted, dispatch.ReturnRule);

        var walk = Assert.IsType<Sim.Core.Scouting.ActivateIdolIntent>(IntentJson.Deserialize("ActivateIdolIntent",
            "{\"UnitId\":3,\"IdolTile\":{\"X\":4,\"Y\":14},\"PlayerId\":0}"));
        Assert.Equal(new TileCoord(4, 14), walk.IdolTile);
        Assert.Null(Assert.IsType<Sim.Core.Scouting.ActivateIdolIntent>(IntentJson.Deserialize("ActivateIdolIntent",
            "{\"UnitId\":3,\"PlayerId\":0}")).IdolTile);
    }

    [Fact]
    public void TheClientsStandingTherePayloads_LeaveTheTargetNull()
    {
        // The field is ABSENT, not null — that is how the client says "no
        // opinion", and it must not land as (0,0).
        Assert.Null(Assert.IsType<EquipUnitIntent>(IntentJson.Deserialize("EquipUnitIntent",
            "{\"UnitId\":7,\"Item\":5,\"PlayerId\":0}")).StoreTile);

        Assert.Null(Assert.IsType<Sim.Core.Caches.LootCacheIntent>(
            IntentJson.Deserialize("LootCacheIntent",
                "{\"UnitId\":3,\"Resource\":1,\"PlayerId\":0}")).CacheTile);

        Assert.Null(Assert.IsType<Sim.Core.Boats.EmbarkIntent>(
            IntentJson.Deserialize("EmbarkIntent",
                "{\"BoatId\":50,\"UnitIds\":[1],\"PlayerId\":0}")).DockTile);
    }

    [Fact]
    public void EquipUnitIntent_CarriesAnOptionalStoreTile()
    {
        // The errand form and the standing-there form are the same intent type,
        // told apart only by whether the tile is present — so both shapes have
        // to survive the durable log, or a replay would arm the wrong soldier
        // in the wrong place.
        var far = new EquipUnitIntent(7, Resource.Sword, new TileCoord(14, 4)) { PlayerId = 0 };
        var (n1, p1) = IntentJson.Serialize(far);
        var r1 = Assert.IsType<EquipUnitIntent>(IntentJson.Deserialize(n1, p1));
        Assert.Equal(new TileCoord(14, 4), r1.StoreTile);
        Assert.Equal(Resource.Sword, r1.Item);

        var here = new EquipUnitIntent(7, Resource.Sword) { PlayerId = 0 };
        var (n2, p2) = IntentJson.Serialize(here);
        Assert.Null(Assert.IsType<EquipUnitIntent>(IntentJson.Deserialize(n2, p2)).StoreTile);
    }

    [Fact]
    public void LootCacheIntent_CarriesAnOptionalCacheTile()
    {
        var far = new Sim.Core.Caches.LootCacheIntent(3, Resource.Wood, new TileCoord(4, 14)) { PlayerId = 1 };
        var (n1, p1) = IntentJson.Serialize(far);
        var r1 = Assert.IsType<Sim.Core.Caches.LootCacheIntent>(IntentJson.Deserialize(n1, p1));
        Assert.Equal(new TileCoord(4, 14), r1.CacheTile);

        var here = new Sim.Core.Caches.LootCacheIntent(3, Resource.Wood) { PlayerId = 1 };
        var (n2, p2) = IntentJson.Serialize(here);
        Assert.Null(Assert.IsType<Sim.Core.Caches.LootCacheIntent>(
            IntentJson.Deserialize(n2, p2)).CacheTile);
    }

    [Fact]
    public void EmbarkIntent_CarriesAnOptionalDockTile()
    {
        var far = new Sim.Core.Boats.EmbarkIntent(50, new[] { 1, 2 }, new TileCoord(9, 4)) { PlayerId = 0 };
        var (n1, p1) = IntentJson.Serialize(far);
        var r1 = Assert.IsType<Sim.Core.Boats.EmbarkIntent>(IntentJson.Deserialize(n1, p1));
        Assert.Equal(new TileCoord(9, 4), r1.DockTile);
        Assert.Equal(new[] { 1, 2 }, r1.UnitIds);

        var here = new Sim.Core.Boats.EmbarkIntent(50, new[] { 1 }) { PlayerId = 0 };
        var (n2, p2) = IntentJson.Serialize(here);
        Assert.Null(Assert.IsType<Sim.Core.Boats.EmbarkIntent>(
            IntentJson.Deserialize(n2, p2)).DockTile);
    }

    [Fact]
    public void ClearRubbleIntent_FromTheClientsWireShape()
    {
        const string payload = "{\"Tile\":{\"X\":12,\"Y\":5},\"PlayerId\":2}";

        var replay = Assert.IsType<Sim.Core.Sieges.ClearRubbleIntent>(
            IntentJson.Deserialize("ClearRubbleIntent", payload));

        Assert.Equal(new TileCoord(12, 5), replay.Tile);
        // Player 2 clearing rubble they never owned is the ordinary case, not
        // an edge one: wreckage belongs to nobody.
        Assert.Equal(2, replay.PlayerId);
    }

    [Fact]
    public void ThePathIntents_FromTheClientsWireShape()
    {
        // The client sends one PlacePathPayload for both, and the TYPE NAME
        // picks which — the two sim intents happen to share a shape. Order is
        // load-bearing in a way most payloads are not: a canal is dug outward
        // from water and a wall is a connected line, so a reordered path is a
        // different build or no build at all.
        const string wall =
            "{\"Path\":[{\"X\":3,\"Y\":1},{\"X\":4,\"Y\":1},{\"X\":5,\"Y\":1}],\"PlayerId\":0}";
        var w = Assert.IsType<Sim.Core.Fortifications.PlaceWallIntent>(
            IntentJson.Deserialize("PlaceWallIntent", wall));
        Assert.Equal(
            new[] { new TileCoord(3, 1), new TileCoord(4, 1), new TileCoord(5, 1) },
            w.Path.ToArray());

        const string canal =
            "{\"Path\":[{\"X\":2,\"Y\":2},{\"X\":2,\"Y\":3}],\"PlayerId\":1}";
        var c = Assert.IsType<Sim.Core.Canals.PlaceCanalIntent>(
            IntentJson.Deserialize("PlaceCanalIntent", canal));
        Assert.Equal(new[] { new TileCoord(2, 2), new TileCoord(2, 3) }, c.Path.ToArray());
        Assert.Equal(1, c.PlayerId);
    }

    [Fact]
    public void CraftEquipmentIntent_FromTheClientsWireShape()
    {
        // The client's CraftEquipmentPayload, verbatim. The field is still
        // BarracksTile even though the catalog now names the forge per item —
        // it is a durable wire name, and renaming it would break every replay
        // ever recorded for a cosmetic gain.
        const string payload =
            "{\"BarracksTile\":{\"X\":6,\"Y\":9},\"Item\":5,\"PlayerId\":0}";

        var replay = Assert.IsType<CraftEquipmentIntent>(
            IntentJson.Deserialize("CraftEquipmentIntent", payload));

        Assert.Equal(new TileCoord(6, 9), replay.BarracksTile);
        Assert.Equal(Resource.Sword, replay.Item);
    }

    // ---- M36 — mixed cargo + the haul queue ------------------------------

    [Fact]
    public void CargoIntents_OldClientShapes_KeepTheirMeaning()
    {
        // The client's existing payloads carry no Amount / Resource / JobId.
        // Absent must mean what these intents meant before M36: a full load,
        // unload everything, a manual haul.
        var unload = Assert.IsType<UnloadCargoIntent>(IntentJson.Deserialize("UnloadCargoIntent",
            "{\"UnitId\":7,\"PlayerId\":0}"));
        Assert.Equal(Resource.None, unload.Resource);
        Assert.Equal(0, unload.Amount);

        var load = Assert.IsType<LoadCargoIntent>(IntentJson.Deserialize("LoadCargoIntent",
            "{\"UnitId\":7,\"Resource\":1,\"PlayerId\":0}"));
        Assert.Equal(0, load.Amount);

        var haul = Assert.IsType<HaulIntent>(IntentJson.Deserialize("HaulIntent",
            "{\"HaulerId\":7,\"SourceTile\":{\"X\":1,\"Y\":2},\"DestTile\":{\"X\":3,\"Y\":4},\"Resource\":1,\"PlayerId\":0}"));
        Assert.Equal(0, haul.Amount);
        Assert.Equal(0, haul.JobId);
    }

    [Fact]
    public void HaulQueueIntents_RoundTrip()
    {
        var set = new Sim.Core.Hauling.SetHaulJobIntent(new TileCoord(1, 2), new TileCoord(3, 4),
            Resource.Iron, Sim.Core.Hauling.HaulJobKind.Once, 40) { PlayerId = 1 };
        var (tn, pl) = IntentJson.Serialize(set);
        Assert.Equal("SetHaulJobIntent", tn);
        var s2 = Assert.IsType<Sim.Core.Hauling.SetHaulJobIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((new TileCoord(1, 2), new TileCoord(3, 4), Resource.Iron, Sim.Core.Hauling.HaulJobKind.Once, 40, 1),
            (s2.Source, s2.Dest, s2.Resource, s2.Kind, s2.Target, s2.PlayerId));

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.ClearHaulJobIntent(9) { PlayerId = 1 });
        Assert.Equal("ClearHaulJobIntent", tn);
        Assert.Equal(9, Assert.IsType<Sim.Core.Hauling.ClearHaulJobIntent>(IntentJson.Deserialize(tn, pl)).JobId);

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.RequeueHaulJobIntent(9) { PlayerId = 1 });
        Assert.Equal("RequeueHaulJobIntent", tn);
        Assert.Equal(9, Assert.IsType<Sim.Core.Hauling.RequeueHaulJobIntent>(IntentJson.Deserialize(tn, pl)).JobId);

        (tn, pl) = IntentJson.Serialize(new HaulIntent(3, new TileCoord(1, 1), new TileCoord(2, 2),
            Resource.Wood, amount: 12, jobId: 9) { PlayerId = 1 });
        var h = Assert.IsType<HaulIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((12, 9), (h.Amount, h.JobId));

        (tn, pl) = IntentJson.Serialize(new UnloadCargoIntent(3, Resource.Ore, amount: 4) { PlayerId = 1 });
        var u = Assert.IsType<UnloadCargoIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((Resource.Ore, 4), (u.Resource, u.Amount));
    }

    [Fact]
    public void RouteIntents_RoundTrip_WithEveryStopAndRule()
    {
        // The stop list is nested collections of a record struct: exactly the
        // shape that round-trips as EMPTY if a collection is get-only (the
        // SetOrderIntent lesson). A route that replays with no stops would be
        // silently deleted by recovery.
        var set = new Sim.Core.Hauling.SetHaulRouteIntent(new()
        {
            new() { Tile = new TileCoord(1, 2), Rules = new()
            {
                new(Resource.Ore, Sim.Core.Hauling.StopRuleOp.Pickup, 40),
                new(Resource.Wood, Sim.Core.Hauling.StopRuleOp.Drop, 25),
            } },
            new() { Tile = new TileCoord(5, 6) },
        }) { PlayerId = 1 };

        var (tn, pl) = IntentJson.Serialize(set);
        Assert.Equal("SetHaulRouteIntent", tn);
        var back = Assert.IsType<Sim.Core.Hauling.SetHaulRouteIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal(2, back.Stops.Count);
        Assert.Equal(new TileCoord(1, 2), back.Stops[0].Tile);
        Assert.Equal(set.Stops[0].Rules, back.Stops[0].Rules);
        Assert.Empty(back.Stops[1].Rules);

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.AddRouteCrewIntent(3, new() { 9, 4 }, 1) { PlayerId = 1 });
        var add = Assert.IsType<Sim.Core.Hauling.AddRouteCrewIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((3, 1), (add.RouteId, add.StartStop));
        Assert.Equal(new[] { 9, 4 }, add.Members);

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.ServeRouteStopIntent(3, 2, 5) { PlayerId = 1 });
        var serve = Assert.IsType<Sim.Core.Hauling.ServeRouteStopIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((3, 2, 5), (serve.RouteId, serve.CrewId, serve.ExpectedStop));

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.RemoveRouteCrewIntent(3, 2) { PlayerId = 1 });
        Assert.Equal(2, Assert.IsType<Sim.Core.Hauling.RemoveRouteCrewIntent>(IntentJson.Deserialize(tn, pl)).CrewId);
        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.ClearHaulRouteIntent(3) { PlayerId = 1 });
        Assert.Equal(3, Assert.IsType<Sim.Core.Hauling.ClearHaulRouteIntent>(IntentJson.Deserialize(tn, pl)).RouteId);
    }

    // M45 — the three edit intents round-trip with every field, and a serve
    // logged before M45 (no ExpectedRevision) replays with the fence off.
    [Fact]
    public void M45_EditIntents_RoundTrip_AndOldServesSkipTheRevisionFence()
    {
        var update = new Sim.Core.Hauling.UpdateHaulRouteIntent(4, new()
        {
            new() { Tile = new TileCoord(2, 3), Rules = new() { new(Resource.Wood, Sim.Core.Hauling.StopRuleOp.Pickup, 60) } },
            new() { Tile = new TileCoord(7, 1) },
        }) { PlayerId = 1 };
        var (tn, pl) = IntentJson.Serialize(update);
        var u = Assert.IsType<Sim.Core.Hauling.UpdateHaulRouteIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal(4, u.RouteId);
        Assert.Equal(2, u.Stops.Count);
        Assert.Equal(update.Stops[0].Rules, u.Stops[0].Rules);

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.RenameHaulRouteIntent(4, "Wood loop") { PlayerId = 1 });
        var r = Assert.IsType<Sim.Core.Hauling.RenameHaulRouteIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((4, "Wood loop"), (r.RouteId, r.Name));

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.UpdateHaulJobIntent(9, Sim.Core.Hauling.HaulJobKind.Once, 40) { PlayerId = 1 });
        var j = Assert.IsType<Sim.Core.Hauling.UpdateHaulJobIntent>(IntentJson.Deserialize(tn, pl));
        Assert.Equal((9, Sim.Core.Hauling.HaulJobKind.Once, 40), (j.JobId, j.Kind, j.Target));

        (tn, pl) = IntentJson.Serialize(new Sim.Core.Hauling.SetHaulRouteIntent(new() { new() { Tile = new TileCoord(1, 1) } },
            name: "North") { PlayerId = 1 });
        Assert.Equal("North", Assert.IsType<Sim.Core.Hauling.SetHaulRouteIntent>(IntentJson.Deserialize(tn, pl)).Name);

        var old = Assert.IsType<Sim.Core.Hauling.ServeRouteStopIntent>(IntentJson.Deserialize("ServeRouteStopIntent",
            "{\"RouteId\":3,\"CrewId\":2,\"ExpectedStop\":5,\"PlayerId\":0}"));
        Assert.Equal(-1, old.ExpectedRevision);
    }

    // M45 — Aow.Net.IntentFactory's JsonUtility output for the edit intents, verbatim.
    [Fact]
    public void M45_TheClientsEditPayloads_DeserializeVerbatim()
    {
        var u = Assert.IsType<Sim.Core.Hauling.UpdateHaulRouteIntent>(IntentJson.Deserialize("UpdateHaulRouteIntent",
            "{\"RouteId\":4,\"Stops\":[{\"Tile\":{\"X\":1,\"Y\":2},\"Rules\":[{\"Resource\":1,\"Op\":1,\"Percent\":60}]}," +
            "{\"Tile\":{\"X\":6,\"Y\":2},\"Rules\":[]}],\"PlayerId\":0}"));
        Assert.Equal((4, 2), (u.RouteId, u.Stops.Count));
        Assert.Equal(new Sim.Core.Hauling.StopRule(Resource.Wood, Sim.Core.Hauling.StopRuleOp.Pickup, 60), u.Stops[0].Rules[0]);

        var r = Assert.IsType<Sim.Core.Hauling.RenameHaulRouteIntent>(IntentJson.Deserialize("RenameHaulRouteIntent",
            "{\"RouteId\":4,\"Name\":\"North farms\",\"PlayerId\":0}"));
        Assert.Equal((4, "North farms"), (r.RouteId, r.Name));

        var j = Assert.IsType<Sim.Core.Hauling.UpdateHaulJobIntent>(IntentJson.Deserialize("UpdateHaulJobIntent",
            "{\"JobId\":9,\"Kind\":2,\"Target\":40,\"PlayerId\":0}"));
        Assert.Equal((9, Sim.Core.Hauling.HaulJobKind.Once, 40), (j.JobId, j.Kind, j.Target));

        var set = Assert.IsType<Sim.Core.Hauling.SetHaulRouteIntent>(IntentJson.Deserialize("SetHaulRouteIntent",
            "{\"Stops\":[{\"Tile\":{\"X\":1,\"Y\":2},\"Rules\":[]}],\"Crew\":[11],\"Name\":\"\",\"PlayerId\":0}"));
        Assert.Equal("", set.Name);
    }

    [Fact]
    public void TheClientsHaulPayloads_DeserializeVerbatim()
    {
        // Aow.Net.IntentFactory's JsonUtility output, verbatim: PascalCase, enums
        // as ints, lists as arrays. If a name drifts on either side, a queued job
        // or a drawn route would silently arrive empty.
        var job = Assert.IsType<Sim.Core.Hauling.SetHaulJobIntent>(IntentJson.Deserialize("SetHaulJobIntent",
            "{\"Source\":{\"X\":4,\"Y\":5},\"Dest\":{\"X\":9,\"Y\":7},\"Resource\":4,\"Kind\":1,\"Target\":120,\"PlayerId\":0}"));
        Assert.Equal((new TileCoord(4, 5), new TileCoord(9, 7), Resource.Food, Sim.Core.Hauling.HaulJobKind.Standing, 120),
            (job.Source, job.Dest, job.Resource, job.Kind, job.Target));

        var route = Assert.IsType<Sim.Core.Hauling.SetHaulRouteIntent>(IntentJson.Deserialize("SetHaulRouteIntent",
            "{\"Stops\":[{\"Tile\":{\"X\":1,\"Y\":2},\"Rules\":[{\"Resource\":3,\"Op\":1,\"Percent\":40}]}," +
            "{\"Tile\":{\"X\":6,\"Y\":2},\"Rules\":[]}],\"Crew\":[11,12],\"PlayerId\":0}"));
        Assert.Equal(2, route.Stops.Count);
        Assert.Equal(new Sim.Core.Hauling.StopRule(Resource.Ore, Sim.Core.Hauling.StopRuleOp.Pickup, 40),
            Assert.Single(route.Stops[0].Rules));
        Assert.Equal(new[] { 11, 12 }, route.Crew);

        var crew = Assert.IsType<Sim.Core.Hauling.AddRouteCrewIntent>(IntentJson.Deserialize("AddRouteCrewIntent",
            "{\"RouteId\":3,\"Members\":[7],\"StartStop\":2,\"PlayerId\":0}"));
        Assert.Equal((3, 2), (crew.RouteId, crew.StartStop));
        Assert.Equal(7, Assert.Single(crew.Members));

        Assert.Equal(5, Assert.IsType<Sim.Core.Hauling.ClearHaulJobIntent>(IntentJson.Deserialize("ClearHaulJobIntent",
            "{\"JobId\":5,\"PlayerId\":0}")).JobId);
        Assert.Equal(3, Assert.IsType<Sim.Core.Hauling.ClearHaulRouteIntent>(IntentJson.Deserialize("ClearHaulRouteIntent",
            "{\"RouteId\":3,\"PlayerId\":0}")).RouteId);
        Assert.Equal(2, Assert.IsType<Sim.Core.Hauling.RemoveRouteCrewIntent>(IntentJson.Deserialize("RemoveRouteCrewIntent",
            "{\"RouteId\":3,\"CrewId\":2,\"PlayerId\":0}")).CrewId);
    }

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
