using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// Battlefield salvage (2026-07-13): ground piles ride the wire for tiles
// in CURRENT sight (the M23 cache stance — looting needs a named
// resource), graves reach the AI brains through the projector's
// GraveSource, and SalvageRung sends idle civilians to haul the drop
// home. The dry-target problem solves itself: the host retires a looted
// grave from every view, so the crew retargets off the view alone.
public class SalvageTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public SalvageTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

    private static (Simulation sim, ViewProjector projector, WorldBuild build) MakeMatch(
        int aiPlayers = 1, int mapSeed = 7, int size = 96)
    {
        var opts = new ServerOptions
        {
            MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = aiPlayers,
        };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        return (sim, new ViewProjector(build), build);
    }

    private static Castle CastleOf(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.OfType<Castle>().Single(c => c.OwnerId == ownerId);

    // ---- the wire: piles + graves through the projector ----------------------

    [Fact]
    public void Wire_Piles_VisibleTilesOnly_AndPureRead()
    {
        var (sim, projector, _) = MakeMatch(aiPlayers: 1);
        var near = CastleOf(sim, 0).At;
        var nearPile = new TileCoord(near.X + 2, near.Y);          // inside the castle's vision disc
        var farPile = CastleOf(sim, 1).At is { } c1
            ? new TileCoord(c1.X + 2, c1.Y) : default;             // deep in player 0's fog
        sim.World.GroundResources[nearPile] = new SortedDictionary<Resource, int>
            { [Resource.Stone] = 12, [Resource.Wood] = 3 };
        sim.World.GroundResources[farPile] = new SortedDictionary<Resource, int>
            { [Resource.Ore] = 7 };

        var view = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var pile = Assert.Single(view.Piles);
        Assert.Equal((nearPile.X, nearPile.Y), (pile.X, pile.Y));
        Assert.Equal(12, ThinkContext.AmountOf(pile.Holdings, Resource.Stone));
        Assert.Equal(3, ThinkContext.AmountOf(pile.Holdings, Resource.Wood));

        // Reveal mode sees everything; projection never moves the hash.
        var before = Snapshot.Hash(sim);
        var revealed = projector.Project(sim, sim.Now, playerId: 0, reveal: true);
        Assert.Equal(2, revealed.Piles.Length);
        for (var i = 0; i < 100; i++)
            projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    [Fact]
    public void Wire_Graves_ReachTheProjectedView_WhenTrackerAttached()
    {
        // The seam that puts graves in front of the AI BRAINS: the driver
        // projects its own view, so graves must ride ViewProjector, not
        // just GameHost's HTTP path.
        var (sim, projector, _) = MakeMatch(aiPlayers: 1);
        var tracker = new GraveTracker();

        // An honest death through the real pipeline: a cargo-laden victim
        // dies inside player 0's castle vision → pile + grave. The victim
        // must be IN the tracker's baseline snapshot — the diff only
        // notices units it knew about.
        var c0 = CastleOf(sim, 0).At;
        var victim = new Unit(9999, new TileCoord(c0.X + 2, c0.Y))
        {
            Role = UnitRole.Farmer, OwnerId = 1,
            Cargo = { { Resource.Ore, 10 } },
        };
        sim.World.AddUnit(victim);
        tracker.SnapshotUnits(sim.World);
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, victim);
        tracker.Harvest(sim, 0);
        Assert.Single(tracker.Graves);

        // No tracker attached → no graves (standalone projector, most tests).
        var blind = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        Assert.Empty(blind.Graves);

        projector.GraveSource = tracker;
        var view = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var grave = Assert.Single(view.Graves);
        Assert.Equal((c0.X + 2, c0.Y), (grave.X, grave.Y));

        // The fogged bystander (faction 1, far away) sees nothing.
        var bystander = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        Assert.Empty(bystander.Graves);
    }

    // ---- the rung: crafted views (the brain's whole world IS the DTO) --------

    private static StructDto Struct(int x, int y, StructureKind kind, int owner) =>
        new() { X = x, Y = y, Kind = (int)kind, OwnerId = owner };

    private static UnitDto Civilian(int id, int x, int y, UnitRole role = UnitRole.Farmer) =>
        new() { Id = id, X = x, Y = y, Role = (int)role, OwnerId = 0,
                Activity = (int)Activity.Idle, Age = 20 };

    private static ViewDto CraftView(UnitDto[] units,
        GraveDto[]? graves = null, PileDto[]? piles = null) => new()
    {
        PlayerId = 0, Width = 64, Height = 64,
        Structures = [Struct(5, 5, StructureKind.Castle, 0)],
        Units = units,
        Graves = graves ?? [],
        Piles = piles ?? [],
    };

    private static PileDto Pile(int x, int y, params (Resource R, int Amt)[] rows) =>
        new() { X = x, Y = y, Holdings = rows
            .Select(r => new ResAmtDto { Resource = (int)r.R, Amount = r.Amt }).ToArray() };

    private static GraveDto Grave(int x, int y) => new() { Id = 1, X = x, Y = y, Tick = 100 };

    [Fact]
    public void Salvage_CrewsUp_Marches_Loads_AndHaulsHome()
    {
        var cfg = new AiConfig();
        var mem = new AiMemory();

        // A: idle farmer at the castle, grave 7 tiles out → crewed +
        // marched. The crew is a designation: a rebuilt context (next
        // think) must not treat the farmer as free labor.
        var farmer = Civilian(50, 5, 5);
        var d = new SalvageRung().TryClaim(
            ThinkContext.Build(CraftView([farmer], [Grave(12, 5)]), cfg, mem, now: 200));
        Assert.NotNull(d);
        Assert.Contains(50, mem.SalvageCrew);
        var move = Assert.IsType<MoveIntent>(Assert.Single(d!.Intents));
        Assert.Equal(new TileCoord(12, 5), move.Destination);
        Assert.False(ThinkContext.Build(CraftView([farmer], [Grave(12, 5)]), cfg, mem, now: 260)
            .IsFree(farmer));

        // B: standing on the grave, the pile visible by its own sight —
        // load the RICHEST row (stone 12 beats wood 3).
        var onGrave = Civilian(50, 12, 5);
        d = new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([onGrave], [Grave(12, 5)], [Pile(12, 5, (Resource.Wood, 3), (Resource.Stone, 12))]),
            cfg, mem, now: 260));
        Assert.NotNull(d);
        var load = Assert.IsType<LoadCargoIntent>(Assert.Single(d!.Intents));
        Assert.Equal(Resource.Stone, load.Resource);

        // C: laden → walk it home; at the castle → hand it over.
        var laden = Civilian(50, 12, 5);
        laden.CargoResource = (int)Resource.Stone; laden.CargoAmount = 5;
        d = new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([laden], [Grave(12, 5)]), cfg, mem, now: 320));
        Assert.Equal(new TileCoord(5, 5),
            Assert.IsType<MoveIntent>(Assert.Single(d!.Intents)).Destination);

        var home = Civilian(50, 5, 5);
        home.CargoResource = (int)Resource.Stone; home.CargoAmount = 5;
        d = new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([home], [Grave(12, 5)]), cfg, mem, now: 380));
        Assert.Equal(50, Assert.IsType<UnloadCargoIntent>(Assert.Single(d!.Intents)).UnitId);

        // D: the grave retired from the view (someone finished the pile) —
        // the empty-handed crew is released where it stands.
        var idle = Civilian(50, 12, 5);
        Assert.Null(new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([idle]), cfg, mem, now: 440)));
        Assert.Empty(mem.SalvageCrew);
    }

    [Fact]
    public void Salvage_RespectsLeash_ThreatGate_AndKillSwitch()
    {
        var cfg = new AiConfig();
        var farmer = Civilian(50, 5, 5);

        // Beyond the leash: someone else's battlefield.
        var mem = new AiMemory();
        Assert.Null(new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([farmer], [Grave(5 + cfg.SalvageLeashTiles + 16, 5)]), cfg, mem, now: 200)));
        Assert.Empty(mem.SalvageCrew);

        // A live sighting inside the danger radius: nobody loots
        // mid-battle — the field is read after it cools.
        mem = new AiMemory();
        mem.SightedHostiles[(13, 5)] = (200L, 2);
        Assert.Null(new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([farmer], [Grave(12, 5)]), cfg, mem, now: 200)));
        Assert.Empty(mem.SalvageCrew);

        // SalvageCrewSize 0 is the kill switch.
        mem = new AiMemory();
        Assert.Null(new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView([farmer], [Grave(12, 5)]),
            cfg with { SalvageCrewSize = 0 }, mem, now: 200)));
        Assert.Empty(mem.SalvageCrew);
    }

    [Fact]
    public void Salvage_NeverConscripts_BuildersHaulersOrTheGarrison()
    {
        // The StaffExtractor exclusions, verbatim: sites keep their hands,
        // the road keeps its backbone, the walls keep their guard. Only
        // the farmer walks — and one grave draws one hand, not a column.
        var cfg = new AiConfig();
        var mem = new AiMemory();
        var units = new[]
        {
            Civilian(50, 5, 5, UnitRole.Builder),
            Civilian(51, 5, 5, UnitRole.Hauler),
            Civilian(52, 5, 5, UnitRole.Soldier),
            Civilian(53, 5, 5),
            Civilian(54, 5, 5),
        };
        var d = new SalvageRung().TryClaim(ThinkContext.Build(
            CraftView(units, [Grave(12, 5)]), cfg, mem, now: 200));
        Assert.NotNull(d);
        Assert.Equal(new HashSet<int> { 53 }, mem.SalvageCrew);
    }

    // ---- the lab: the whole loop against the real sim -------------------------

    [Fact]
    public void Salvage_Lab_ColonyHaulsBattlefieldLoot_IntoTheCastle()
    {
        // An ore-laden victim dies (real death pipeline) two tiles from the
        // AI's castle. The colony has no mine — every point of ore in the
        // castle at the end walked home from that grave. Drives the full
        // chain: pile → grave → witnessed on the wire → crew → LoadCargo →
        // haul → Unload → grave retires.
        var (sim, projector, _) = MakeMatch(aiPlayers: 1);
        var tracker = new GraveTracker();
        projector.GraveSource = tracker;

        var c1 = CastleOf(sim, 1).At;
        var graveTile = new TileCoord(c1.X + 2, c1.Y);
        var victim = new Unit(9999, graveTile)
        {
            Role = UnitRole.Farmer, OwnerId = 0,
            Cargo = { { Resource.Ore, 10 } },
        };
        sim.World.AddUnit(victim);
        tracker.SnapshotUnits(sim.World);   // baseline INCLUDES the victim — the diff needs to see it vanish
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, victim);

        var cfg = new AiConfig();
        var driver = new AiPlayerDriver(1, cfg);
        for (long t = 0; t <= 6 * Time.Day; t += cfg.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            driver.Think(sim, projector, t);
            tracker.Harvest(sim, 0);   // GameHost's cadence: harvest after every advance
        }

        var ore = CastleOf(sim, 1).Holdings.GetValueOrDefault(Resource.Ore);
        _output.WriteLine($"day 6: castle ore={ore}, graves={tracker.Graves.Count}");
        Assert.True(ore >= 10,
            $"loot never reached the castle (ore={ore}) — trace tail:\n{driver.Trace.Dump()}");
        Assert.False(sim.World.GroundResources.ContainsKey(graveTile), "the pile should be drained");
        Assert.Empty(tracker.Graves);   // looted → retired
    }
}
