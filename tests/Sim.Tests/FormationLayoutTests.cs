using Sim.Core.Battlefields;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M46 Phase B — FormationLayout (docs/m46-groups-spec.md, "Formation layout"): the
// anchor tile fills first, the block spills onto connected neighbours, nothing lands
// on water or on someone else's subtile, a member with no place is reported (null),
// and it is a pure read.
public class FormationLayoutTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 30, int h = 30)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return (new Simulation(world, seed: 1), world);
    }

    // `count` members standing in a row away from the anchor (they are walking to it).
    private static List<Unit> Members(GameWorld world, int count, int firstId = 1)
    {
        var list = new List<Unit>();
        for (var i = 0; i < count; i++)
            list.Add(world.AddUnit(new Unit(firstId + i, new TileCoord(i % 25, 28)) { Role = UnitRole.Soldier }));
        return list;
    }

    private static List<(Unit Member, WorldSubtile? Place)> Lay(GameWorld world, TileCoord anchor, List<Unit> members) =>
        FormationLayout.Places(world, 0, anchor, FormationLayout.FillOrder(members), visible: null);

    [Fact]
    public void Sixteen_FillTheAnchorTile()
    {
        var (_, world) = MakeWorld();
        var anchor = new TileCoord(10, 10);
        var places = Lay(world, anchor, Members(world, 16));

        Assert.All(places, p => Assert.Equal(anchor, p.Place!.Value.Tile));
        Assert.Equal(16, places.Select(p => p.Place).Distinct().Count());
    }

    [Fact]
    public void Eighty_SpillOntoNeighbours_AnchorFirst_NeverOverTheCap()
    {
        var (_, world) = MakeWorld();
        var anchor = new TileCoord(10, 10);
        var places = Lay(world, anchor, Members(world, 80));

        Assert.All(places, p => Assert.NotNull(p.Place));
        Assert.Equal(80, places.Select(p => p.Place).Distinct().Count());
        // The first sixteen in fill order take the anchor tile.
        Assert.All(places.Take(16), p => Assert.Equal(anchor, p.Place!.Value.Tile));
        Assert.All(places.Skip(16), p => Assert.NotEqual(anchor, p.Place!.Value.Tile));
        // No tile holds more than a tile's worth, and the block stays close.
        Assert.All(places.GroupBy(p => p.Place!.Value.Tile), g => Assert.True(g.Count() <= Subtile.Count));
        Assert.All(places, p =>
            Assert.True(Math.Max(Math.Abs(p.Place!.Value.Tile.X - anchor.X), Math.Abs(p.Place!.Value.Tile.Y - anchor.Y)) <= 2));
    }

    [Fact]
    public void SameInputs_SamePlaces()
    {
        var (_, world) = MakeWorld();
        var members = Members(world, 40);
        var a = Lay(world, new TileCoord(10, 10), members).Select(p => (p.Member.Id, p.Place)).ToList();
        var b = Lay(world, new TileCoord(10, 10), members).Select(p => (p.Member.Id, p.Place)).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Water_BoundsTheBlock()
    {
        var (_, world) = MakeWorld();
        for (var y = 0; y < 30; y++) world.Grid.SetBiome(new TileCoord(12, y), Biome.Water);
        var anchor = new TileCoord(11, 10);

        var places = Lay(world, anchor, Members(world, 60));

        Assert.All(places, p => Assert.NotNull(p.Place));
        Assert.All(places, p => Assert.True(p.Place!.Value.Tile.X < 12, $"placed at {p.Place} across the water"));
    }

    [Fact]
    public void NoRoomInReach_IsReported_NotDropped()
    {
        var (_, world) = MakeWorld();
        // A 2 × 2 island: 64 places.
        for (var y = 0; y < 30; y++)
            for (var x = 0; x < 30; x++)
                if (x is < 10 or > 11 || y is < 10 or > 11)
                    world.Grid.SetBiome(new TileCoord(x, y), Biome.Water);
        var members = new List<Unit>();
        for (var i = 1; i <= 70; i++)
            members.Add(world.AddUnit(new Unit(i, new TileCoord(10 + i % 2, 10)) { Role = UnitRole.Soldier }));

        var places = Lay(world, new TileCoord(10, 10), members);

        Assert.Equal(70, places.Count);
        Assert.Equal(64, places.Count(p => p.Place is not null));
        Assert.Equal(6, places.Count(p => p.Place is null));
        // The ones left over are the last in fill order.
        Assert.All(places.Skip(64), p => Assert.Null(p.Place));
    }

    [Fact]
    public void SomeoneElsesSubtile_IsSkipped_AMembersOwnIsNot()
    {
        var (_, world) = MakeWorld();
        var anchor = new TileCoord(10, 10);
        // A friend outside the group stands on the anchor tile's centre-most subtile.
        var first = Lay(world, anchor, Members(world, 1, firstId: 100))[0].Place!.Value;
        world.Units.Remove(100);
        var outsider = world.AddUnit(new Unit(50, anchor) { Role = UnitRole.Builder });
        outsider.Subtile = first.Sub;

        var places = Lay(world, anchor, Members(world, 16));

        Assert.DoesNotContain(places, p => p.Place == first);
        Assert.Equal(15, places.Count(p => p.Place!.Value.Tile == anchor));

        // A member already standing there keeps the place free for the group.
        var (_, world2) = MakeWorld();
        var members = Members(world2, 16);
        members[0].Position = anchor;
        members[0].Subtile = first.Sub;
        Assert.Equal(16, Lay(world2, anchor, members).Count(p => p.Place!.Value.Tile == anchor));
    }

    [Fact]
    public void AnEnemyStandingThere_DoesNotTakeThePlace()
    {
        var (_, world) = MakeWorld();
        world.Diplomacy.SetState(Sim.Core.Diplomacy.FactionPair.Of(0, 1), Sim.Core.Diplomacy.RelationshipState.Enemy);
        var anchor = new TileCoord(10, 10);
        var enemy = world.AddUnit(new Unit(50, anchor) { Role = UnitRole.Soldier, OwnerId = 1 });

        var places = Lay(world, anchor, Members(world, 16));

        Assert.Equal(16, places.Count(p => p.Place!.Value.Tile == anchor));
    }

    [Fact]
    public void IsAPureRead()
    {
        var (sim, world) = MakeWorld();
        var members = Members(world, 50);
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++) Lay(world, new TileCoord(10, 10), members);
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
