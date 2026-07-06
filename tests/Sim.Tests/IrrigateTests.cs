using Sim.Core.Canals;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;

namespace Sim.Tests;

// M27 — the Irrigate rung (docs/canals.md update, docs/ai-players.md):
// the AI digs a canal from known water into its farm belt so the fields
// rest at the irrigated rate. Pins:
//   1. The plan is REAL: the emitted PlaceCanalIntent passes the server's
//      own validation (water-rooted, 4-connected, diggable) and its end
//      waters the dry claims.
//   2. The full loop: dig -> flood -> the belt reads as watered and the
//      rung retires.
//   3. Gates: dry-claim floor, stone floor, and the 0-disable knob.
//   4. BuilderDemand rises to the canal's 3-builder crew (TrainRung's
//      floor input) and falls back after.
public class IrrigateTests
{
    private static (Simulation sim, ViewProjector projector) MakeMatch(
        int mapSeed = 7, int size = 96)
    {
        var opts = new ServerOptions
        {
            MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = 1,
        };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0x1881);
        return (sim, new ViewProjector(build));
    }

    private static Castle CastleOf(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.OfType<Castle>().Single(c => c.OwnerId == ownerId);

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    private static AiConfig TestCfg => new()
    {
        IrrigatePopulationFloor = 1,
        IrrigateMaxCanalTiles = 12,
        IrrigateMinDryClaimTiles = 4,   // the 8-claim ring caps end coverage at ~5
        IrrigateStoneFloor = 200,
    };

    // A hand-built INLAND farm southeast of the coastal castle: its claim
    // ring (8 tiles around the farm) sits well beyond the water-recovery
    // radius of the NW ocean — the dry belt a canal should reach.
    private static (Extractor Farm, List<TileCoord> Claims) AddInlandFarm(Simulation sim)
    {
        var keep = CastleOf(sim, 1).At;
        var at = new TileCoord(keep.X + 5, keep.Y + 3);
        var farm = new Extractor(StructureKind.Farm, at) { OwnerId = 1 };
        var claims = new List<TileCoord>();
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            claims.Add(new TileCoord(at.X + dx, at.Y + dy));
        }
        claims.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        farm.ClaimTiles.AddRange(claims);
        sim.World.AddStructure(farm);
        return (farm, claims);
    }

    private static ThinkContext Ctx(Simulation sim, ViewProjector projector,
        AiConfig cfg, AiMemory mem) =>
        ThinkContext.Build(projector.Project(sim, sim.Now, playerId: 1, reveal: false),
            cfg, mem, sim.Now);

    [Fact]
    public void Irrigate_DigsARealCanal_ThenTheBeltReadsWatered()
    {
        var (sim, projector) = MakeMatch();
        var cfg = TestCfg;
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Stone, 600);
        castle.Deposit(Resource.Wood, 300);
        castle.Deposit(Resource.Food, 1500);
        var (_, claims) = AddInlandFarm(sim);

        // 1. The rung plans a dig. The server accepting it is the real
        //    pin: water-rooted, 4-connected, diggable land throughout.
        var mem = new AiMemory();
        var d = new IrrigateRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var dig = Assert.IsType<PlaceCanalIntent>(Assert.Single(d!.Intents));
        Assert.InRange(dig.Path.Count, 1, cfg.IrrigateMaxCanalTiles);
        Assert.True(dig.Resolve(sim).IsApplied, "the server rejected the planned canal");

        // The end tile waters at least the threshold of dry claims.
        var end = dig.Path[^1];
        var watered = claims.Count(c =>
            Math.Max(Math.Abs(c.X - end.X), Math.Abs(c.Y - end.Y)) <= cfg.IrrigateWaterRadius);
        Assert.True(watered >= cfg.IrrigateMinDryClaimTiles,
            $"canal end {end.X},{end.Y} waters only {watered} claim tiles");

        // 2. While the dig is in flight the rung crews it, plans nothing new,
        //    and the builder demand rises to the canal's 3-man crew.
        var ctx = Ctx(sim, projector, cfg, mem);
        Assert.Equal(3, ctx.BuilderDemand());
        var next = new IrrigateRung().TryClaim(ctx);
        if (next is not null)   // builders may or may not be marchable this think
            Assert.DoesNotContain(next.Intents, i => i is PlaceCanalIntent);

        // 3. Complete the dig (the standard dance) — the path floods.
        var site = (ConstructionSite)sim.World.Structures[dig.Path[0]];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 0; i < site.RequiredBuilderCount; i++)
        {
            var b = new Unit(600 + i, dig.Path[0]) { Role = UnitRole.Builder, OwnerId = 1 };
            sim.World.AddUnit(b);
            b.TrySetActivity(Activity.Building, dig.Path[0]);
        }
        site.StartOrResume(sim);
        AdvanceTo(sim, sim.Now + site.BuildDurationTicks + 2);
        Assert.All(dig.Path, t => Assert.Equal(Biome.Water, sim.World.Grid.BiomeAt(t)));

        // 4. The belt reads watered: not enough dry claims remain, the rung
        //    retires, and the builder demand falls back to the floor.
        ctx = Ctx(sim, projector, cfg, mem);
        Assert.Null(new IrrigateRung().TryClaim(ctx));
        Assert.Equal(cfg.BuilderFloor, ctx.BuilderDemand());
    }

    [Fact]
    public void Irrigate_Gates_HoldWithoutNeedOrStone()
    {
        var (sim, projector) = MakeMatch();
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Food, 1500);

        // No farm at all → no dry claims → quiet, even with a full treasury.
        castle.Deposit(Resource.Stone, 600);
        Assert.Null(new IrrigateRung().TryClaim(
            Ctx(sim, projector, TestCfg, new AiMemory())));

        // Dry claims exist but the treasury is drained → quiet.
        AddInlandFarm(sim);
        castle.Withdraw(Resource.Stone, castle.AmountOf(Resource.Stone));
        Assert.Null(new IrrigateRung().TryClaim(
            Ctx(sim, projector, TestCfg, new AiMemory())));

        // Disabled by the knob → quiet regardless.
        castle.Deposit(Resource.Stone, 600);
        var off = TestCfg with { IrrigateMaxCanalTiles = 0 };
        Assert.Null(new IrrigateRung().TryClaim(
            Ctx(sim, projector, off, new AiMemory())));
    }
}
