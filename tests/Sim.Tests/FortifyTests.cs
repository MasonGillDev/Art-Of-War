using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;

namespace Sim.Tests;

// M26 — the Fortify rung (docs/walls-and-gates.md, docs/ai-players.md):
// the AI rings its castle in walls once it can afford stone. Pins:
//   1. GATE FIRST — no wall segment is ever placed while zero gates
//      stand or build (own walls block own units; a gateless ring
//      would wall the colony's haulers in).
//   2. Placements land ON the ring (Chebyshev FortifyRadius from the
//      keep), gates at side midpoints.
//   3. STONE BOOTSTRAP — no stone income exists anywhere else in the
//      brain; below the stone floor the rung places a Quarry instead
//      of walls (M22 makes mountains common knowledge, so it always
//      knows where).
//   4. FortifyRadius = 0 disables the rung entirely.
public class FortifyTests
{
    private static (Simulation sim, ViewProjector projector) MakeMatch(
        int mapSeed = 7, int size = 96)
    {
        var opts = new ServerOptions
        {
            MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = 1,
        };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xF027);
        return (sim, new ViewProjector(build));
    }

    private static void RunThinks(Simulation sim, ViewProjector projector,
        AiPlayerDriver driver, long until, long step)
    {
        for (var t = sim.Now; t <= until; t += step)
        {
            sim.Run(until: t);
            driver.Think(sim, projector, t);
        }
        sim.Run(until: until + step);
    }

    private static Castle CastleOf(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.OfType<Castle>().Single(c => c.OwnerId == ownerId);

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    // Bounded advance — a generated world's queue holds lifetime events
    // (aging deaths), so an unbounded Run() fast-forwards to extinction.
    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    // Own fortification placements: standing Walls/Gates plus sites
    // targeting them.
    private static List<(TileCoord Tile, StructureKind Kind)> FortPlacements(
        Simulation sim, int ownerId)
    {
        var result = new List<(TileCoord, StructureKind)>();
        foreach (var s in sim.World.Structures.Values)
        {
            if (s.OwnerId != ownerId) continue;
            var kind = s is ConstructionSite cs ? cs.TargetKind : s.Kind;
            if (kind is StructureKind.Wall or StructureKind.Gate)
                result.Add((s.At, kind));
        }
        return result;
    }

    private static bool HasQuarry(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.Any(s => s.OwnerId == ownerId
            && (s.Kind == StructureKind.Quarry
                || (s is ConstructionSite cs && cs.TargetKind == StructureKind.Quarry)));

    [Fact]
    public void Fortify_GateFirst_ThenWallsOnTheRing()
    {
        var (sim, projector) = MakeMatch();
        var cfg = new AiConfig
        {
            // Fire immediately: a genesis camp is "town" enough for the
            // test, and the treasury below satisfies the surplus gates
            // (fat larder, healthy runway) — this pin is ring GEOMETRY;
            // the economics pin is the balance labs' job. Radius 5, not
            // tighter: the bootstrap farms' claim belts blanket the close
            // ring on this map (a radius-3 ring here is GENUINELY
            // unbuildable — the rung correctly refuses it).
            FortifyPopulationFloor = 1,
            FortifyRadius = 5,
            FortifyStoneFloor = 50,
        };
        // Pre-fund the wall (and the surplus gates) so the quarry
        // bootstrap stays out of this pin.
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Stone, 600);
        castle.Deposit(Resource.Wood, 300);
        castle.Deposit(Resource.Food, 1500);

        var driver = new AiPlayerDriver(1, cfg);

        // Drive until the FIRST fortification placement appears — it must
        // be the gate, alone (pin 1). The window is generous: the bootstrap
        // rungs (farms, barracks, house) outrank Fortify and claim the
        // early thinks; fortification is what quiet thinks buy.
        List<(TileCoord Tile, StructureKind Kind)> forts = new();
        for (var t = sim.Now; t <= 120 * cfg.ThinkPeriodTicks; t += cfg.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            driver.Think(sim, projector, t);
            sim.Run(until: t + 1);   // resolve this think's submissions
            forts = FortPlacements(sim, 1);
            if (forts.Count > 0) break;
        }
        Assert.True(forts.Count > 0,
            "no fortification placed in 120 thinks — trace:\n" + driver.Trace.Dump());
        Assert.All(forts, f => Assert.Equal(StructureKind.Gate, f.Kind));

        // Keep going: walls follow, and everything sits ON the ring.
        RunThinks(sim, projector, driver, until: sim.Now + 60 * cfg.ThinkPeriodTicks,
            step: cfg.ThinkPeriodTicks);
        forts = FortPlacements(sim, 1);
        var keep = CastleOf(sim, 1).At;
        Assert.Contains(forts, f => f.Kind == StructureKind.Wall);
        // Everything — gate included — sits ON the ring (gate slots shift
        // along their side when the midpoint is claimed/flooded, but never
        // off the perimeter).
        Assert.All(forts, f => Assert.Equal(cfg.FortifyRadius,
            Math.Max(Math.Abs(f.Tile.X - keep.X), Math.Abs(f.Tile.Y - keep.Y))));
        Assert.Contains(forts, f => f.Kind == StructureKind.Gate);
    }

    [Fact]
    public void Fortify_StoneShort_BootstrapsAQuarry_NoWallsYet()
    {
        // Seed 3: hills in faction 1's sight from the start. Since M44 a quarry needs a known
        // hill pocket, and fair start placement moved seed 7's castle away from the hills.
        var (sim, projector) = MakeMatch(mapSeed: 3);
        var cfg = new AiConfig
        {
            FortifyPopulationFloor = 1,
            FortifyRadius = 5,
            // Default stone floor (120) — far above the drained treasury.
        };
        var castle = CastleOf(sim, 1);
        castle.Withdraw(Resource.Stone, castle.AmountOf(Resource.Stone));   // stone-broke
        castle.Deposit(Resource.Food, 1500);   // ...but food-rich: the surplus gates pass

        var driver = new AiPlayerDriver(1, cfg);
        RunThinks(sim, projector, driver, until: 60 * cfg.ThinkPeriodTicks,
            step: cfg.ThinkPeriodTicks);

        Assert.True(HasQuarry(sim, 1),
            "no quarry placed while stone-short — trace:\n" + driver.Trace.Dump());
        Assert.Empty(FortPlacements(sim, 1));   // the floor holds: no walls on credit
    }

    // The first WALL segment the rung emits for a fresh colony, resolving
    // its gate placements along the way (gates always precede walls). The
    // memory carries the threat picture under test.
    private static List<TileCoord>? FirstWallSegment(AiMemory mem)
    {
        var (sim, projector) = MakeMatch();
        var cfg = new AiConfig
        {
            FortifyPopulationFloor = 1,
            FortifyRadius = 5,
            FortifyStoneFloor = 50,
        };
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Stone, 600);
        castle.Deposit(Resource.Wood, 300);
        castle.Deposit(Resource.Food, 1500);

        for (var i = 0; i < 8; i++)
        {
            var view = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
            var ctx = ThinkContext.Build(view, cfg, mem, sim.Now);
            var decision = new Sim.Server.Ai.Rungs.FortifyRung().TryClaim(ctx);
            if (decision is null) return null;
            if (decision.Intents.OfType<Sim.Core.Fortifications.PlaceWallIntent>()
                    .FirstOrDefault() is { } wall)
                return wall.Path;
            foreach (var intent in decision.Intents)
                Assert.True(intent.Resolve(sim).IsApplied, $"setup intent rejected: {intent.Describe()}");
        }
        return null;
    }

    [Fact]
    public void Fortify_WallsTheThreatArcFirst()
    {
        var (probe, _) = MakeMatch();
        var keep = CastleOf(probe, 1).At;
        var keepY = keep.Y;

        // A: no threat picture — the ring walks clockwise from the north,
        // so the south arc is never first.
        var calm = FirstWallSegment(new AiMemory());
        Assert.NotNull(calm);
        Assert.True(calm![0].Y < keepY + 5,
            $"calm build should not start on the south arc (started {calm[0].X},{calm[0].Y})");

        // B: a fresh hostile sighting far to the SOUTH pivots the build —
        // the south arc goes up first even though walk order prefers north.
        var threatened = new AiMemory();
        threatened.SightedHostiles[(keep.X, Math.Min(keep.Y + 40, 95))] = (Tick: 0L, Count: 3);
        var southFirst = FirstWallSegment(threatened);
        Assert.NotNull(southFirst);
        Assert.All(southFirst!, t => Assert.Equal(keepY + 5, t.Y));
    }

    [Fact]
    public void Fortify_ClearsRingRubble_ThenReWallsTheBreach()
    {
        var (sim, projector) = MakeMatch();
        var cfg = new AiConfig
        {
            FortifyPopulationFloor = 1,
            FortifyRadius = 5,
            FortifyStoneFloor = 50,
            FortifyMaxOpenSites = 40,   // generous — this pin is the mend loop, not pacing
        };
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Stone, 600);
        castle.Deposit(Resource.Wood, 300);
        castle.Deposit(Resource.Food, 1500);

        // A standing ring segment is razed: the breach leaves rubble ON the ring.
        var breach = new TileCoord(castle.At.X + 5, castle.At.Y + 1);
        sim.World.AddStructure(new Wall(breach) { OwnerId = 1 });
        Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, sim.World.Structures[breach]);
        Assert.IsType<Rubble>(sim.World.Structures[breach]);

        // 1. The FIRST fortify decision is the clearing job — ahead of gates,
        //    walls, and (deliberately) the stone floor.
        var mem = new AiMemory();
        var view = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        var ctx = ThinkContext.Build(view, cfg, mem, sim.Now);
        var d = new Sim.Server.Ai.Rungs.FortifyRung().TryClaim(ctx);
        Assert.NotNull(d);
        var clear = Assert.IsType<Sim.Core.Sieges.ClearRubbleIntent>(Assert.Single(d!.Intents));
        Assert.Equal(breach, clear.Tile);
        Assert.True(clear.Resolve(sim).IsApplied);

        // Complete the clearing job (the standard build dance).
        var site = (ConstructionSite)sim.World.Structures[breach];
        var builder = new Unit(500, breach) { Role = UnitRole.Builder, OwnerId = 1 };
        sim.World.AddUnit(builder);
        builder.TrySetActivity(Activity.Building, breach);
        site.StartOrResume(sim);
        // Bounded run — a generated world's queue holds lifetime events
        // (aging deaths); an unbounded Run() would fast-forward the colony
        // to extinction.
        AdvanceTo(sim, sim.Now
            + StructureCatalog.Spec(StructureKind.Rubble).BuildDurationTicks + 2);
        Assert.False(sim.World.Structures.ContainsKey(breach));   // reclaimed

        // 2. The rung re-walls the reclaimed ground through its ordinary
        //    flow: gates first, then a wall line that covers the breach.
        var rewalled = false;
        var log = new List<string>();
        for (var i = 0; i < 16 && !rewalled; i++)
        {
            view = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
            ctx = ThinkContext.Build(view, cfg, mem, sim.Now);
            d = new Sim.Server.Ai.Rungs.FortifyRung().TryClaim(ctx);
            if (d is null) { log.Add($"{i}: NULL"); break; }
            log.Add($"{i}: {d.Why} => {string.Join("; ", d.Intents.Select(x => x.Describe()))}");
            foreach (var intent in d.Intents)
            {
                if (intent is Sim.Core.Fortifications.PlaceWallIntent w
                    && w.Path.Contains(breach))
                    rewalled = true;
                Assert.True(intent.Resolve(sim).IsApplied, $"rejected: {intent.Describe()}");
            }
        }
        Assert.True(rewalled,
            "the breach tile was never re-walled:\n" + string.Join("\n", log));
    }

    [Fact]
    public void FortifyRadiusZero_DisablesTheRung()
    {
        var (sim, projector) = MakeMatch();
        var cfg = new AiConfig
        {
            FortifyPopulationFloor = 1,
            FortifyRadius = 0,
        };
        var castle = CastleOf(sim, 1);
        castle.Deposit(Resource.Stone, 600);

        var driver = new AiPlayerDriver(1, cfg);
        RunThinks(sim, projector, driver, until: 30 * cfg.ThinkPeriodTicks,
            step: cfg.ThinkPeriodTicks);

        Assert.Empty(FortPlacements(sim, 1));
        Assert.False(HasQuarry(sim, 1));   // the stone bootstrap is Fortify's alone
    }
}
