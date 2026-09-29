using Sim.Core.Canals;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Rivers;
using Sim.Core.Roads;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server;
using Sim.Server.Wire;

namespace Sim.Tests;

// M34 — roads on edges (docs/roads-on-edges.md). A road is the ARC between
// two adjacent tiles, not a tile. Every number here derives from the
// constants it tests; retune RoadConstants and this file does not move.
public class RoadsOnEdgesTests
{
    private static readonly int G = Biomes.MoveCost(Biome.Grassland);
    private static readonly int M = Biomes.MoveCost(Biome.Mountain);
    private static readonly int X = RiverConstants.CrossingCost;

    // The reduction formula from docs/road-cost-reduction.md at max condition.
    private static int Maxed(int biomeCost)
    {
        var reduction = (int)((long)biomeCost * RoadConstants.MAX_REDUCTION_PERCENT
                              * RoadConstants.CONDITION_MAX / (100L * RoadConstants.CONDITION_MAX));
        var cost = biomeCost - reduction;
        return cost < RoadConstants.MIN_COST ? RoadConstants.MIN_COST : cost;
    }

    private static (Simulation sim, GameWorld world) Flat(int w, int h)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        return (new Simulation(world, seed: 1), world);
    }

    private static void Seed(GameWorld world, TileCoord a, TileCoord b, int condition = RoadConstants.CONDITION_MAX) =>
        world.Roads[TileEdge.Between(a, b)] = new RoadState(condition, 0);

    // ---- the key ---------------------------------------------------------

    [Fact]
    public void TileEdge_IsCanonical_AndSymmetric()
    {
        var a = new TileCoord(2, 2);
        Assert.Equal(TileEdge.Between(a, new(3, 2)), TileEdge.Between(new(3, 2), a));
        Assert.Equal(TileEdge.Between(a, new(2, 3)), TileEdge.Between(new(2, 3), a));
        Assert.Equal(TileEdge.Axis.East,  TileEdge.Between(a, new(1, 2)).Direction);
        Assert.Equal(TileEdge.Axis.South, TileEdge.Between(a, new(2, 1)).Direction);
        // The owner is the west / north tile.
        Assert.Equal(new TileCoord(1, 2), TileEdge.Between(a, new(1, 2)).A);
        Assert.Equal(new TileCoord(2, 1), TileEdge.Between(a, new(2, 1)).A);
        Assert.Equal(TileEdge.Between(a, new(3, 2)), TileEdge.FromOwner(a, TileEdge.Axis.East));

        Assert.False(TileEdge.TryBetween(a, a, out _));
        Assert.False(TileEdge.TryBetween(a, new(3, 3), out _));
        Assert.False(TileEdge.TryBetween(a, new(5, 2), out _));
        Assert.Throws<ArgumentException>(() => TileEdge.Between(a, new(3, 3)));

        Assert.Equal(RiverEdge.East, TileEdge.Between(a, new(3, 2)).SideOf(a));
        Assert.Equal(RiverEdge.West, TileEdge.Between(a, new(3, 2)).SideOf(new(3, 2)));
        Assert.Equal(4, TileEdge.Around(a).Distinct().Count());
        Assert.All(TileEdge.Around(a), e => Assert.True(e.A == a || e.B == a));
    }

    // ---- one lane, not four ---------------------------------------------

    [Fact]
    public void Traffic_OnOneArc_ReducesOnlyThatHop()
    {
        var (_, world) = Flat(3, 3);
        var c = new TileCoord(1, 1);
        var e = new TileCoord(2, 1);
        for (var t = 0L; t < 200; t += 10) Road.CreditTraffic(world, c, e, t);

        var reduced = Road.EffectiveCost(world, c, e, 200);
        Assert.True(reduced < G);
        // The same lane read from the other end.
        Assert.Equal(reduced, Road.EffectiveCost(world, e, c, 200));
        // The other three hops out of the same tile are untouched. The tile
        // model fails this: a road "on" (1,1) discounted every hop into it.
        Assert.Equal(G, Road.EffectiveCost(world, c, new(0, 1), 200));
        Assert.Equal(G, Road.EffectiveCost(world, c, new(1, 0), 200));
        Assert.Equal(G, Road.EffectiveCost(world, c, new(1, 2), 200));
        Assert.Equal(G, Road.EffectiveCost(world, new(0, 1), c, 200));
        Assert.Single(world.Roads);
    }

    [Fact]
    public void CreditTraffic_NonAdjacentPair_CreditsNothing()
    {
        var (_, world) = Flat(3, 3);
        Road.CreditTraffic(world, new(1, 1), new(1, 1), 0);
        Road.CreditTraffic(world, new(0, 0), new(2, 2), 0);
        Assert.Empty(world.Roads);
        Assert.Equal(G, Road.EffectiveCost(world, new(1, 1), new(1, 1), 0));
    }

    // ---- terrain is the destination's -----------------------------------

    [Fact]
    public void MaxedArc_UsesDestinationTerrain_EachWay()
    {
        var (_, world) = Flat(3, 1);
        world.Grid.SetBiome(new(2, 0), Biome.Mountain);
        Seed(world, new(1, 0), new(2, 0));
        Assert.Equal(Maxed(M), Road.EffectiveCost(world, new(1, 0), new(2, 0), 0));
        Assert.Equal(Maxed(G), Road.EffectiveCost(world, new(2, 0), new(1, 0), 0));
    }

    // ---- rivers untouched -----------------------------------------------

    [Fact]
    public void RoadAcrossRiver_StillPaysFullCrossing_BankRoadDoesNot()
    {
        // River along the boundary between columns 2 and 3, full height.
        var rivers = new Dictionary<TileCoord, RiverEdge>();
        for (var y = 0; y < 6; y++)
        {
            rivers[new TileCoord(2, y)] = RiverEdge.East;
            rivers[new TileCoord(3, y)] = RiverEdge.West;
        }
        var sim = new Simulation(new GenesisSpec
        {
            Width = 6, Height = 6, Rivers = rivers,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0, CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[] { new UnitSpawn(1, new TileCoord(2, 2), UnitRole.Scout) },
                },
            },
        }, seed: 11);
        var w = sim.World;
        Seed(w, new(2, 2), new(3, 2));   // a worn ford
        Seed(w, new(2, 2), new(2, 3));   // a bank road

        Assert.Equal(Maxed(G) + X, MovementCost.ExecutionCost(w, new(2, 2), new(3, 2), sim.Now));
        Assert.Equal(Maxed(G) + X, MovementCost.ExecutionCost(w, new(3, 2), new(2, 2), sim.Now));
        Assert.Equal(Maxed(G),     MovementCost.ExecutionCost(w, new(2, 2), new(2, 3), sim.Now));
        Assert.Equal(G,            MovementCost.ExecutionCost(w, new(2, 2), new(1, 2), sim.Now));
    }

    // ---- water clears arcs ----------------------------------------------

    private sealed class NoOpEvent : ScheduledEvent { public override void Apply(Simulation sim) { } }

    [Fact]
    public void CanalFlood_RemovesEveryArcTouchingTheTile_AndNoOther()
    {
        var (sim, world) = Flat(12, 12);
        world.Grid.SetBiome(new(0, 5), Biome.Water);   // source
        var p = new TileCoord(1, 5);
        foreach (var arc in TileEdge.Around(p)) world.Roads[arc] = new RoadState(500, 0);
        var beside = TileEdge.Between(new(2, 5), new(3, 5));
        world.Roads[beside] = new RoadState(500, 0);

        var path = new List<TileCoord> { p };
        Assert.True(new PlaceCanalIntent(path) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var site = (ConstructionSite)world.Structures[p];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 1; i <= site.RequiredBuilderCount; i++)
        {
            var u = new Unit(i, p) { Role = UnitRole.Builder };
            world.AddUnit(u);
            u.TrySetActivity(Activity.Building, p);
        }
        site.StartOrResume(sim);
        sim.Run();

        Assert.Equal(Biome.Water, world.Grid.BiomeAt(p));
        foreach (var arc in TileEdge.Around(p)) Assert.False(world.Roads.ContainsKey(arc));
        Assert.True(world.Roads.ContainsKey(beside));
    }

    // ---- purity ---------------------------------------------------------

    [Fact]
    public void Reads_ArePure_100x()
    {
        var (sim, world) = Flat(4, 4);
        Seed(world, new(1, 1), new(2, 1), 300);
        Seed(world, new(2, 1), new(2, 2), 700);
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            Road.EffectiveCost(world, new(1, 1), new(2, 1), 5_000 + i);
            Road.EffectiveCost(world, new(2, 2), new(2, 1), 5_000 + i);
            Road.ConditionAt(world, TileEdge.Between(new(1, 1), new(2, 1)), 50_000 + i);
            MovementCost.PlanCost(world, new(1, 1), new(2, 1), 0, new HashSet<TileCoord>(), 5_000 + i);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // ---- snapshot -------------------------------------------------------

    [Fact]
    public void Snapshot_RoundTripsArcs_AndRejectsV31()
    {
        var (sim, world) = Flat(5, 5);
        Seed(world, new(1, 1), new(2, 1), 300);
        Seed(world, new(2, 1), new(2, 2), 700);
        Seed(world, new(0, 4), new(1, 4), 42);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(3, restored.World.Roads.Count);
        Assert.Equal(700, restored.World.Roads[TileEdge.Between(new(2, 2), new(2, 1))].Condition);

        // Magic is the first 4 bytes; the format version is the next 4.
        var stale = (byte[])bytes.Clone();
        BitConverter.GetBytes(31).CopyTo(stale, 4);
        Assert.Throws<InvalidDataException>(() => Snapshot.Restore(stale, seed: 1));
    }

    // ---- wire -----------------------------------------------------------

    [Fact]
    public void Wire_ShipsArc_WhenEitherEndpointExplored_InBothBuilders()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7 });
        var sim = new Simulation(build.Spec, seed: 9);
        var projector = new ViewProjector(build);
        var world = sim.World;

        // Two land tiles far from anyone's sight, and the arc between them.
        var a = new TileCoord(40, 40);
        var b = new TileCoord(41, 40);
        world.Grid.SetBiome(a, Biome.Grassland);
        world.Grid.SetBiome(b, Biome.Grassland);
        Seed(world, a, b, 400);
        var explored = world.Explored.TryGetValue(0, out var set) ? set : world.Explored[0] = new HashSet<TileCoord>();
        explored.Remove(a); explored.Remove(b);

        RoadDto? Find(RoadDto[] roads) =>
            roads.FirstOrDefault(r => r.X == a.X && r.Y == a.Y && r.Axis == (int)TileEdge.Axis.East);

        Assert.Null(Find(projector.Project(sim, sim.Now, 0, reveal: false).Roads));
        Assert.Null(Find(projector.ProjectV2(sim, sim.Now, 0, reveal: false).Roads));

        explored.Add(b);   // only the far endpoint
        var v1 = Find(projector.Project(sim, sim.Now, 0, reveal: false).Roads);
        var v2 = Find(projector.ProjectV2(sim, sim.Now, 0, reveal: false).Roads);
        Assert.NotNull(v1);
        Assert.NotNull(v2);
        Assert.Equal(400, v1!.Condition);
        Assert.Equal(400, v2!.Condition);
    }

    // ---- determinism ----------------------------------------------------

    [Fact]
    public void GeneratedWorld_WithTraffic_TwinRunHashesEqual()
    {
        Simulation Run()
        {
            var map = MapGenerator.Build(new GenerationConfig { Seed = 42, Width = 64, Height = 64 });
            var sim = new Simulation(new GenesisSpec
            {
                Width = map.Width, Height = map.Height,
                Biomes = MapGenerator.ToBiomeOverrides(map),
                Rivers = MapGenerator.ToRiverOverrides(map),
                FactionStarts = new[]
                {
                    new FactionStartSpec
                    {
                        CastlePosition = map.Start,
                        UnitSpawns = new[]
                        {
                            new UnitSpawn(1, map.Start, UnitRole.Scout),
                            new UnitSpawn(2, map.Start, UnitRole.Scout),
                        },
                    },
                },
            }, seed: 5);
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(map.Width - 3, map.Height - 3)) { PlayerId = 0 });
            sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(3, map.Height - 3)) { PlayerId = 0 });
            sim.Run(until: 3000);
            return sim;
        }
        var x = Run();
        var y = Run();
        Assert.Equal(Snapshot.Hash(x), Snapshot.Hash(y));
        Assert.NotEmpty(x.World.Roads);
        Assert.All(x.World.Roads.Keys, e => Assert.Equal(1, Math.Abs(e.A.X - e.B.X) + Math.Abs(e.A.Y - e.B.Y)));
    }
}
