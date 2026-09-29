using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;

namespace Sim.Tests;

// The haul belt (2026-09-19, the food-wall post-mortem in
// docs/ai-players.md): the AI buys logistics before it buys sprawl.
//   1. CartRung places a REAL Workshop (the server accepts the site), the
//      feed line stocks it, a cart is forged to need, and a Hauler walks
//      in and hitches up — the loop closes on a "cart" buff.
//   2. The hauler floor rises with the extractor belt (TrainRung reads
//      ThinkContext.HaulerDemand), never below the configured floor.
//   3. Gates: no Hauler, no workshop; population floor; famine; and the
//      Carts=false kill switch silences the rung and its feed line.
public class CartTests
{
    private static (Simulation sim, ViewProjector projector) MakeMatch(int mapSeed = 7, int size = 96)
    {
        var opts = new ServerOptions { MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xCA27);
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

    private static AiConfig TestCfg => new() { CartPopulationFloor = 1 };

    private static ThinkContext Ctx(Simulation sim, ViewProjector projector, AiConfig cfg, AiMemory mem) =>
        ThinkContext.Build(projector.Project(sim, sim.Now, playerId: 1, reveal: false), cfg, mem, sim.Now);

    private static TileCoord FreeNear(Simulation sim, TileCoord origin, int minRing = 1)
    {
        for (var r = minRing; r <= 8; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var t = new TileCoord(origin.X + dx, origin.Y + dy);
            if (!sim.World.Grid.InBounds(t)) continue;
            if (sim.World.Grid.BiomeAt(t) is Biome.Water or Biome.None or Biome.Mountain) continue;
            if (sim.World.Structures.ContainsKey(t)) continue;
            if (sim.World.Structures.Values.Any(s => s is Extractor e && e.ClaimTiles.Contains(t))) continue;
            return t;
        }
        throw new InvalidOperationException("no free tile near the keep");
    }

    private static Unit AddHauler(Simulation sim, int id)
    {
        var u = new Unit(id, CastleOf(sim, 1).At) { Role = UnitRole.Hauler, OwnerId = 1 };
        sim.World.AddUnit(u);
        return u;
    }

    private static void CompleteSite(Simulation sim, TileCoord at)
    {
        var site = (ConstructionSite)sim.World.Structures[at];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        for (var i = 0; i < site.RequiredBuilderCount; i++)
        {
            var b = new Unit(800 + i, at) { Role = UnitRole.Builder, OwnerId = 1 };
            sim.World.AddUnit(b);
            b.TrySetActivity(Activity.Building, at);
        }
        site.StartOrResume(sim);
        AdvanceTo(sim, sim.Now + site.BuildDurationTicks + 2);
    }

    [Fact]
    public void Cart_RaisesAWorkshop_ThenCartsTheHaulers()
    {
        var (sim, projector) = MakeMatch();
        var cfg = TestCfg;
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Wood, 600);
        castle.Deposit(Resource.Stone, 300);
        castle.Deposit(Resource.Food, 1500);
        var hauler = AddHauler(sim, 700);
        var mem = new AiMemory();

        var d = new CartRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var place = Assert.IsType<PlaceSiteIntent>(Assert.Single(d!.Intents));
        Assert.Equal(StructureKind.Workshop, place.Kind);
        Assert.True(place.Resolve(sim).IsApplied, "the server rejected the workshop site");
        CompleteSite(sim, place.Tile);
        var workshop = Assert.IsType<Workshop>(sim.World.Structures[place.Tile]);

        // The feed line stocks the workshop's frame and wheels.
        var hauls = LogisticsLayer.Emit(Ctx(sim, projector, cfg, mem)).OfType<HaulIntent>().ToList();
        Assert.Contains(hauls, h => h.DestTile == place.Tile && h.Resource is Resource.Wood or Resource.Stone);
        foreach (var (r, n) in EquipmentCatalog.Spec(Resource.Cart).CraftCost) workshop.Deposit(r, n);

        // Forge to need, then hitch up.
        d = new CartRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var craft = Assert.IsType<CraftEquipmentIntent>(Assert.Single(d!.Intents));
        Assert.Equal(Resource.Cart, craft.Item);
        Assert.True(craft.Resolve(sim).IsApplied);

        d = new CartRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        // One cart, two haulers (genesis ships one): the lowest id hitches
        // up first, and the rung keeps forging for the other.
        var equip = Assert.Single(d!.Intents.OfType<EquipUnitIntent>());
        Assert.Equal(UnitRole.Hauler, sim.World.Units[equip.UnitId].Role);
        Assert.True(equip.Resolve(sim).IsApplied, equip.Describe());
        AdvanceTo(sim, sim.Now + 2 * Sim.Core.Time.Day);
        Assert.Contains(sim.World.Units[equip.UnitId].Buffs, b => b.Kind == "cart");
        Assert.Contains(sim.World.Units[hauler.Id].Buffs.Concat(sim.World.Units[equip.UnitId].Buffs),
            b => b.Kind == "cart");
    }

    [Fact]
    public void HaulerDemand_RisesWithTheBelt()
    {
        var (sim, projector) = MakeMatch();
        // Floors above the hauler's in TrainRung's order are zeroed so the
        // belt is the next need the rung sees.
        var cfg = new AiConfig { ExtractorsPerHauler = 2, BuilderFloor = 0 };
        var mem = new AiMemory();
        var keep = CastleOf(sim, 1).At;

        var before = Ctx(sim, projector, cfg, mem).HaulerDemand();
        Assert.Equal(cfg.HaulerFloor, before);

        // Six farms: three haulers wanted at one per two extractors.
        for (var i = 0; i < 6; i++)
            sim.World.AddStructure(new Extractor(StructureKind.Farm, FreeNear(sim, keep, 2 + i)) { OwnerId = 1 });
        var ctx = Ctx(sim, projector, cfg, mem);
        Assert.Equal(Math.Max(cfg.HaulerFloor, 3), ctx.HaulerDemand());

        // ...and TrainRung acts on it: the next apprentice trains as a Hauler.
        var d = new TrainRung().TryClaim(ctx);
        Assert.NotNull(d);
        Assert.Equal(UnitRole.Hauler, mem.DesignatedTrainee?.Target);
    }

    [Fact]
    public void Cart_Gates_HoldWithoutAHaulerOrTheKillSwitch()
    {
        var (sim, projector) = MakeMatch();
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Wood, 600);
        castle.Deposit(Resource.Stone, 300);
        castle.Deposit(Resource.Food, 1500);
        var mem = new AiMemory();
        // Strip the genesis roster of haulers: nobody to hitch a cart to.
        foreach (var id in sim.World.Units.Values.Where(u => u.OwnerId == 1 && u.Role == UnitRole.Hauler)
                     .Select(u => u.Id).ToList())
            sim.World.Units.Remove(id);
        Assert.Null(new CartRung().TryClaim(Ctx(sim, projector, TestCfg, mem)));

        AddHauler(sim, 700);
        Assert.NotNull(new CartRung().TryClaim(Ctx(sim, projector, TestCfg, mem)));
        Assert.Null(new CartRung().TryClaim(Ctx(sim, projector,
            new AiConfig { CartPopulationFloor = 10_000 }, mem)));

        var off = new AiConfig { Carts = false, CartPopulationFloor = 1 };
        sim.World.AddStructure(new Workshop(FreeNear(sim, castle.At, 2)) { OwnerId = 1 });
        Assert.Null(new CartRung().TryClaim(Ctx(sim, projector, off, mem)));
        var hauls = LogisticsLayer.Emit(Ctx(sim, projector, off, mem)).OfType<HaulIntent>();
        Assert.DoesNotContain(hauls, h =>
            sim.World.Structures.TryGetValue(h.DestTile, out var s) && s is Workshop);
    }
}
