using Sim.Core.Boats;
using Sim.Core.Groups;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Persistence;

namespace Sim.Persistence.Tests;

// M4 Phase C tests. Durable intent log round-trips every intent type;
// ordering by (tick, seq) is stable; reopen-after-close preserves data;
// transaction rollback on a failure keeps the table consistent.
public class IntentStoreTests
{
    // ---------- Round-trip every intent type ----------

    public static IEnumerable<object[]> AllIntentTypes()
    {
        yield return new object[] { new MoveIntent(unitId: 7, new TileCoord(3, 4)) { PlayerId = 0 } };
        yield return new object[] { new PlaceSiteIntent(new TileCoord(1, 1), StructureKind.LumberCamp) { PlayerId = 0 } };
        yield return new object[] { new BuildIntent(new TileCoord(1, 2), StructureKind.Farm, builderId: 4, workerToManId: 5) { PlayerId = 0 } };
        yield return new object[] { new AssignBuildersIntent(new TileCoord(2, 2), new[] { 1, 2, 3 }) { PlayerId = 0 } };
        yield return new object[] { new AssignWorkersIntent(new TileCoord(3, 3), new[] { 10, 11 }) { PlayerId = 0 } };
        yield return new object[] { new UnassignWorkersIntent(new TileCoord(3, 3), new[] { 11 }) { PlayerId = 0 } };
        yield return new object[] { new HaulIntent(haulerId: 5, new TileCoord(0, 0), new TileCoord(9, 9), Resource.Wood) { PlayerId = 0 } };
        yield return new object[] { new FormGroupIntent(new[] { 1, 2, 3 }, new TileCoord(5, 5)) { PlayerId = 0 } };
        yield return new object[] { new MoveGroupIntent(groupId: 1, new TileCoord(9, 9)) { PlayerId = 0 } };
        yield return new object[] { new DisbandGroupIntent(groupId: 1) { PlayerId = 0 } };
        // M46 — groups as records.
        yield return new object[] { new CreateGroupIntent("Bridge Guard", new[] { 4, 5 }, parentId: 2) { PlayerId = 0 } };
        yield return new object[] { new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true) { PlayerId = 0 } };
        yield return new object[] { new RenameGroupIntent(groupId: 3, "Night Watch") { PlayerId = 0 } };
        yield return new object[] { new SetGroupParentIntent(groupId: 3, parentId: null) { PlayerId = 0 } };
        yield return new object[] { new AddToGroupIntent(groupId: 3, new[] { 8, 9 }) { PlayerId = 0 } };
        yield return new object[] { new DeleteGroupIntent(groupId: 3) { PlayerId = 0 } };
        yield return new object[] { new MusterGroupIntent(groupId: 1, new TileCoord(7, 8)) { PlayerId = 0 } };
        yield return new object[] { new DismissGroupIntent(groupId: 1) { PlayerId = 0 } };
        yield return new object[] { new MergeGroupsIntent(fromId: 2, intoId: 1) { PlayerId = 0 } };
        yield return new object[] { new SplitGroupIntent(groupId: 1, new[] { 4 }, "Scouts") { PlayerId = 0 } };
        yield return new object[] { new SplitGroupIntent(groupId: 1, new[] { 5 }) { PlayerId = 0 } };
        yield return new object[] { new MoveGroupIntent(groupId: 2, new TileCoord(4, 4), forRoute: true) { PlayerId = 0 } };
        yield return new object[] { new Sim.Core.Hauling.AssignGroupToRouteIntent(groupId: 2, routeId: 3, startStop: 1) { PlayerId = 0 } };
        yield return new object[] { new Sim.Core.Hauling.UnassignGroupFromRouteIntent(groupId: 2) { PlayerId = 0 } };
        yield return new object[] { new SetGroupStanceIntent(groupId: 2, GroupStance.Aggressive) { PlayerId = 0 } };
        yield return new object[] { new ChargeGroupIntent(groupId: 2, targetUnitId: 40) { PlayerId = 0 } };
        yield return new object[] { new TrainUnitIntent(unitId: 7, UnitRole.Builder) { PlayerId = 0 } };
        yield return new object[] { new EmbarkIntent(boatId: 50, new[] { 1, 2, 3 }) { PlayerId = 0 } };
        yield return new object[] { new DisembarkIntent(boatId: 50) { PlayerId = 0 } };
    }

    [Theory]
    [MemberData(nameof(AllIntentTypes))]
    public void RoundTrip_EveryIntentType(Sim.Core.Intents.Intent original)
    {
        using var store = SqliteIntentStore.OpenInMemory();
        var (typeName, payload) = IntentJson.Serialize(original);
        store.AppendIntent(tick: 42, seq: 7, playerId: original.PlayerId,
            typeName: typeName, payloadJson: payload);

        var loaded = store.LoadIntentsAfter(0).Single();
        Assert.Equal(42, loaded.Tick);
        Assert.Equal(7, loaded.Seq);
        Assert.Equal(typeName, loaded.TypeName);
        var roundTripped = IntentJson.Deserialize(loaded.TypeName, loaded.PayloadJson);
        Assert.Equal(original.GetType(), roundTripped.GetType());
        Assert.Equal(original.PlayerId, roundTripped.PlayerId);
        // Spot-check identifying payload fields per intent type.
        AssertIntentEquivalent(original, roundTripped);
    }

    private static void AssertIntentEquivalent(Sim.Core.Intents.Intent a, Sim.Core.Intents.Intent b)
    {
        switch (a)
        {
            case BuildIntent ba when b is BuildIntent bb:
                Assert.Equal(ba.Tile, bb.Tile);
                Assert.Equal(ba.Kind, bb.Kind);
                Assert.Equal(ba.BuilderId, bb.BuilderId);
                Assert.Equal(ba.WorkerToManId, bb.WorkerToManId); break;
            case MoveIntent ma when b is MoveIntent mb:
                Assert.Equal(ma.UnitId, mb.UnitId);
                Assert.Equal(ma.Destination, mb.Destination); break;
            case PlaceSiteIntent pa when b is PlaceSiteIntent pb:
                Assert.Equal(pa.Tile, pb.Tile); Assert.Equal(pa.Kind, pb.Kind); break;
            case AssignBuildersIntent aba when b is AssignBuildersIntent abb:
                Assert.Equal(aba.SiteTile, abb.SiteTile);
                Assert.Equal(aba.BuilderIds, abb.BuilderIds); break;
            case AssignWorkersIntent aw1 when b is AssignWorkersIntent aw2:
                Assert.Equal(aw1.StructureTile, aw2.StructureTile);
                Assert.Equal(aw1.WorkerIds, aw2.WorkerIds); break;
            case UnassignWorkersIntent uw1 when b is UnassignWorkersIntent uw2:
                Assert.Equal(uw1.StructureTile, uw2.StructureTile);
                Assert.Equal(uw1.WorkerIds, uw2.WorkerIds); break;
            case HaulIntent ha when b is HaulIntent hb:
                Assert.Equal(ha.HaulerId, hb.HaulerId);
                Assert.Equal(ha.SourceTile, hb.SourceTile);
                Assert.Equal(ha.DestTile, hb.DestTile);
                Assert.Equal(ha.Resource, hb.Resource); break;
            case FormGroupIntent fa when b is FormGroupIntent fb:
                Assert.Equal(fa.UnitIds, fb.UnitIds);
                Assert.Equal(fa.RendezvousTile, fb.RendezvousTile); break;
            case MoveGroupIntent mga when b is MoveGroupIntent mgb:
                Assert.Equal(mga.GroupId, mgb.GroupId);
                Assert.Equal(mga.Destination, mgb.Destination);
                Assert.Equal(mga.ForRoute, mgb.ForRoute); break;
            case Sim.Core.Hauling.AssignGroupToRouteIntent aga when b is Sim.Core.Hauling.AssignGroupToRouteIntent agb:
                Assert.Equal(aga.GroupId, agb.GroupId);
                Assert.Equal(aga.RouteId, agb.RouteId);
                Assert.Equal(aga.StartStop, agb.StartStop); break;
            case Sim.Core.Hauling.UnassignGroupFromRouteIntent uga when b is Sim.Core.Hauling.UnassignGroupFromRouteIntent ugb:
                Assert.Equal(uga.GroupId, ugb.GroupId); break;
            case SetGroupStanceIntent ssa when b is SetGroupStanceIntent ssb:
                Assert.Equal(ssa.GroupId, ssb.GroupId);
                Assert.Equal(ssa.Stance, ssb.Stance); break;
            case ChargeGroupIntent cga when b is ChargeGroupIntent cgb:
                Assert.Equal(cga.GroupId, cgb.GroupId);
                Assert.Equal(cga.TargetUnitId, cgb.TargetUnitId); break;
            case DisbandGroupIntent da when b is DisbandGroupIntent db:
                Assert.Equal(da.GroupId, db.GroupId); break;
            case CreateGroupIntent ca when b is CreateGroupIntent cb:
                Assert.Equal(ca.Name, cb.Name);
                Assert.Equal(ca.UnitIds, cb.UnitIds);
                Assert.Equal(ca.ParentId, cb.ParentId);
                Assert.Equal(ca.HoldsGroups, cb.HoldsGroups); break;
            case RenameGroupIntent ra when b is RenameGroupIntent rb:
                Assert.Equal(ra.GroupId, rb.GroupId);
                Assert.Equal(ra.Name, rb.Name); break;
            case SetGroupParentIntent sa when b is SetGroupParentIntent sb:
                Assert.Equal(sa.GroupId, sb.GroupId);
                Assert.Equal(sa.ParentId, sb.ParentId); break;
            case AddToGroupIntent aa when b is AddToGroupIntent ab:
                Assert.Equal(aa.GroupId, ab.GroupId);
                Assert.Equal(aa.UnitIds, ab.UnitIds); break;
            case DeleteGroupIntent dga when b is DeleteGroupIntent dgb:
                Assert.Equal(dga.GroupId, dgb.GroupId); break;
            case MusterGroupIntent mua when b is MusterGroupIntent mub:
                Assert.Equal(mua.GroupId, mub.GroupId);
                Assert.Equal(mua.Anchor, mub.Anchor); break;
            case DismissGroupIntent dia2 when b is DismissGroupIntent dib2:
                Assert.Equal(dia2.GroupId, dib2.GroupId); break;
            case MergeGroupsIntent mga2 when b is MergeGroupsIntent mgb2:
                Assert.Equal(mga2.FromId, mgb2.FromId);
                Assert.Equal(mga2.IntoId, mgb2.IntoId); break;
            case SplitGroupIntent sga when b is SplitGroupIntent sgb:
                Assert.Equal(sga.GroupId, sgb.GroupId);
                Assert.Equal(sga.UnitIds, sgb.UnitIds);
                Assert.Equal(sga.NewName, sgb.NewName); break;
            case TrainUnitIntent ta when b is TrainUnitIntent tb:
                Assert.Equal(ta.UnitId, tb.UnitId);
                Assert.Equal(ta.NewRole, tb.NewRole); break;
            case EmbarkIntent ea when b is EmbarkIntent eb:
                Assert.Equal(ea.BoatId, eb.BoatId);
                Assert.Equal(ea.UnitIds, eb.UnitIds); break;
            case DisembarkIntent dia when b is DisembarkIntent dib:
                Assert.Equal(dia.BoatId, dib.BoatId); break;
            default:
                Assert.Fail($"Unrecognized intent type pair: {a.GetType().Name} vs {b.GetType().Name}");
                break;
        }
    }

    // ---------- LoadIntentsAfter ordering + filtering ----------

    [Fact]
    public void LoadIntentsAfter_FiltersByTick_OrderedByTickSeq()
    {
        using var store = SqliteIntentStore.OpenInMemory();
        // Insert out of order to prove ORDER BY does its job.
        Append(store, tick: 150, seq: 3);
        Append(store, tick: 50,  seq: 1);
        Append(store, tick: 100, seq: 2);
        Append(store, tick: 100, seq: 1);
        Append(store, tick: 200, seq: 0);

        var afterHundred = store.LoadIntentsAfter(100).ToList();
        Assert.Equal(2, afterHundred.Count);
        Assert.Equal((150L, 3L), (afterHundred[0].Tick, afterHundred[0].Seq));
        Assert.Equal((200L, 0L), (afterHundred[1].Tick, afterHundred[1].Seq));

        var all = store.LoadIntentsAfter(-1).ToList();
        Assert.Equal(5, all.Count);
        // Same-tick rows must be ordered by seq ascending.
        Assert.Equal((100L, 1L), (all[1].Tick, all[1].Seq));
        Assert.Equal((100L, 2L), (all[2].Tick, all[2].Seq));
    }

    private static void Append(SqliteIntentStore store, long tick, long seq)
    {
        var intent = new MoveIntent(unitId: 1, new TileCoord(0, 0));
        var (typeName, payload) = IntentJson.Serialize(intent);
        store.AppendIntent(tick, seq, playerId: 0, typeName: typeName, payloadJson: payload);
    }

    // ---------- Reopen-after-close ----------

    [Fact]
    public void ReopenAfterClose_DataPersists()
    {
        var tempPath = Path.Combine(Path.GetTempPath(),
            $"aow-intentstore-{Guid.NewGuid():N}.db");
        try
        {
            using (var s1 = SqliteIntentStore.Open(tempPath))
            {
                Append(s1, tick: 5, seq: 0);
                Append(s1, tick: 5, seq: 1);
            }
            // Connection closed via Dispose; reopen.
            using var s2 = SqliteIntentStore.Open(tempPath);
            var rows = s2.LoadIntentsAfter(-1).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal((5L, 0L), (rows[0].Tick, rows[0].Seq));
            Assert.Equal((5L, 1L), (rows[1].Tick, rows[1].Seq));
        }
        finally
        {
            CleanupSqliteFiles(tempPath);
        }
    }

    // ---------- Transaction integrity ----------

    [Fact]
    public void DuplicatePrimaryKey_ThrowsAndLeavesNoPartialState()
    {
        using var store = SqliteIntentStore.OpenInMemory();
        Append(store, tick: 10, seq: 5);
        // Same (tick, seq) → PRIMARY KEY constraint violation.
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(
            () => Append(store, tick: 10, seq: 5));
        // The store still contains exactly one row; the failed insert
        // rolled back without trace.
        var rows = store.LoadIntentsAfter(-1).ToList();
        Assert.Single(rows);
    }

    private static void CleanupSqliteFiles(string path)
    {
        // Microsoft.Data.Sqlite pools connections per connection string;
        // Dispose returns the handle to the pool rather than closing it,
        // so the .db file can still be locked here. Flush the pools first.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // SQLite WAL leaves -wal and -shm sidecars; clean them too.
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }
}
