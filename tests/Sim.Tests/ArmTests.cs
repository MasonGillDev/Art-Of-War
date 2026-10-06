using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Xunit.Abstractions;

namespace Sim.Tests;

// Arming (2026-09-19, docs/refining-structures.md's deferred SmeltRung +
// docs/ai-players.md update): the AI raises an armoury and equips its
// garrison. Pins:
//   1. ForgeRung places a REAL Smithy (the server accepts the site), the
//      feed lines stock it, and ArmRung forges shields and walks a soldier
//      in to take one — the loop closes on a "shield" buff.
//   2. With a Mine and a Smelter standing, Forge staffs them and the feed
//      lines run ore into the furnace and iron on to the smithy; the
//      smithy then forges a SWORD before a shield.
//   3. Gates: no army, no forge; population floor; the Arm=false kill
//      switch silences both rungs and the feed lines.
//   4. EnemyIntel prices a faction's soldiers as armed once its Smithy
//      has been seen — the Rival's overmatch gate stops launching into
//      swords it never counted.
public class ArmTests
{
    private readonly ITestOutputHelper _output;
    public ArmTests(ITestOutputHelper output) { _output = output; }

    private static (Simulation sim, ViewProjector projector) MakeMatch(
        int mapSeed = 7, int size = 96)
    {
        var opts = new ServerOptions
        {
            MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = 1,
        };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA2A2);
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

    private static AiConfig TestCfg => new() { ArmPopulationFloor = 1 };

    private static ThinkContext Ctx(Simulation sim, ViewProjector projector,
        AiConfig cfg, AiMemory mem) =>
        ThinkContext.Build(projector.Project(sim, sim.Now, playerId: 1, reveal: false),
            cfg, mem, sim.Now);

    // A free land tile near the keep for a hand-placed structure.
    private static TileCoord FreeNear(Simulation sim, TileCoord origin, int minRing = 1)
    {
        for (var r = minRing; r <= 6; r++)
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

    // A barracks and a bare garrison standing at the keep: the "army to arm".
    private static List<Unit> AddGarrison(Simulation sim, int soldiers)
    {
        var keep = CastleOf(sim, 1).At;
        sim.World.AddStructure(new Barracks(FreeNear(sim, keep)) { OwnerId = 1 });
        var units = new List<Unit>();
        for (var i = 0; i < soldiers; i++)
        {
            var u = new Unit(700 + i, keep) { Role = UnitRole.Soldier, OwnerId = 1 };
            sim.World.AddUnit(u);
            units.Add(u);
        }
        return units;
    }

    private static void Stock(Castle castle)
    {
        castle.Deposit(Resource.Wood, 600);
        castle.Deposit(Resource.Stone, 300);
        castle.Deposit(Resource.Food, 1500);
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

    // M44 — no known vein: Forge surveys until a Miner finds one, then puts
    // the Mine on it (docs/stone-and-ore-land.md). Every order the brain
    // issues goes through the real intents and the server accepts it.
    [Fact]
    public void Forge_SurveysForAVein_ThenPlacesTheMineOnIt()
    {
        var (sim, projector) = MakeMatch();
        var cfg = TestCfg;
        var castle = CastleOf(sim, 1);
        Stock(castle);
        AddGarrison(sim, soldiers: 2);
        sim.World.AddStructure(new Smithy(FreeNear(sim, castle.At, 2)) { OwnerId = 1 });
        var mem = new AiMemory();

        PlaceSiteIntent? mineSite = null;
        for (var sweep = 0; sweep < 12 && mineSite is null; sweep++)
        {
            var d = new ForgeRung().TryClaim(Ctx(sim, projector, cfg, mem));
            Assert.NotNull(d);
            switch (Assert.Single(d!.Intents))
            {
                case Sim.Core.Mining.SurveyIntent survey:
                    Assert.Equal(Biome.Mountain, sim.World.Grid.BiomeAt(survey.Target));
                    sim.SubmitIntent(sim.Now, survey);
                    // Walk there and dig: run until the Miner reports.
                    for (var guard = 0; guard < 60 && (sim.World.Units[survey.UnitId].Survey is not null
                            || sim.ResolvedLog.Count == 0); guard++)
                        AdvanceTo(sim, sim.Now + Sim.Core.Time.Day);
                    Assert.Null(sim.World.Units[survey.UnitId].Survey);
                    break;
                case PlaceSiteIntent place:
                    mineSite = place;
                    break;
                default:
                    Assert.Fail($"unexpected forge order {d.Intents[0].Describe()}");
                    break;
            }
        }

        Assert.NotNull(mineSite);
        Assert.Equal(StructureKind.CopperMine, mineSite!.Kind);
        Assert.True(Sim.Core.Mining.Veins.Knows(sim.World, 1, mineSite.Tile));
        Assert.True(mineSite.Resolve(sim).IsApplied, "the server rejected the mine site");
    }

    [Fact]
    public void Forge_RaisesASmithy_ThenArmsTheGarrisonWithShields()
    {
        var (sim, projector) = MakeMatch();
        var cfg = TestCfg;
        var castle = CastleOf(sim, 1);
        Stock(castle);
        var garrison = AddGarrison(sim, soldiers: 2);
        var mem = new AiMemory();

        // 1. The site is real: the server accepts it.
        var d = new ForgeRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var place = Assert.IsType<PlaceSiteIntent>(Assert.Single(d!.Intents));
        Assert.Equal(StructureKind.Smithy, place.Kind);
        Assert.True(place.Resolve(sim).IsApplied, "the server rejected the smithy site");
        CompleteSite(sim, place.Tile);
        var smithy = Assert.IsType<Smithy>(sim.World.Structures[place.Tile]);

        // 2. Feed lines: logistics sends the smithy its working stock.
        // M44 — the chain's next link is ore, and ore needs a known vein: a
        // fresh colony knows none, so Forge sends one of its Miners to survey
        // a mountain (common knowledge), and the server takes the order.
        var ctx = Ctx(sim, projector, cfg, mem);
        var next = new ForgeRung().TryClaim(ctx);
        if (next is not null)
        {
            var survey = Assert.IsType<Sim.Core.Mining.SurveyIntent>(Assert.Single(next.Intents));
            Assert.Equal(Biome.Mountain, sim.World.Grid.BiomeAt(survey.Target));
            Assert.Equal(UnitRole.Miner, sim.World.Units[survey.UnitId].Role);
            Assert.True(survey.Resolve(sim).IsApplied, "the server rejected the survey");
        }
        var hauls = LogisticsLayer.Emit(ctx).OfType<HaulIntent>().ToList();
        Assert.Contains(hauls, h => h.DestTile == place.Tile
            && h.Resource is Resource.Wood or Resource.Stone);
        smithy.Deposit(Resource.Wood, cfg.ArmSmithyStockTarget);
        smithy.Deposit(Resource.Stone, cfg.ArmSmithyStockTarget);

        // 3. Forge a shield (no iron anywhere: no sword yet), then hand it out.
        d = new ArmRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var craft = Assert.IsType<CraftEquipmentIntent>(Assert.Single(d!.Intents));
        Assert.Equal(Resource.Shield, craft.Item);
        Assert.True(craft.Resolve(sim).IsApplied);
        Assert.Equal(1, smithy.AmountOf(Resource.Shield));

        d = new ArmRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var equip = Assert.Single(d!.Intents.OfType<EquipUnitIntent>());
        Assert.Equal(Resource.Shield, equip.Item);
        Assert.Contains(equip.UnitId, garrison.Select(u => u.Id));
        // Need still outruns stock (one shield, two bare soldiers): forge on.
        Assert.Single(d.Intents.OfType<CraftEquipmentIntent>());
        foreach (var i in d.Intents) Assert.True(i.Resolve(sim).IsApplied, i.Describe());

        // The goal walks him there and arms him on arrival.
        AdvanceTo(sim, sim.Now + 2 * Sim.Core.Time.Day);
        var armed = sim.World.Units[equip.UnitId];
        Assert.Contains(armed.Buffs, b => b.Kind == "shield");
    }

    [Fact]
    public void Forge_StaffsTheOreChain_AndTheFeedLinesRunIronToTheSword()
    {
        var (sim, projector) = MakeMatch();
        var cfg = TestCfg;
        var castle = CastleOf(sim, 1);
        Stock(castle);
        castle.Deposit(Resource.CopperOre, 50);
        AddGarrison(sim, soldiers: 2);
        var keep = castle.At;
        var smithy = new Smithy(FreeNear(sim, keep, 2)) { OwnerId = 1 };
        sim.World.AddStructure(smithy);
        var mine = new Extractor(StructureKind.CopperMine, FreeNear(sim, keep, 3)) { OwnerId = 1 };
        sim.World.AddStructure(mine);
        var furnace = new Extractor(StructureKind.Smelter, FreeNear(sim, keep, 4)) { OwnerId = 1 };
        sim.World.AddStructure(furnace);
        var mem = new AiMemory();

        // 1. Everything stands: Forge's only job left is hands on the picks.
        var d = new ForgeRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        Assert.Contains("mine", d!.Why);
        Assert.All(d.Intents, i => Assert.True(i is AssignWorkersIntent or MoveIntent, i.Describe()));

        // 2. Ore in, iron out. The furnace reads empty of ore: the castle
        //    feeds it; its iron buffer is over the haul threshold: it goes
        //    to the smithy, not home.
        furnace.AddOutput(Resource.Bronze, cfg.HaulBufferThreshold);
        furnace.DepositInput(Resource.Wood, cfg.ArmFeedFloor);   // fuelled, but no ore: ore is the short line
        var hauls = LogisticsLayer.Emit(Ctx(sim, projector, cfg, mem)).OfType<HaulIntent>().ToList();
        var dump = string.Join("; ", hauls.Select(h => h.Describe()));
        Assert.True(hauls.Any(h => h.SourceTile == castle.At && h.DestTile == furnace.At
            && h.Resource == Resource.CopperOre), $"no ore feed — hauls: {dump}");
        Assert.True(hauls.Any(h => h.SourceTile == furnace.At && h.DestTile == smithy.At
            && h.Resource == Resource.Bronze), $"no iron line — hauls: {dump}");

        // 3. Iron and hafts at the smithy: the sword outranks the shield.
        var sword = EquipmentCatalog.Spec(Resource.BronzeSword);
        foreach (var (r, n) in sword.CraftCost) smithy.Deposit(r, n);
        smithy.Deposit(Resource.Stone, 10);
        d = new ArmRung().TryClaim(Ctx(sim, projector, cfg, mem));
        Assert.NotNull(d);
        var craft = Assert.IsType<CraftEquipmentIntent>(Assert.Single(d!.Intents));
        Assert.Equal(Resource.BronzeSword, craft.Item);
        Assert.True(craft.Resolve(sim).IsApplied);
        Assert.Equal(1, smithy.AmountOf(Resource.BronzeSword));
    }

    [Fact]
    public void Arm_Gates_HoldWithoutAnArmyOrTheKillSwitch()
    {
        var (sim, projector) = MakeMatch();
        var castle = CastleOf(sim, 1);
        Stock(castle);
        var mem = new AiMemory();

        // No barracks, no soldiers: nothing to arm, however rich the keep.
        Assert.Null(new ForgeRung().TryClaim(Ctx(sim, projector, TestCfg, mem)));

        AddGarrison(sim, soldiers: 1);
        Assert.NotNull(new ForgeRung().TryClaim(Ctx(sim, projector, TestCfg, mem)));

        // Population floor: a camp doesn't run a smithy.
        var camp = new AiConfig { ArmPopulationFloor = 10_000 };
        Assert.Null(new ForgeRung().TryClaim(Ctx(sim, projector, camp, mem)));

        // The kill switch silences the rungs AND the feed lines.
        var off = new AiConfig { Arm = false, ArmPopulationFloor = 1 };
        sim.World.AddStructure(new Smithy(FreeNear(sim, castle.At, 2)) { OwnerId = 1 });
        Assert.Null(new ForgeRung().TryClaim(Ctx(sim, projector, off, mem)));
        Assert.Null(new ArmRung().TryClaim(Ctx(sim, projector, off, mem)));
        var hauls = LogisticsLayer.Emit(Ctx(sim, projector, off, mem)).OfType<HaulIntent>();
        Assert.DoesNotContain(hauls, h =>
            sim.World.Structures.TryGetValue(h.DestTile, out var s) && s is Smithy);
    }

    [Fact]
    public void EnemyIntel_PricesSoldiersAsArmed_OnceTheirSmithyIsSeen()
    {
        var (sim, projector) = MakeMatch();
        var keep = CastleOf(sim, 1).At;
        // A foreign soldier in plain sight of the keep.
        var foe = new Unit(900, FreeNear(sim, keep)) { Role = UnitRole.Soldier, OwnerId = 0 };
        sim.World.AddUnit(foe);
        var cfg = TestCfg;
        var mem = new AiMemory();

        var bare = Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower;
        var ctx = Ctx(sim, projector, cfg, mem);
        EnemyIntel.Perceive(ctx);
        Assert.Equal(bare, EnemyIntel.EstimateVisiblePower(ctx, 0, keep, 3));

        // Their smithy comes into view: every soldier of theirs is now
        // priced with the best power item the catalog sells his role.
        sim.World.AddStructure(new Smithy(FreeNear(sim, keep, 2)) { OwnerId = 0 });
        ctx = Ctx(sim, projector, cfg, mem);
        EnemyIntel.Perceive(ctx);
        var sword = EquipmentCatalog.Spec(Resource.BronzeSword).PowerModifier;
        Assert.Equal(bare + sword, EnemyIntel.EstimateVisiblePower(ctx, 0, keep, 3));

        // The memory keeps the tell once the smithy leaves sight (the
        // structure is remembered, like any enemy asset).
        Assert.True(EnemyIntel.HasKnownSmithy(ctx, 0));
    }

    // THE LAB REPORT — an A/B: two default Homesteaders, Arm on vs off,
    // same seed, LabDays of hourly thinks. Does the armoury come up
    // ORGANICALLY (smithy, shields on the garrison, then the ore chain and
    // swords), and does the colony carry it? The counts print as the
    // report. Pins: with arming on at least one faction raises a smithy,
    // and arming never puts a faction in famine that the baseline kept
    // fed (a famine the baseline ALSO ends in is the map's, not ours).
    private const long LabDays = 200;

    private sealed record LabRow(int Pop, int Smithy, int Mine, int Smelter,
        int Soldiers, int Shields, int Swords, int Haulers, int Carts, bool Famine, string Trace);

    private Dictionary<int, LabRow> RunLab(bool arm)
    {
        // LAB_MAPSEED overrides the continent for ad-hoc seed sweeps (the
        // AiPlayerTests convention); unset, the seed is fixed for CI.
        var mapSeed = 7;
        if (int.TryParse(Environment.GetEnvironmentVariable("LAB_MAPSEED"), out var envSeed) && envSeed != 0)
            mapSeed = envSeed;
        var opts = new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = mapSeed, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        var projector = new ViewProjector(build);
        var cfg = new AiConfig { Arm = arm, TraceCapacity = 4096 };
        var drivers = new[] { new AiPlayerDriver(0, cfg), new AiPlayerDriver(1, cfg) };
        for (long t = sim.Now; t <= LabDays * Sim.Core.Time.Day; t += cfg.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
        }
        sim.Run(until: LabDays * Sim.Core.Time.Day + cfg.ThinkPeriodTicks);

        var rows = new Dictionary<int, LabRow>();
        foreach (var id in new[] { 0, 1 })
        {
            int Kind(StructureKind k) => sim.World.Structures.Values.Count(s => s.OwnerId == id && s.Kind == k);
            var soldiers = sim.World.Units.Values.Where(u => u.OwnerId == id && u.Role == UnitRole.Soldier).ToList();
            var haulers = sim.World.Units.Values.Where(u => u.OwnerId == id && u.Role == UnitRole.Hauler).ToList();
            var castle = CastleOf(sim, id);
            rows[id] = new LabRow(sim.World.Players[id].PopulationCount,
                Kind(StructureKind.Smithy), Kind(StructureKind.CopperMine), Kind(StructureKind.Smelter),
                soldiers.Count,
                soldiers.Count(u => u.Buffs.Any(b => b.Kind == "shield")),
                soldiers.Count(u => u.Buffs.Any(b => b.Kind == "bronze-sword")),
                haulers.Count, haulers.Count(u => u.Buffs.Any(b => b.Kind == "cart")),
                castle.FoodDebt > 0 || castle.FamineStartTick is not null,
                drivers.Single(d => d.PlayerId == id).Trace.Dump());
            // LAB_TRACE_DIR dumps each faction's full decision trace for
            // post-mortems (the lab.ps1 convention: env-driven, unset in CI).
            if (Environment.GetEnvironmentVariable("LAB_TRACE_DIR") is { Length: > 0 } dir)
                File.WriteAllText(Path.Combine(dir, $"arm-{arm}-faction{id}.txt"), rows[id].Trace);
            _output.WriteLine($"arm={arm} faction {id}: pop={rows[id].Pop} smithy={rows[id].Smithy} " +
                $"mine={rows[id].Mine} smelter={rows[id].Smelter} soldiers={rows[id].Soldiers} " +
                $"shields={rows[id].Shields} swords={rows[id].Swords} haulers={rows[id].Haulers} " +
                $"carts={rows[id].Carts} famine={rows[id].Famine}");
        }
        return rows;
    }

    [Fact]
    public void Arm_TheGarrisonArmsItself_LabReport()
    {
        var armed = RunLab(arm: true);
        var bare = RunLab(arm: false);
        Assert.True(armed.Values.Sum(r => r.Smithy) >= 1, "no faction raised a smithy in the lab");
        Assert.All(bare.Values, r => Assert.Equal(0, r.Smithy + r.Mine + r.Smelter));
        foreach (var id in new[] { 0, 1 })
            Assert.True(!armed[id].Famine || bare[id].Famine,
                $"arming put faction {id} in a famine the baseline avoided — trace tail:\n" +
                armed[id].Trace);
    }
}
