using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Hauling;
using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M50 — march modes and saved formations (docs/m50-march-formations-spec.md): a column
// that holds its shape on the straights and moves at its slowest member's walking speed,
// every member stepping every beat (no stop-start); a single file faster still; a facing
// that doesn't flip on a diagonal; a formation arranged and saved in the world, mustered
// into and marched in; gaps that close; caravans in single file; deterministic.
public class MarchFormationTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 40, int h = 24)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        world.Players[0] = new Player(0);
        return (new Simulation(world, seed: 3), world);
    }

    private static IntentOutcome Do(Simulation sim, Intent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(until: sim.Now);
        return sim.ResolvedLog.OfType<IntentEvent>().Last(e => e.Intent == intent).Outcome;
    }

    private static void RunUntil(Simulation sim, Func<bool> done, long budget = 100_000)
    {
        var end = sim.Now + budget;
        for (var t = sim.Now + 1; t <= end && !done(); t++) sim.Run(until: t);
        Assert.True(done(), "timed out");
    }

    // `n` soldiers created as a group and mustered at `at`, Idle.
    private static int Formed(Simulation sim, GameWorld world, int n, TileCoord at, int firstId = 1)
    {
        var ids = Enumerable.Range(firstId, n).ToArray();
        foreach (var id in ids) world.AddUnit(new Unit(id, at) { Role = UnitRole.Soldier });
        Assert.False(Do(sim, new CreateGroupIntent("G", ids)).IsRejected);
        var gid = world.NextGroupId - 1;
        Assert.False(Do(sim, new MusterGroupIntent(gid, at)).IsRejected);
        RunUntil(sim, () => world.Groups[gid].State == GroupState.Idle);
        return gid;
    }

    // Ticks a lone soldier takes to walk from `from` to `to`.
    private static long SoloTicks(TileCoord from, TileCoord to)
    {
        var (sim, world) = MakeWorld();
        var u = world.AddUnit(new Unit(1, from) { Role = UnitRole.Soldier });
        var start = sim.Now;
        Assert.False(Do(sim, new MoveIntent(1, to)).IsRejected);
        RunUntil(sim, () => !u.IsWalking && u.Position == to);
        return sim.Now - start;
    }

    // March and watch every beat: the share of member-beats that moved (smoothness) once the
    // column has formed, and the ticks to come to rest.
    private static (double MovingShare, long Ticks) March(Simulation sim, GameWorld world, int gid, TileCoord to)
    {
        var group = world.Groups[gid];
        var start = sim.Now;
        Assert.False(Do(sim, new MoveGroupIntent(gid, to)).IsRejected);
        long? beat = null;
        Dictionary<int, (TileCoord, Subtile?)>? last = null;
        int moved = 0, total = 0;
        for (var tick = start + 1; group.State != GroupState.Idle && tick < start + 100_000; tick++)
        {
            sim.Run(until: tick);
            if (group.MarchPath is not { } path || group.NextStepTick == beat) continue;
            beat = group.NextStepTick;
            var now = group.Members.ToDictionary(id => id, id => (world.Units[id].Position, world.Units[id].Subtile));
            // Count only the middle of the march: formed up behind the start, short of the end.
            if (last is not null && group.MarchLead > 8 && group.MarchLead < path.Count - 8)
                foreach (var (id, at) in now) { total++; if (last[id] != at) moved++; }
            last = now;
        }
        Assert.Equal(GroupState.Idle, group.State);
        return (total == 0 ? 0 : (double)moved / total, sim.Now - start);
    }

    [Fact]
    public void Column_MovesAtWalkingSpeed_EveryMemberSteppingEveryBeat()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 8, new TileCoord(4, 10));

        var (moving, ticks) = March(sim, world, gid, new TileCoord(26, 10));

        var solo = SoloTicks(new TileCoord(4, 10), new TileCoord(26, 10));
        Assert.True(ticks <= solo * 5 / 4, $"column took {ticks} ticks; one soldier alone {solo}");
        Assert.True(moving >= 0.95, $"only {moving:P0} of member-beats moved on the straight: stop-start");
        Assert.All(world.Groups[gid].Members, id => Assert.Equal(new TileCoord(26, 10), world.Units[id].Position));
    }

    [Fact]
    public void SingleFile_IsFasterStill_AndStepsEveryBeat()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 8, new TileCoord(4, 10));
        Assert.False(Do(sim, new SetGroupMarchModeIntent(gid, MarchMode.SingleFile)).IsRejected);

        var (moving, ticks) = March(sim, world, gid, new TileCoord(26, 10));

        var solo = SoloTicks(new TileCoord(4, 10), new TileCoord(26, 10));
        // The whole caravan arrives within 1.2× one soldier's time: its last member starts the
        // line's length behind the leader, so the gap is the caravan's length, not stalling.
        Assert.True(ticks <= solo * 6 / 5, $"single file took {ticks} ticks; one soldier alone {solo}");
        Assert.True(moving >= 0.95, $"only {moving:P0} of member-beats moved");
    }

    [Fact]
    public void Column_HoldsItsShape_OnTheStraight()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 8, new TileCoord(4, 10));
        var group = world.Groups[gid];
        Assert.False(Do(sim, new MoveGroupIntent(gid, new TileCoord(30, 10))).IsRejected);

        int held = 0, samples = 0;
        long? beat = null;
        var budget = sim.Now + 100_000;
        for (var tick = sim.Now + 1; group.State != GroupState.Idle && tick < budget; tick++)
        {
            sim.Run(until: tick);
            if (group.MarchPath is not { } path || group.NextStepTick == beat) continue;
            beat = group.NextStepTick;
            var t = group.MarchLead;
            if (t <= 8 || t >= path.Count - 8) continue;
            samples++;
            // Every member within a subtile of its spot: P[t − B] + A to the right (south,
            // marching east).
            var ok = Formations.Assign(group, group.Members.Select(id => world.Units[id]).ToList()).All(s =>
            {
                var anchor = path[t - s.B];
                var u = s.Member;
                var here = WorldSubtile.Of(u.Position, u.Subtile!.Value);
                return Math.Abs(here.X - anchor.X) + Math.Abs(here.Y - (anchor.Y + s.A)) <= 1;
            });
            if (ok) held++;
        }
        Assert.Equal(GroupState.Idle, group.State);
        Assert.True(samples > 20);
        Assert.True(held >= samples * 9 / 10, $"shape held in {held} of {samples} beats");
    }

    [Fact]
    public void ADiagonalRoute_TurnsTheFormation_AtMostTwice()
    {
        var (_, world) = MakeWorld();
        var group = new Group(1) { OwnerId = 0, Position = new TileCoord(2, 2) };
        var path = GroupMarch.Plan(world, group, new TileCoord(30, 20), 0)!;
        var turns = 0;
        for (var j = 1; j < path.Count; j++)
            if (Formations.FacingAt(path, j) != Formations.FacingAt(path, j - 1)) turns++;
        Assert.InRange(turns, 0, 2);
    }

    [Fact]
    public void ArrangedAndSaved_TheFormationMustersAndMarchesInItsShape()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 3, new TileCoord(10, 10));
        // A wedge facing east: the leader ahead, two behind it on either side.
        var tile = new TileCoord(10, 10);
        Assert.False(Do(sim, new ArrangeGroupMemberIntent(gid, 1, tile, 2, 1)).IsRejected);
        Assert.False(Do(sim, new ArrangeGroupMemberIntent(gid, 2, tile, 1, 0)).IsRejected);
        Assert.False(Do(sim, new ArrangeGroupMemberIntent(gid, 3, tile, 1, 2)).IsRejected);
        RunUntil(sim, () => world.Groups[gid].Members.All(id => !world.Units[id].IsWalking));
        Assert.False(Do(sim, new SaveGroupFormationIntent(gid, 1, Heading.East)).IsRejected);
        var saved = world.Groups[gid].Formation!;
        Assert.Contains(saved.Slots, s => s.UnitId == 1 && (s.A, s.B) == (0, 0));
        Assert.Contains(saved.Slots, s => s.UnitId == 2 && (s.A, s.B) == (-1, 1));   // left, one behind
        Assert.Contains(saved.Slots, s => s.UnitId == 3 && (s.A, s.B) == (1, 1));    // right, one behind

        // Mustered elsewhere: it forms up in the wedge, still facing east, the leader on the
        // anchor's centre.
        Do(sim, new DismissGroupIntent(gid));
        Assert.False(Do(sim, new MusterGroupIntent(gid, new TileCoord(20, 5))).IsRejected);
        RunUntil(sim, () => world.Groups[gid].State == GroupState.Idle);
        WorldSubtile At(int id) => WorldSubtile.Of(world.Units[id].Position, world.Units[id].Subtile!.Value);
        var lead = At(1);
        Assert.Equal(new TileCoord(20, 5), lead.Tile);
        Assert.Equal((lead.X - 1, lead.Y - 1), (At(2).X, At(2).Y));
        Assert.Equal((lead.X - 1, lead.Y + 1), (At(3).X, At(3).Y));

        // Marched south, it arrives in the same wedge turned to face south.
        Assert.False(Do(sim, new MoveGroupIntent(gid, new TileCoord(20, 18))).IsRejected);
        RunUntil(sim, () => world.Groups[gid].State == GroupState.Idle);
        lead = At(1);
        Assert.Equal((lead.X + 1, lead.Y - 1), (At(2).X, At(2).Y));   // facing south, left is east
        Assert.Equal((lead.X - 1, lead.Y - 1), (At(3).X, At(3).Y));
    }

    [Fact]
    public void ArrangeAndSave_Refusals()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 2, new TileCoord(10, 10));
        world.AddUnit(new Unit(9, new TileCoord(3, 3)) { Role = UnitRole.Soldier });
        Assert.True(Do(sim, new ArrangeGroupMemberIntent(gid, 9, new TileCoord(10, 10), 0, 0)).IsRejected);    // not a member
        Assert.True(Do(sim, new ArrangeGroupMemberIntent(gid, 1, new TileCoord(20, 10), 0, 0)).IsRejected);    // too far
        Assert.True(Do(sim, new ArrangeGroupMemberIntent(gid, 1, new TileCoord(10, 10), 5, 0)).IsRejected);    // not a subtile
        Assert.True(Do(sim, new SaveGroupFormationIntent(gid, 9, Heading.North)).IsRejected);                  // leader not a member
        Do(sim, new MoveGroupIntent(gid, new TileCoord(20, 10)));
        Assert.True(Do(sim, new SaveGroupFormationIntent(gid, 1, Heading.North)).IsRejected);                  // not standing formed
    }

    [Fact]
    public void Gaps_CloseFromTheBack_AndNewcomersFallInBehind()
    {
        var (sim, world) = MakeWorld();
        var group = new Group(1) { OwnerId = 0 };
        group.Formation = new SavedFormation
        {
            Front = Heading.North,
            Slots = new()
            {
                new FormationSlot(1, UnitRole.Soldier, 0, 0),
                new FormationSlot(2, UnitRole.Archer, 0, 1),
                new FormationSlot(3, UnitRole.Soldier, 0, 2),
            },
        };
        // The leader (1) is gone; a soldier newcomer (7) joined.
        var two = new Unit(2, default) { Role = UnitRole.Archer };
        var three = new Unit(3, default) { Role = UnitRole.Soldier };
        var seven = new Unit(7, default) { Role = UnitRole.Soldier };

        var shape = Formations.Assign(group, new[] { two, three, seven });

        // The same-role newcomer takes the empty front slot; nobody's place is left empty
        // ahead of a filled one.
        Assert.Contains(shape, s => s.Member.Id == 7 && (s.A, s.B) == (0, 0));
        Assert.Contains(shape, s => s.Member.Id == 2 && (s.A, s.B) == (0, 1));
        Assert.Contains(shape, s => s.Member.Id == 3 && (s.A, s.B) == (0, 2));

        // Without the newcomer, the archer and the rear soldier move up.
        var closed = Formations.Assign(group, new[] { two, three });
        Assert.Equal(new[] { 0, 1 }, closed.Select(s => s.B).OrderBy(b => b).ToArray());
    }

    [Fact]
    public void ACrewOnARoute_MarchesInSingleFile_AndModeCascades()
    {
        var (sim, world) = MakeWorld();
        world.AddStructure(new Stockpile(new TileCoord(4, 4)) { OwnerId = 0 });
        world.AddUnit(new Unit(1, new TileCoord(4, 4)) { Role = UnitRole.Hauler });
        var route = world.NextHaulRouteId;
        new SetHaulRouteIntent(new() { new RouteStop { Tile = new TileCoord(4, 4) }, new RouteStop { Tile = new TileCoord(14, 4) } })
            { PlayerId = 0 }.Resolve(sim);
        var crew = TestGroups.Crew(sim, route, 0, new[] { 1 });
        Assert.Equal(MarchMode.SingleFile, world.Groups[crew].MarchMode);

        Assert.False(Do(sim, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true)).IsRejected);
        var army = world.NextGroupId - 1;
        Do(sim, new SetGroupParentIntent(crew, army));
        Assert.False(Do(sim, new SetGroupMarchModeIntent(army, MarchMode.Column)).IsRejected);
        Assert.Equal(MarchMode.Column, world.Groups[crew].MarchMode);
    }

    [Fact]
    public void SwitchingModeMidMarch_ReformsOnTheMove_AndArrives()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 6, new TileCoord(4, 10));
        Do(sim, new MoveGroupIntent(gid, new TileCoord(30, 10)));
        sim.Run(until: sim.Now + 600);
        Assert.NotNull(world.Groups[gid].MarchPath);
        Assert.False(Do(sim, new SetGroupMarchModeIntent(gid, MarchMode.SingleFile)).IsRejected);
        RunUntil(sim, () => world.Groups[gid].State == GroupState.Idle);
        Assert.All(world.Groups[gid].Members, id =>
            Assert.True(Math.Abs(world.Units[id].Position.X - 30) <= 1, $"unit {id} at {world.Units[id].Position}"));
    }

    private static Simulation Scene()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, 7, new TileCoord(4, 6));
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(28, 18)));
        return sim;
    }

    [Fact]
    public void TwinRun_AndMidMarchRestore_EndTheSame()
    {
        var a = Scene(); var end = a.Now + 20_000; a.Run(until: end);
        var b = Scene(); b.Run(until: end);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));

        var mid = Scene();
        mid.Run(until: mid.Now + 700);
        Assert.NotNull(mid.World.Groups.Values.Single().MarchPath);
        var restored = Snapshot.Restore(Snapshot.Serialize(mid), seed: 3);
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
        mid.Run(until: end);
        restored.Run(until: end);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(mid));
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
    }
}
