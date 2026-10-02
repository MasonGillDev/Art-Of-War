using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M46 Phase C — the group walks (GroupMarch; docs/m46-groups-spec.md, "Formation
// march"): a column that stays together, at its slowest member's pace, narrowing where
// the ground does, closing into a block that may spill past one tile, deterministic
// across a twin run and a mid-march restore.
public class GroupMarchTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 32, int h = 20)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        world.Players[0] = new Player(0);
        return (new Simulation(world, seed: 1), world);
    }

    // A group of `count` soldiers formed up at `at` (FormGroup, walked in and Idle). More
    // than a tile holds start in the tiles below it, sixteen to a tile, as a formed block
    // would stand.
    private static int Formed(Simulation sim, GameWorld world, TileCoord at, int count, Action<Unit>? dress = null)
    {
        var ids = Enumerable.Range(1, count).ToArray();
        foreach (var id in ids)
        {
            var u = world.AddUnit(new Unit(id, new TileCoord(at.X, at.Y + (id - 1) / 16)) { Role = UnitRole.Soldier });
            dress?.Invoke(u);
        }
        sim.SubmitIntent(sim.Now, new FormGroupIntent(ids, at));
        sim.Run(until: sim.Now + 2000);
        var gid = world.Groups.Keys.Last();
        Assert.Equal(GroupState.Idle, world.Groups[gid].State);
        return gid;
    }

    // The widest gap, in tiles, between two members.
    private static int Spread(GameWorld world, Group group)
    {
        var ps = group.Members.Select(id => world.Units[id].Position).ToList();
        var spread = 0;
        foreach (var a in ps)
            foreach (var b in ps)
                spread = Math.Max(spread, Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
        return spread;
    }

    // March and watch: the largest spread seen while the column marched, and the tick
    // the group came to rest.
    private static (int MaxSpread, long Arrived) March(Simulation sim, GameWorld world, int gid, TileCoord to)
    {
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, to));
        var group = world.Groups[gid];
        var maxSpread = 0;
        var columnSamples = 0;
        for (var t = sim.Now + 1; t < 200_000; t += 10)
        {
            sim.Run(until: t);
            if (group.MarchPath is not null)
            {
                columnSamples++;
                maxSpread = Math.Max(maxSpread, Spread(world, group));
            }
            if (group.State == GroupState.Idle)
            {
                Assert.True(columnSamples > 10, $"the column marched for only {columnSamples} samples");
                return (maxSpread, sim.Now);
            }
        }
        throw new Xunit.Sdk.XunitException("the group never came to rest");
    }

    [Fact]
    public void Column_StaysTogether_AndClosesOnTheDestination()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, new TileCoord(3, 10), 16);

        var (spread, _) = March(sim, world, gid, new TileCoord(26, 10));

        Assert.InRange(spread, 0, 3);   // sixteen four abreast: four ranks, a tile and a bit
        var group = world.Groups[gid];
        Assert.Equal(new TileCoord(26, 10), group.Position);
        Assert.Null(group.MarchPath);
        Assert.All(group.Members, id => Assert.Equal(new TileCoord(26, 10), world.Units[id].Position));
        Assert.Equal(16, group.Members.Select(id => world.Units[id].Subtile).Distinct().Count());
        Assert.All(group.Members, id => Assert.False(world.Units[id].IsWalking));
    }

    [Fact]
    public void TheSlowestMember_SetsThePace()
    {
        var (simA, worldA) = MakeWorld();
        var a = Formed(simA, worldA, new TileCoord(3, 10), 8);
        var (_, startA) = (0, simA.Now);
        var (_, arrivedA) = March(simA, worldA, a, new TileCoord(26, 10));

        var (simB, worldB) = MakeWorld();
        var b = Formed(simB, worldB, new TileCoord(3, 10), 8,
            u => { if (u.Id == 8) u.Buffs.Add(new Buff("cart", 0, 0, null, MoveCostPercent: 100)); });
        var startB = simB.Now;
        var (spreadB, arrivedB) = March(simB, worldB, b, new TileCoord(26, 10));

        // One cart in eight doubles the column's time; nobody runs ahead of it.
        Assert.True(arrivedB - startB > (arrivedA - startA) * 3 / 2,
            $"with a cart {arrivedB - startB} ticks, without {arrivedA - startA}");
        Assert.InRange(spreadB, 0, 3);
    }

    [Fact]
    public void ANarrowWay_NarrowsTheColumn_AndEveryoneGetsThrough()
    {
        var (sim, world) = MakeWorld(24, 12);
        // Water everywhere but a winding causeway one tile wide.
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 24; x++)
                world.Grid.SetBiome(new TileCoord(x, y), Biome.Water);
        var land = new List<TileCoord>();
        for (var x = 1; x <= 8; x++) land.Add(new TileCoord(x, 5));
        for (var y = 5; y <= 8; y++) land.Add(new TileCoord(8, y));
        for (var x = 8; x <= 20; x++) land.Add(new TileCoord(x, 8));
        foreach (var t in land) world.Grid.SetBiome(t, Biome.Grassland);
        for (var y = 4; y <= 6; y++) world.Grid.SetBiome(new TileCoord(1, y), Biome.Grassland);   // room to form up
        for (var y = 7; y <= 9; y++) world.Grid.SetBiome(new TileCoord(20, y), Biome.Grassland);  // room to close

        var gid = Formed(sim, world, new TileCoord(1, 5), 12);
        March(sim, world, gid, new TileCoord(20, 8));

        var group = world.Groups[gid];
        Assert.Equal(GroupState.Idle, group.State);
        Assert.All(group.Members, id => Assert.True(
            Math.Abs(world.Units[id].Position.X - 20) <= 1, $"unit {id} ended at {world.Units[id].Position}"));
    }

    [Fact]
    public void ABigGroup_ClosesIntoABlockThatSpills()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, new TileCoord(3, 10), 40);

        March(sim, world, gid, new TileCoord(24, 10));

        var group = world.Groups[gid];
        var places = group.Members.Select(id => (world.Units[id].Position, world.Units[id].Subtile)).ToList();
        Assert.Equal(40, places.Distinct().Count());
        Assert.Equal(16, places.Count(p => p.Position == new TileCoord(24, 10)));
        Assert.All(places.GroupBy(p => p.Position), g => Assert.True(g.Count() <= 16));
        Assert.All(places, p => Assert.True(Math.Max(Math.Abs(p.Position.X - 24), Math.Abs(p.Position.Y - 10)) <= 2));
    }

    [Fact]
    public void Retasking_MidMarch_GoesToTheNewPlace()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, new TileCoord(3, 10), 8);
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(26, 10)));
        sim.Run(until: sim.Now + 400);
        Assert.NotNull(world.Groups[gid].MarchPath);

        March(sim, world, gid, new TileCoord(10, 2));

        Assert.Equal(new TileCoord(10, 2), world.Groups[gid].Position);
        Assert.All(world.Groups[gid].Members, id => Assert.Equal(new TileCoord(10, 2), world.Units[id].Position));
    }

    private static Simulation Scenario()
    {
        var (sim, world) = MakeWorld();
        var gid = Formed(sim, world, new TileCoord(3, 10), 20);
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, new TileCoord(25, 4)));
        return sim;
    }

    [Fact]
    public void TwinRun_AndMidMarchRestore_EndTheSame()
    {
        var a = Scenario(); a.Run(until: a.Now + 60_000);
        var b = Scenario(); b.Run(until: b.Now + 60_000);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));

        var mid = Scenario();
        var end = mid.Now + 60_000;
        mid.Run(until: mid.Now + 900);
        Assert.NotNull(mid.World.Groups.Values.Single().MarchPath);
        Assert.NotNull(mid.World.Groups.Values.Single().NextStepTick);
        var restored = Snapshot.Restore(Snapshot.Serialize(mid), seed: 1);
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
        mid.Run(until: end);
        restored.Run(until: end);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(mid));
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
        Assert.Equal(GroupState.Idle, restored.World.Groups.Values.Single().State);
    }
}
