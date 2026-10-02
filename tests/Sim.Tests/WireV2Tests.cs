using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Vision;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Wire;

namespace Sim.Tests;

// The v2 wire (Sim.Server/Wire/WireV2.cs) — the contract the production client
// speaks. v1 re-sends every known tile's {x,y,biome,elevation} on every poll; v2
// splits that into a static genesis payload fetched once plus a per-tick view
// carrying only what a tick can change (fog as RLE runs + biome drift overrides).
//
// The load-bearing property is EQUIVALENCE: genesis + fog runs + overrides must
// reconstruct exactly the tile picture v1 spells out longhand. If that ever breaks,
// the production client renders a world the sim did not describe.
public class WireV2Tests
{
    private static (Simulation sim, ViewProjector projector, WorldBuild build) MakeWorld(int size = 64)
    {
        var opts = new ServerOptions { MapWidth = size, MapHeight = size, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        return (sim, new ViewProjector(build), build);
    }

    // Expand the parallel run arrays back into a per-tile state grid, the way the
    // client's KnownWorld does.
    private static int[] ExpandFog(ViewV2Dto v, int w, int h)
    {
        Assert.Equal(v.FogRunState.Length, v.FogRunLength.Length);
        var fog = new int[w * h];
        var i = 0;
        for (var r = 0; r < v.FogRunState.Length; r++)
            for (var e = i + v.FogRunLength[r]; i < e; i++)
                fog[i] = v.FogRunState[r];
        Assert.Equal(w * h, i);   // runs must cover the grid exactly, no more, no less
        return fog;
    }

    // Genesis biome grid overlaid with the view's drift overrides = what the player
    // believes each tile is. -1 where the player knows nothing.
    private static int[] BelievedBiome(WorldDto world, ViewV2Dto v, int[] fog)
    {
        var biome = new int[world.Biome.Length];
        for (var t = 0; t < biome.Length; t++)
            biome[t] = fog[t] == FogState.Unknown ? -1 : world.Biome[t];
        foreach (var o in v.BiomeOverrides)
            biome[o.Y * world.Width + o.X] = o.Biome;
        return biome;
    }

    [Fact]
    public void World_MatchesTheGeneratedMap()
    {
        var (_, projector, build) = MakeWorld();
        var w = projector.BuildWorldDto();

        Assert.Equal(2, w.WireVersion);
        Assert.Equal(build.Map.Width, w.Width);
        Assert.Equal(build.Map.Height, w.Height);
        Assert.Equal(build.Map.Seed, w.MapSeed);
        Assert.Equal(Sim.Core.Time.Day, w.TicksPerDay);
        Assert.Equal(w.Width * w.Height, w.Elevation.Length);
        Assert.Equal(w.Width * w.Height, w.Biome.Length);

        // Row-major layout is the contract the client indexes with (y * width + x).
        for (var y = 0; y < w.Height; y++)
            for (var x = 0; x < w.Width; x++)
            {
                Assert.Equal(build.Elevation[x, y], w.Elevation[y * w.Width + x]);
                Assert.Equal((int)build.Map.Grid[x, y], w.Biome[y * w.Width + x]);
            }
    }

    [Fact]
    public void FogRunsAndOverrides_ReconstructExactlyWhatV1Describes()
    {
        var (sim, projector, _) = MakeWorld();
        sim.Run(Sim.Core.Time.Day);   // let the world move: units walk, vision shifts

        var now = sim.Now;
        var v1 = projector.Project(sim, now, playerId: 0, reveal: false);
        var v2 = projector.ProjectV2(sim, now, playerId: 0, reveal: false);

        var world = projector.BuildWorldDto();
        var fog = ExpandFog(v2, world.Width, world.Height);
        var biome = BelievedBiome(world, v2, fog);

        var v1Live = v1.Visible.ToDictionary(t => (t.X, t.Y), t => (t.Biome, t.Elevation));
        var v1Rem = v1.Remembered.ToDictionary(t => (t.X, t.Y), t => (t.Biome, t.Elevation));

        // The player must know something, or this test proves nothing.
        Assert.NotEmpty(v1Live);

        for (var y = 0; y < world.Height; y++)
            for (var x = 0; x < world.Width; x++)
            {
                var i = y * world.Width + x;
                switch (fog[i])
                {
                    case FogState.Live:
                        Assert.True(v1Live.ContainsKey((x, y)), $"({x},{y}) v2 Live, v1 does not see it");
                        Assert.Equal(v1Live[(x, y)].Biome, biome[i]);
                        Assert.Equal(v1Live[(x, y)].Elevation, world.Elevation[i]);
                        break;
                    case FogState.Remembered:
                        Assert.True(v1Rem.ContainsKey((x, y)), $"({x},{y}) v2 Remembered, v1 does not");
                        Assert.Equal(v1Rem[(x, y)].Biome, biome[i]);
                        Assert.Equal(v1Rem[(x, y)].Elevation, world.Elevation[i]);
                        break;
                    default:
                        Assert.False(v1Live.ContainsKey((x, y)), $"({x},{y}) v2 Unknown but v1 visible");
                        Assert.False(v1Rem.ContainsKey((x, y)), $"({x},{y}) v2 Unknown but v1 remembered");
                        break;
                }
            }

        // ...and nothing v1 knows about is missing from v2.
        foreach (var (x, y) in v1Live.Keys)
            Assert.Equal(FogState.Live, fog[y * world.Width + x]);
        foreach (var (x, y) in v1Rem.Keys)
            Assert.Equal(FogState.Remembered, fog[y * world.Width + x]);
    }

    [Fact]
    public void Reveal_IsOneLiveRunCoveringTheWholeMap()
    {
        var (sim, projector, _) = MakeWorld();
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);

        Assert.Single(v2.FogRunState);
        Assert.Equal(FogState.Live, v2.FogRunState[0]);
        Assert.Equal(64 * 64, v2.FogRunLength[0]);
    }

    [Fact]
    public void BiomeOverride_EmittedWhenABelievedTileDriftsFromGenesis()
    {
        var (sim, projector, build) = MakeWorld();
        var world = sim.World;
        var cfg = world.BiomeDegradationConfig;
        var now = sim.Now;

        // Find a VISIBLE tile that sits on the degradation ladder — only ladder
        // biomes can drift, so a Water/Mountain tile would prove nothing.
        var visible = View.BuildPlayerView(world, 0, now).Visible;
        TileCoord? target = null;
        foreach (var t in visible)
        {
            var genesis = build.Map.Grid[t.X, t.Y];
            if (genesis is Biome.Forest or Biome.Grassland) { target = t; break; }
        }
        Assert.True(target.HasValue, "no visible ladder tile to degrade — fixture assumption broke");
        var tile = target.Value;
        var genesisBiome = (int)build.Map.Grid[tile.X, tile.Y];

        // Baseline: no drift anywhere yet, so no overrides at all.
        Assert.Empty(projector.ProjectV2(sim, now, 0, reveal: false).BiomeOverrides);

        // Drive the tile's fertility far below the desert threshold. Config-derived,
        // not a magic number: the fertility space has been rescaled before and will be
        // again. LastUpdateTick = now so no catch-up drift is applied on read.
        world.Fertility[tile] = new Fertility(
            deviation: cfg.DesertThreshold - cfg.ForestBaseline - cfg.GrasslandBaseline,
            lastUpdateTick: now);

        var drifted = (int)BiomeDegradation.BiomeAt(world, tile, now, cfg);
        Assert.NotEqual(genesisBiome, drifted);   // the fixture actually moved it

        var v2 = projector.ProjectV2(sim, now, 0, reveal: false);
        var o = Assert.Single(v2.BiomeOverrides);
        Assert.Equal(tile.X, o.X);
        Assert.Equal(tile.Y, o.Y);
        Assert.Equal(drifted, o.Biome);

        // Genesis is untouched — drift lives on the view, never on the static payload.
        Assert.Equal(genesisBiome, projector.BuildWorldDto().Biome[tile.Y * world.Grid.Width + tile.X]);
    }

    [Fact]
    public void UnitHealth_IsOwnOnly()
    {
        var (sim, projector, _) = MakeWorld();
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);

        Assert.NotEmpty(v2.Units);
        foreach (var u in v2.Units)
        {
            if (u.OwnerId == 0) Assert.True(u.Health > 0, $"own unit {u.Id} has no health on the wire");
            else Assert.Equal(-1, u.Health);   // enemy health is private, same rule as Power
        }
    }

    // C2 — group membership, the last of C1's surfacing debt. Without this the client
    // can only select N loose units where the player commanded one army.
    [Fact]
    public void GroupId_IsOnTheWireForOwnUnitsAndPrivateForEveryoneElse()
    {
        // Big enough that the AI faction actually gets a start — at 64x64 the
        // generator skips it, and then the privacy half of this test is vacuous.
        var (sim, projector, _) = MakeWorld(128);

        // Two of player 0's own units, gathered where they already stand so the
        // group forms immediately rather than after a march.
        var mine = sim.World.Units.Values.Where(u => u.OwnerId == 0).Take(2).ToList();
        Assert.Equal(2, mine.Count);
        var rendezvous = mine[0].Position;
        sim.SubmitIntent(sim.Now, new CreateAndMuster(
            mine.Select(u => u.Id).ToArray(), rendezvous) { PlayerId = 0 });
        sim.Run(until: sim.Now + 1);

        // The sim is the authority on who ended up grouped; the wire must agree
        // with it exactly rather than with what we asked for.
        var expected = sim.World.Units.Values
            .Where(u => u.OwnerId == 0 && u.GroupId is not null)
            .ToDictionary(u => u.Id, u => u.GroupId!.Value);
        Assert.NotEmpty(expected);

        // reveal:true so foreign units are in the payload at all — that is the only
        // way to assert they are redacted.
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        var sawForeign = false;
        foreach (var u in v2.Units)
        {
            if (u.OwnerId != 0)
            {
                sawForeign = true;
                Assert.Equal(-1, u.GroupId);   // enemy order of battle is private
                continue;
            }
            Assert.Equal(expected.TryGetValue(u.Id, out var g) ? g : -1, u.GroupId);
        }
        Assert.True(sawForeign, "no foreign units in the revealed view — privacy went unasserted");
    }

    // The fogged path builds units from UnitView, not Unit, and has to look the real
    // unit back up to fill own-only fields. That is a separate code path from the one
    // above and has silently dropped fields before.
    [Fact]
    public void GroupId_SurvivesTheFoggedProjectionPath()
    {
        var (sim, projector, _) = MakeWorld();

        var mine = sim.World.Units.Values.Where(u => u.OwnerId == 0).Take(2).ToList();
        sim.SubmitIntent(sim.Now, new CreateAndMuster(
            mine.Select(u => u.Id).ToArray(), mine[0].Position) { PlayerId = 0 });
        sim.Run(until: sim.Now + 1);

        var expected = sim.World.Units.Values
            .Where(u => u.OwnerId == 0 && u.GroupId is not null)
            .ToDictionary(u => u.Id, u => u.GroupId!.Value);
        Assert.NotEmpty(expected);

        var fogged = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        var matched = 0;
        foreach (var u in fogged.Units.Where(u => u.OwnerId == 0))
        {
            Assert.Equal(expected.TryGetValue(u.Id, out var g) ? g : -1, u.GroupId);
            if (u.GroupId != -1) matched++;
        }
        // Own units are always visible to their owner, so every grouped unit must
        // have come through — a zero here would make the assert loop vacuous.
        Assert.Equal(expected.Count, matched);
    }

    // Pacing is a HOST dial, not a sim one. These two tests pin the property that
    // makes it safe to expose: the sim cannot tell the difference.
    [Fact]
    public void Pace_IsClampedAndReportedBack()
    {
        var (_, _, build) = MakeWorld();
        using var host = new GameHost(build, seed: 0xBEEF, ticksPerSecond: 4.0);

        Assert.Equal((false, 4.0), host.GetPace());

        // Above the ceiling clamps down, and the caller is TOLD the real pace so the
        // client's speed control can render the server's truth, not its own request.
        Assert.Equal((false, GameHost.MaxTicksPerSecond), host.SetPace(false, 1000.0));
        // Zero is not a back-door pause: the floor keeps "stopped" expressible only
        // through the flag that actually means it.
        Assert.Equal((false, GameHost.MinTicksPerSecond), host.SetPace(false, 0.0));

        Assert.Equal((true, 8.0), host.SetPace(true, 8.0));
        Assert.Equal((true, 8.0), host.GetPace());
    }

    [Fact]
    public void Pace_NeverAppearsInTheDurableIntentLog()
    {
        var (_, _, build) = MakeWorld();
        using var host = new GameHost(build, seed: 0xBEEF, ticksPerSecond: 4.0);

        host.SetPace(true, 1.0);
        host.SetPace(false, 32.0);

        // A pause is not a world event. If pacing ever became an intent it would be
        // replayed, and a replay would then be hostage to how fast someone once
        // watched it — which is exactly the coupling this endpoint exists to avoid.
        var pace = host.SubmitEnvelopeJson("{\"typeName\":\"SetPaceIntent\",\"payload\":\"{}\"}");
        Assert.Contains("false", pace);   // no such intent type exists, and must not
    }

    // The equipment catalog rides genesis for the same reason the build catalog
    // does — and this one has a scar. The client kept a hand-written copy of
    // "Sword for Soldiers, Bow for Archers", and that copy quietly omitted the
    // Cart, so a hauler could never be given one and nothing in either codebase
    // could notice. Comparing against EquipmentCatalog directly is what makes a
    // future item impossible to lose the same way.
    [Fact]
    public void CraftCatalog_IsExactlyTheSimsForgeableItems()
    {
        var (_, projector, _) = MakeWorld();
        var world = projector.BuildWorldDto();

        var expected = Enum.GetValues<Sim.Core.World.Resource>()
            .Where(r => Sim.Core.Equipment.EquipmentCatalog.TryGetSpec(r, out _))
            .Select(r => (int)r)
            .OrderBy(r => r)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, world.Craftable.Select(c => c.Item).ToArray());

        // The Cart specifically: the item the mirror lost. If it ever falls off
        // the wire again, this is the line that says so.
        Assert.Contains(world.Craftable, c => c.Item == (int)Sim.Core.World.Resource.Cart);

        foreach (var opt in world.Craftable)
        {
            var item = (Sim.Core.World.Resource)opt.Item;
            Assert.True(Sim.Core.Equipment.EquipmentCatalog.TryGetSpec(item, out var spec));

            Assert.Equal((int)spec.CraftedAt, opt.CraftedAt);
            Assert.Equal(spec.BuffKind, opt.BuffKind);
            Assert.Equal(spec.PowerModifier, opt.PowerModifier);
            Assert.Equal(spec.HealthModifier, opt.HealthModifier);
            Assert.Equal(spec.CargoModifier, opt.CargoModifier);
            Assert.Equal(spec.MoveCostPercent, opt.MoveCostPercent);

            Assert.Equal(spec.CraftCost.Count, opt.Cost.Length);
            foreach (var c in opt.Cost)
                Assert.Equal(spec.CraftCost[(Sim.Core.World.Resource)c.Resource], c.Amount);

            Assert.Equal(
                spec.AllowedRoles.Select(r => (int)r).OrderBy(r => r).ToArray(),
                opt.AllowedRoles);
        }
    }

    // C2 — the build catalog rides genesis so the client's build menu is the sim's
    // own table rather than a copy of it. The whole point is that it CANNOT drift,
    // so this test compares against StructureCatalog directly.
    [Fact]
    public void BuildCatalog_IsExactlyTheSimsPlayerBuildableKinds()
    {
        var (_, projector, _) = MakeWorld();
        var world = projector.BuildWorldDto();

        var expected = Enum.GetValues<Sim.Core.World.StructureKind>()
            .Where(k => Sim.Core.World.StructureCatalog.Spec(k).IsPlayerBuildable)
            .Select(k => (int)k)
            .OrderBy(k => k)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, world.Buildable.Select(b => b.Kind).ToArray());

        foreach (var opt in world.Buildable)
        {
            var spec = Sim.Core.World.StructureCatalog.Spec((Sim.Core.World.StructureKind)opt.Kind);
            Assert.Equal((int)spec.RequiredBiome, opt.RequiredBiome);
            Assert.Equal(spec.RequiredBuilderCount, opt.BuildersRequired);
            Assert.Equal(spec.ClaimCount, opt.ClaimCount);
            Assert.Equal(spec.BuildCost.Count, opt.Cost.Length);
            foreach (var c in opt.Cost)
                Assert.Equal(spec.BuildCost[(Sim.Core.World.Resource)c.Resource], c.Amount);

            // What it DOES once built — the half of the catalog the build menu needs
            // to answer "why would I want this?".
            Assert.Equal((int)spec.OutputResource, opt.OutputResource);
            Assert.Equal((int)spec.PreferredRole, opt.PreferredRole);
            Assert.Equal(spec.WorkerCap, opt.WorkerCap);
            Assert.Equal(spec.StorageCapacity, opt.StorageCapacity);
            Assert.Equal(spec.ResidentCap, opt.ResidentCap);
        }
    }

    // C2/M43 — the current step. Without it the client draws units at tile centres and
    // they teleport; nothing ever reads as marching. The numbers must be the SIM'S, not an
    // estimate, or the animation drifts out of step with where the unit actually is.
    [Fact]
    public void SubtileStep_MatchesTheSimsOwnStepAnchorAndCost()
    {
        var (sim, projector, _) = MakeWorld();

        var mover = sim.World.Units.Values.First(u => u.OwnerId == 0);
        var from = mover.Position;
        var dest = new Sim.Core.World.TileCoord(from.X + 6, from.Y);
        sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(mover.Id, dest) { PlayerId = 0 });
        sim.Run(until: sim.Now + 1);

        // The sim must actually be mid-walk, or the assertions below are vacuous.
        Assert.True(mover.IsWalking);
        Assert.NotNull(mover.SubtileRouteTick);

        var here = Sim.Core.Battlefields.WorldSubtile.Of(mover.Position, mover.Subtile!.Value);
        var next = mover.SubtileRoute![0];
        var expectedCost = Sim.Core.Battlefields.SubtileRoutes.StepCost(sim.World, mover, here, next, mover.SubtileRouteTick!.Value);

        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        var dto = v2.Units.Single(u => u.Id == mover.Id);

        Assert.Equal((next.X, next.Y), (dto.SubStepX, dto.SubStepY));
        Assert.Equal(mover.SubtileRouteTick!.Value, dto.SubStepArriveTick);
        Assert.Equal(expectedCost, dto.SubStepTotalTicks);
        Assert.True(dto.SubStepTotalTicks > 0, "a step with no duration cannot be interpolated");
    }

    // A step is PUBLIC where a destination is PRIVATE: you can see which way an army
    // is walking, but not where it is ultimately headed. If the step were redacted
    // like DestX/DestY, every foreign unit would slide around without animating.
    [Fact]
    public void SubtileStep_IsPublicEvenThoughTheDestinationIsNot()
    {
        var (sim, projector, _) = MakeWorld(128);

        var foreign = sim.World.Units.Values.First(u => u.OwnerId != 0);
        var dest = new Sim.Core.World.TileCoord(foreign.Position.X + 6, foreign.Position.Y);
        sim.SubmitIntent(sim.Now,
            new Sim.Core.Movement.MoveIntent(foreign.Id, dest) { PlayerId = foreign.OwnerId });
        sim.Run(until: sim.Now + 1);
        Assert.True(foreign.IsWalking);

        // reveal:true so the foreign unit is in the payload at all.
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        var dto = v2.Units.Single(u => u.Id == foreign.Id);

        Assert.Equal(-1, dto.DestX);          // the plan stays private
        Assert.Equal(-1, dto.DestY);
        Assert.Empty(dto.RouteX);
        Assert.True(dto.SubStepX >= 0, "the step a visible unit is taking is observable");
        Assert.True(dto.SubStepTotalTicks > 0);
    }

    // A unit standing still has no step, and -1 is what says so. A client that
    // interpolated a stale step would walk idle units off their subtile.
    [Fact]
    public void SubtileStep_IsAbsentForAStandingUnit()
    {
        var (sim, projector, _) = MakeWorld();
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);

        var still = v2.Units.Where(u => u.OwnerId == 0).ToList();
        Assert.NotEmpty(still);
        foreach (var u in still)
        {
            Assert.Equal(-1, u.SubStepX);
            Assert.Equal(-1, u.SubStepY);
            Assert.Equal(-1, u.SubStepArriveTick);
            Assert.Equal(-1, u.SubStepTotalTicks);
        }
    }

    // M42 — where a unit stands on its tile is a physical fact (public); the route
    // it was told to walk is an order (own units only).
    [Fact]
    public void Subtile_IsPublic_ButTheDrawnRouteIsOwnUnitsOnly()
    {
        var (sim, projector, _) = MakeWorld(128);

        var mine = sim.World.Units.Values.First(u => u.OwnerId == 0);
        var foreign = sim.World.Units.Values.First(u => u.OwnerId != 0);
        var route = new List<Sim.Core.Battlefields.WorldSubtile>
        {
            new(mine.Position.X * 4 + 1, mine.Position.Y * 4 + 1),
            new(mine.Position.X * 4 + 2, mine.Position.Y * 4 + 1),
        };
        mine.Subtile = new Sim.Core.Battlefields.Subtile(1, 2);
        mine.SubtileRoute = route;
        foreign.Subtile = new Sim.Core.Battlefields.Subtile(3, 0);
        foreign.SubtileRoute = new List<Sim.Core.Battlefields.WorldSubtile>
        {
            new(foreign.Position.X * 4 + 3, foreign.Position.Y * 4 + 1),
        };

        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        var m = v2.Units.Single(u => u.Id == mine.Id);
        var f = v2.Units.Single(u => u.Id == foreign.Id);

        Assert.Equal((1, 2), (m.SubX, m.SubY));
        Assert.Equal(route.Select(r => r.X), m.RouteX);
        Assert.Equal(route.Select(r => r.Y), m.RouteY);

        Assert.Equal((3, 0), (f.SubX, f.SubY));           // seen, like the hop
        Assert.Empty(f.RouteX);                           // the plan stays private
        Assert.Empty(f.RouteY);
    }

    // M42 — a unit mid-route shows the step it is taking (public, like the hop), and
    // one that is not stepping shows none.
    [Fact]
    public void SubtileStep_ShowsTheRoutesNextStep_AndIsAbsentWhenStanding()
    {
        var (sim, projector, _) = MakeWorld(128);
        var u = sim.World.Units.Values.First(x => x.OwnerId == 0);
        var still = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false).Units.Single(x => x.Id == u.Id);
        Assert.Equal(-1, still.SubStepX);
        Assert.Equal(-1, still.SubStepTotalTicks);

        u.Subtile = new Sim.Core.Battlefields.Subtile(1, 1);
        var here = Sim.Core.Battlefields.WorldSubtile.Of(u.Position, u.Subtile.Value);
        var next = new Sim.Core.Battlefields.WorldSubtile(here.X + 1, here.Y);
        u.SubtileRoute = new List<Sim.Core.Battlefields.WorldSubtile> { next };
        u.SubtileRouteTick = sim.Now + 9;

        var dto = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false).Units.Single(x => x.Id == u.Id);
        Assert.Equal((next.X, next.Y), (dto.SubStepX, dto.SubStepY));
        Assert.Equal(sim.Now + 9, dto.SubStepArriveTick);
        Assert.True(dto.SubStepTotalTicks > 0);
    }

    [Fact]
    public void Subtile_IsMinusOneWhenAUnitHasNone()
    {
        var (sim, projector, _) = MakeWorld();
        var u0 = sim.World.Units.Values.First(u => u.OwnerId == 0);
        u0.Subtile = null;
        var dto = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false).Units.Single(u => u.Id == u0.Id);
        Assert.Equal(-1, dto.SubX);
        Assert.Equal(-1, dto.SubY);
        Assert.Empty(dto.RouteX);
    }

    // The breeding UI has to say WHY a citizen is ineligible — too young, past the
    // window, the house too poor. Those bounds are an explicit tuning knob sim-side
    // ("change it here and every age gate follows"), so a copy in the client would
    // start lying the first time demography was retuned.
    [Fact]
    public void PopulationRules_AreTheWorldsOwnConfig()
    {
        var (sim, projector, _) = MakeWorld();
        var cfg = sim.World.PopulationConfig;
        var world = projector.BuildWorldDto(cfg);

        Assert.Equal(cfg.TicksPerYear, world.Population.TicksPerYear);
        Assert.Equal(cfg.MinTrainAge, world.Population.MinTrainAge);
        Assert.Equal(cfg.MinFertileAge, world.Population.MinFertileAge);
        Assert.Equal(cfg.MaxFertileAge, world.Population.MaxFertileAge);
        Assert.Equal(cfg.GestationTicks, world.Population.GestationTicks);
        Assert.Equal(cfg.BirthFoodCost, world.Population.BirthFoodCost);

        // A window that is empty or inverted would gray out every citizen forever.
        Assert.True(world.Population.MinFertileAge > 0);
        Assert.True(world.Population.MaxFertileAge >= world.Population.MinFertileAge);
        Assert.True(world.Population.BirthFoodCost > 0);
    }

    // M30 — the visibility contract has to hold on the path CLIENTS USE.
    //
    // The goal fields were filled in the reveal-path projection and not in the fogged
    // one, so a player watching their own builder walk across the map saw goalKind 0
    // and an empty goalState the whole way: the errand was invisible to the only
    // person it exists for. reveal:true is a dev switch; reveal:false is the game.
    [Fact]
    public void Goal_IsVisibleOnTheFoggedPathNotJustTheRevealedOne()
    {
        var (sim, projector, _) = MakeWorld();

        // A site somewhere the builder is NOT, so the goal has to carry them there.
        var builder = sim.World.Units.Values.First(
            u => u.OwnerId == 0 && u.Role == Sim.Core.World.UnitRole.Builder);
        var castle = sim.World.Structures.Values.First(
            s => s.OwnerId == 0 && s.Kind == Sim.Core.World.StructureKind.Castle);
        var siteTile = new Sim.Core.World.TileCoord(castle.At.X - 4, castle.At.Y - 4);

        sim.SubmitIntent(sim.Now, new Sim.Core.Logistics.BuildIntent(
            siteTile, Sim.Core.World.StructureKind.Stockpile, builderId: builder.Id)
            { PlayerId = 0 });
        sim.Run(until: sim.Now + 1);

        // The sim must actually have bound a goal, or this proves nothing.
        Assert.NotNull(builder.Goal);

        var fogged = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        var mine = fogged.Units.Single(u => u.Id == builder.Id);

        Assert.Equal((int)builder.Goal!.Kind, mine.GoalKind);
        Assert.Equal(builder.Goal.TargetTile.X, mine.GoalX);
        Assert.Equal(builder.Goal.TargetTile.Y, mine.GoalY);
        Assert.False(string.IsNullOrEmpty(mine.GoalState),
            "a bound errand must say what it is doing — that is the whole contract");

        // And the two projections must agree, since they describe the same unit.
        var revealed = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true)
            .Units.Single(u => u.Id == builder.Id);
        Assert.Equal(revealed.GoalKind, mine.GoalKind);
        Assert.Equal(revealed.GoalState, mine.GoalState);
        Assert.Equal(revealed.GoalX, mine.GoalX);
        Assert.Equal(revealed.GoalY, mine.GoalY);
    }

    // Someone else's errands are their business.
    [Fact]
    public void Goal_IsOwnOnly()
    {
        var (sim, projector, _) = MakeWorld(128);
        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);

        var sawForeign = false;
        foreach (var u in v2.Units)
        {
            if (u.OwnerId == 0) continue;
            sawForeign = true;
            Assert.Equal(0, u.GoalKind);
            Assert.Equal("", u.GoalState);
            Assert.Equal(-1, u.GoalX);
            Assert.Equal(-1, u.GoalY);
        }
        Assert.True(sawForeign, "no foreign units — privacy went unasserted");
    }

    // A freshly formed group is FORMING — members still walking to the rendezvous —
    // and MoveGroupIntent refuses it outright. Without the state on the wire the
    // player forms an army, orders it to march, and is refused for a reason they had
    // no way to see and no way to predict the end of.
    [Fact]
    public void GroupState_IsOnTheWireSoAFormingArmyCanSaySo()
    {
        var (sim, projector, _) = MakeWorld();

        // Two units that are NOT already together, so the group must actually form.
        var mine = sim.World.Units.Values.Where(u => u.OwnerId == 0).ToList();
        var a = mine[0];
        var b = mine.First(u => u.Position != a.Position);

        sim.SubmitIntent(sim.Now, new CreateAndMuster(
            new[] { a.Id, b.Id }, a.Position) { PlayerId = 0 });
        sim.Run(until: sim.Now + 1);

        var gid = a.GroupId;
        Assert.NotNull(gid);
        var group = sim.World.Groups[gid!.Value];

        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        foreach (var u in v2.Units.Where(u => u.GroupId == gid.Value))
            Assert.Equal((int)group.State, u.GroupState);

        // The whole point: a member that is still walking reports Forming, which is
        // exactly the state MoveGroupIntent refuses.
        Assert.Equal((int)Sim.Core.Groups.GroupState.Forming, (int)group.State);
        Assert.Equal((int)Sim.Core.Groups.GroupState.Forming,
            v2.Units.First(u => u.Id == a.Id).GroupState);

        // Ungrouped units report 0, not a state they do not have.
        foreach (var u in v2.Units.Where(u => u.OwnerId == 0 && u.GroupId <= 0))
            Assert.Equal(0, u.GroupState);
    }

    // M31 — the crown tag says WHO wears it; these say what wearing it means. A
    // client with the tag alone can draw a crown and nothing else: no aura, and no
    // way to tell the player their monarch is a child projecting nothing. That
    // minority window is meant to be a visible, plannable weakness — and a window
    // nobody can see is not telegraphed.
    [Fact]
    public void RoyaltyRules_AreTheWorldsOwnConfig()
    {
        var (sim, projector, _) = MakeWorld();
        var cfg = sim.World.RoyaltyConfig;
        var world = projector.BuildWorldDto(sim.World.PopulationConfig, cfg);

        Assert.Equal(cfg.AuraRadius, world.Royalty.AuraRadius);
        Assert.Equal(cfg.AuraPowerBonus, world.Royalty.AuraPowerBonus);
        Assert.Equal(cfg.MajorityAge, world.Royalty.MajorityAge);

        // A zero radius or bonus would mean the aura does not exist, and the client
        // would be drawing a disc for nothing.
        Assert.True(world.Royalty.AuraRadius > 0);
        Assert.True(world.Royalty.AuraPowerBonus > 0);
    }

    // ── P2 — fortification visibility ─────────────────────────────────────────

    // Three free land tiles in a row, well inside the map: a besieger's tile, the
    // wall's tile, and the defender's tile behind it.
    private static (TileCoord unitAt, TileCoord wallAt, TileCoord behind) FreeRow(GameWorld world)
    {
        for (var y = 4; y < world.Grid.Height - 4; y++)
            for (var x = 4; x < world.Grid.Width - 6; x++)
            {
                var a = new TileCoord(x, y);
                var b = new TileCoord(x + 1, y);
                var c = new TileCoord(x + 2, y);
                if (world.Grid.BiomeAt(a) == Biome.Water || world.Grid.BiomeAt(b) == Biome.Water
                    || world.Grid.BiomeAt(c) == Biome.Water) continue;
                if (world.Structures.ContainsKey(a) || world.Structures.ContainsKey(b)
                    || world.Structures.ContainsKey(c)) continue;
                return (a, b, c);
            }
        throw new InvalidOperationException("no free land row");
    }

    private static bool IsFortKind(int kind) => (StructureKind)kind is StructureKind.Wall
        or StructureKind.Gate or StructureKind.Tower or StructureKind.Castle;

    // P2/T1 — health is own-only for ordinary kinds, public for fortifications,
    // and 0/0 for kinds the catalog makes indestructible.
    [Fact]
    public void StructureHealth_OwnAlways_FortificationsPublic_OtherEnemyKindsPrivate()
    {
        var (sim, projector, _) = MakeWorld();
        var world = sim.World;
        var (_, wallAt, behind) = FreeRow(world);
        var wall = world.AddStructure(new Wall(wallAt) { OwnerId = 1 });   // enemy wall
        wall.Health = 123;                                                 // damaged: exact, not max
        // An enemy store: destructible, not a fortification, so it must stay private.
        world.AddStructure(new Stockpile(behind) { OwnerId = 1 });
        Assert.True(StructureCatalog.Spec(StructureKind.Stockpile).BaseHealth > 0);

        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        Assert.NotEmpty(v2.Structures);
        var sawOwn = false; var sawEnemyOther = false;
        foreach (var st in v2.Structures)
        {
            var spec = StructureCatalog.Spec((StructureKind)st.Kind);
            if (spec.BaseHealth == 0)
            {
                Assert.Equal(0, st.Health); Assert.Equal(0, st.MaxHealth);
                continue;
            }
            if (st.OwnerId == 0 || IsFortKind(st.Kind))
            {
                Assert.Equal(spec.BaseHealth, st.MaxHealth);
                Assert.Equal(world.Structures[new TileCoord(st.X, st.Y)].Health, st.Health);
                Assert.True(st.Health > 0);
                if (st.OwnerId == 0) sawOwn = true;
            }
            else
            {
                Assert.Equal(-1, st.Health); Assert.Equal(-1, st.MaxHealth);
                sawEnemyOther = true;
            }
        }
        Assert.True(sawOwn, "no own structure on the wire");
        Assert.True(sawEnemyOther, "no enemy non-fortification on the wire to prove privacy");

        var row = Assert.Single(v2.Structures, st => st.X == wallAt.X && st.Y == wallAt.Y);
        Assert.Equal(1, row.OwnerId);
        Assert.Equal(123, row.Health);
        Assert.Equal(StructureCatalog.Spec(StructureKind.Wall).BaseHealth, row.MaxHealth);
    }

    // P2/T2 — the build menu can say what a fortification does and how much it takes.
    [Fact]
    public void BuildCatalog_CarriesFortificationSemanticsAndBaseHealth()
    {
        var (_, projector, _) = MakeWorld();
        var options = projector.BuildWorldDto().Buildable.ToDictionary(o => (StructureKind)o.Kind);

        foreach (var (kind, o) in options)
        {
            var spec = StructureCatalog.Spec(kind);
            Assert.Equal(spec.BlocksMovement, o.BlocksMovement);
            Assert.Equal(spec.AlliedPassage, o.AlliedPassage);
            Assert.Equal(spec.BaseHealth, o.BaseHealth);
        }
        // The semantics the menu exists to state: a wall blocks everyone, a gate
        // blocks everyone but allies, a farm blocks nobody.
        Assert.True(options[StructureKind.Wall].BlocksMovement);
        Assert.False(options[StructureKind.Wall].AlliedPassage);
        Assert.True(options[StructureKind.Gate].BlocksMovement);
        Assert.True(options[StructureKind.Gate].AlliedPassage);
        Assert.False(options[StructureKind.Farm].BlocksMovement);
        Assert.True(options[StructureKind.Wall].BaseHealth > 0);
    }

    // P2/T3 — a siege row says what it is besieging, who is at the wall, and
    // what the next round will deal, computed by the same rule FortSiege uses.
    [Fact]
    public void CombatRow_CarriesSiegeState_ForAFortAndZerosForAFieldBattle()
    {
        var (sim, projector, _) = MakeWorld();
        var world = sim.World;
        var (unitAt, wallAt, behind) = FreeRow(world);
        world.AddStructure(new Wall(wallAt) { OwnerId = 1 });
        var u = new Unit(9001, unitAt) { Role = UnitRole.Soldier, OwnerId = 0 };
        world.AddUnit(u);
        // A defender behind the wall: gives the wall's owner eyes on the siege, and
        // must NOT be counted as a besieger (own side, not hostile to the fort).
        world.AddUnit(new Unit(9002, behind) { Role = UnitRole.Soldier, OwnerId = 1 });
        world.Diplomacy.SetState(Sim.Core.Diplomacy.FactionPair.Of(0, 1),
            Sim.Core.Diplomacy.RelationshipState.Enemy);
        Sim.Core.Fortifications.FortSiege.MaybeBeginSiegeAdjacentTo(sim, unitAt);
        Assert.True(world.CombatStates.ContainsKey(wallAt), "siege did not open");

        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        var row = Assert.Single(v2.Combats, c => c.X == wallAt.X && c.Y == wallAt.Y);
        Assert.Equal((int)StructureKind.Wall, row.FortKind);
        Assert.Equal(1, row.Besiegers);
        var expected = Sim.Core.Combat.CombatRules.EffectivePower(world, u, sim.Now);
        Assert.True(expected > 0);
        Assert.Equal(expected, row.SiegePower);

        // The other side sees the same numbers: the wall's owner can count who is
        // at their gate.
        var theirs = projector.ProjectV2(sim, sim.Now, playerId: 1, reveal: false);
        var theirRow = Assert.Single(theirs.Combats, c => c.X == wallAt.X && c.Y == wallAt.Y);
        Assert.Equal(row.SiegePower, theirRow.SiegePower);
        Assert.Equal(row.Besiegers, theirRow.Besiegers);

        // A combat on a tile with no standing fortification is a field battle:
        // FortKind 0 and nothing counted.
        Assert.DoesNotContain(v2.Combats, c => c.FortKind == 0 && (c.Besiegers != 0 || c.SiegePower != 0));
    }

    // ── P3 — combat legibility ────────────────────────────────────────────────

    // P3/T1 — the unit catalog is exactly the sim's role table, so Health and
    // cargo on the view have a denominator the client did not invent.
    [Fact]
    public void UnitCatalog_IsExactlyTheSimsRoleTable()
    {
        var (_, projector, _) = MakeWorld();
        var rows = projector.BuildWorldDto().Units;

        var expected = Enum.GetValues<UnitRole>().Where(r => r != UnitRole.None).OrderBy(r => (int)r).ToArray();
        Assert.Equal(expected.Select(r => (int)r), rows.Select(o => o.Role));
        foreach (var o in rows)
        {
            var role = (UnitRole)o.Role;
            var spec = Sim.Core.Combat.UnitCombatCatalog.Spec(role);
            Assert.Equal(spec.BaseHealth, o.BaseHealth);
            Assert.Equal(spec.BasePower, o.BasePower);
            Assert.Equal(Sim.Core.Logistics.UnitCargoCatalog.CapacityFor(role), o.CargoCapacity);
            Assert.True(o.BaseHealth > 0, $"{role} has no health");
        }
        // The two rows the client most needs to be right about.
        var hauler = Assert.Single(rows, o => o.Role == (int)UnitRole.Hauler);
        Assert.Equal(Sim.Core.Logistics.UnitCargoCatalog.HaulerCapacity, hauler.CargoCapacity);
        var soldier = Assert.Single(rows, o => o.Role == (int)UnitRole.Soldier);
        Assert.True(soldier.BasePower > hauler.BasePower);
    }

    // P3/T2 — a chase is an order; the wire shows yours and never theirs, on both
    // projection paths.
    [Fact]
    public void Pursuit_IsOnTheWireForOwnUnitsOnly()
    {
        var (sim, projector, _) = MakeWorld();
        var world = sim.World;
        var (unitAt, targetAt, enemyAt) = FreeRow(world);
        var target = new Unit(9101, targetAt) { Role = UnitRole.Bandit, OwnerId = -1 };
        world.AddUnit(target);
        var mine = new Unit(9102, unitAt) { Role = UnitRole.Soldier, OwnerId = 0 };
        mine.Pursuit = new Pursuit(target.Id, unitAt, leashRadius: 4);
        world.AddUnit(mine);
        var theirs = new Unit(9103, enemyAt) { Role = UnitRole.Soldier, OwnerId = 1 };
        theirs.Pursuit = new Pursuit(target.Id, enemyAt, leashRadius: 0);
        world.AddUnit(theirs);

        foreach (var reveal in new[] { true, false })
        {
            var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: reveal);
            var own = Assert.Single(v2.Units, u => u.Id == mine.Id);
            Assert.Equal(target.Id, own.PursuitTargetId);
            Assert.Equal(unitAt.X, own.PursuitLeashX);
            Assert.Equal(unitAt.Y, own.PursuitLeashY);
            Assert.Equal(4, own.PursuitLeashRadius);

            var foe = Assert.Single(v2.Units, u => u.Id == theirs.Id);
            Assert.Equal(-1, foe.PursuitTargetId);
            Assert.Equal(-1, foe.PursuitLeashX);
            Assert.Equal(-1, foe.PursuitLeashY);
            Assert.Equal(-1, foe.PursuitLeashRadius);

            // Not chasing: the -1 sentinel, distinct from a real radius of 0.
            var idle = Assert.Single(v2.Units, u => u.Id == target.Id);
            Assert.Equal(-1, idle.PursuitTargetId);
        }

        // A leash of 0 ("chase while visible") is a value, not an absence.
        var enemyView = projector.ProjectV2(sim, sim.Now, playerId: 1, reveal: true);
        var chaser = Assert.Single(enemyView.Units, u => u.Id == theirs.Id);
        Assert.Equal(0, chaser.PursuitLeashRadius);
        Assert.Equal(target.Id, chaser.PursuitTargetId);
    }

    // P3/T3 — a field battle says who is on the tile, per owner, with the same
    // power sum the sim fights with. Public, ordered by owner, embarked excluded.
    [Fact]
    public void CombatRow_CarriesSidesPerOwner_ForAFieldBattle()
    {
        var (sim, projector, _) = MakeWorld();
        var world = sim.World;
        var (tile, _, _) = FreeRow(world);
        var a1 = new Unit(9201, tile) { Role = UnitRole.Soldier, OwnerId = 0 };
        var a2 = new Unit(9202, tile) { Role = UnitRole.Archer, OwnerId = 0 };
        var b1 = new Unit(9203, tile) { Role = UnitRole.Soldier, OwnerId = 1 };
        foreach (var u in new[] { a1, a2, b1 }) world.AddUnit(u);
        world.Diplomacy.SetState(Sim.Core.Diplomacy.FactionPair.Of(0, 1),
            Sim.Core.Diplomacy.RelationshipState.Enemy);
        var state = new Sim.Core.Combat.CombatState(tile) { RoundNumber = 2, NextRoundTick = sim.Now + 10 };
        world.CombatStates[tile] = state;

        var v2 = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        var row = Assert.Single(v2.Combats, c => c.X == tile.X && c.Y == tile.Y);
        Assert.Equal(0, row.FortKind);   // a field battle, not a siege
        Assert.Equal(2, row.Sides.Length);
        Assert.Equal(new[] { 0, 1 }, row.Sides.Select(s => s.OwnerId));

        var mine = row.Sides[0];
        Assert.Equal(2, mine.Units);
        Assert.Equal(Sim.Core.Combat.CombatRules.ForcePower(world, 0, tile, sim.Now), mine.Power);
        var theirs = row.Sides[1];
        Assert.Equal(1, theirs.Units);
        Assert.Equal(Sim.Core.Combat.CombatRules.ForcePower(world, 1, tile, sim.Now), theirs.Power);
        Assert.True(theirs.Power > 0);

        // The enemy reads the same rollup: sides are public.
        var enemyView = projector.ProjectV2(sim, sim.Now, playerId: 1, reveal: false);
        var enemyRow = Assert.Single(enemyView.Combats, c => c.X == tile.X && c.Y == tile.Y);
        Assert.Equal(row.Sides.Select(s => (s.OwnerId, s.Units, s.Power)),
                     enemyRow.Sides.Select(s => (s.OwnerId, s.Units, s.Power)));
    }

    // P1/T10 — the view carries a raw ClaimFertility per extractor tile; genesis
    // carries the band edges that turn it into a grade. Read off the world's own
    // config so a retuned ladder can never leave the client grading against 7500/2500
    // from memory.
    [Fact]
    public void FertilityRules_AreTheWorldsOwnConfig()
    {
        var (sim, projector, _) = MakeWorld();
        var cfg = sim.World.BiomeDegradationConfig;
        var world = projector.BuildWorldDto(sim.World.PopulationConfig, sim.World.RoyaltyConfig, cfg);

        Assert.Equal(cfg.ForestThreshold, world.Fertility.ForestThreshold);
        Assert.Equal(cfg.DesertThreshold, world.Fertility.DesertThreshold);

        // The ladder must be a ladder: Desert below Forest, and both above zero, or
        // the client would grade every tile into one band.
        Assert.True(world.Fertility.DesertThreshold > 0);
        Assert.True(world.Fertility.ForestThreshold > world.Fertility.DesertThreshold);

        // The parameterless overload (tests and tooling with no world in hand) ships
        // the config defaults, not zeros.
        var defaults = new Sim.Core.Biomes.BiomeDegradationConfig();
        Assert.Equal(defaults.ForestThreshold, projector.BuildWorldDto().Fertility.ForestThreshold);
        Assert.Equal(defaults.DesertThreshold, projector.BuildWorldDto().Fertility.DesertThreshold);
    }

    // C3 — one clock, two consumers. Genesis ships the projector's own light cycle
    // and every view carries the server's evaluation of it, so the client's sky can
    // prove it agrees with what narration will read.
    [Fact]
    public void LightCycle_GenesisShipsTheConfig_AndEachViewCarriesItsPhase()
    {
        var (sim, projector, _) = MakeWorld();
        var cfg = Sim.Server.Atmosphere.LightCycleConfig.ForCycle(Sim.Core.Time.Day);
        projector.LightCycle = cfg;

        var world = projector.BuildWorldDto(sim.World.PopulationConfig, sim.World.RoyaltyConfig);
        Assert.Equal(cfg.TicksPerCycle, world.LightCycle.TicksPerCycle);
        Assert.Equal(cfg.PhaseOffsetTicks, world.LightCycle.PhaseOffsetTicks);

        foreach (var now in new long[] { 0, 1, Sim.Core.Time.Day / 2, Sim.Core.Time.Day * 3 + 17 })
        {
            var view = projector.ProjectV2(sim, now, playerId: 0, reveal: false);
            Assert.Equal(Sim.Server.Atmosphere.WorldClock.Phase(now, cfg), view.LightPhase);
        }
    }

    // The crown reaches the fogged path, and stays own-only.
    [Fact]
    public void Royal_IsTaggedForOwnUnitsAndPrivateForEveryoneElse()
    {
        var (sim, projector, _) = MakeWorld(128);

        var fogged = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        var kings = fogged.Units.Where(u => u.Royal == 1).ToList();
        Assert.Single(kings);
        Assert.Equal(0, kings[0].OwnerId);

        // Every faction is crowned at genesis, so a revealed view would show other
        // kings too if the tag were public — it must not.
        var revealed = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);
        var sawForeign = false;
        foreach (var u in revealed.Units)
        {
            if (u.OwnerId == 0) continue;
            sawForeign = true;
            Assert.Equal(0, u.Royal);
        }
        Assert.True(sawForeign, "no foreign units — privacy went unasserted");
    }

    // THE GUARD FOR A BUG CLASS, not for one bug.
    //
    // UnitDto is projected by TWO overloads: one from a real Unit (the reveal path,
    // a dev switch) and one from a UnitView (the fogged path, which is the actual
    // game). Every own-only field added since the v2 wire landed has gone into the
    // first and been forgotten in the second — the M30 goal fields, and then the M31
    // Royal tag. Both times the feature worked perfectly with reveal=1 and was
    // invisible to every real player.
    //
    // For the VIEWER'S OWN units the two projections describe the same unit with the
    // same knowledge, so they must agree field for field. Comparing by reflection
    // means the next field added is covered without anyone remembering to cover it.
    [Fact]
    public void OwnUnits_ProjectIdenticallyOnBothPaths()
    {
        var (sim, projector, _) = MakeWorld();

        // Give the units something to be mid-doing, so this compares live state and
        // not fourteen identical rows of defaults.
        var mover = sim.World.Units.Values.First(u => u.OwnerId == 0);
        sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(
            mover.Id, new Sim.Core.World.TileCoord(mover.Position.X + 5, mover.Position.Y))
            { PlayerId = 0 });
        sim.Run(until: sim.Now + 1);

        var fogged = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false)
            .Units.Where(u => u.OwnerId == 0).ToDictionary(u => u.Id);
        var revealed = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true)
            .Units.Where(u => u.OwnerId == 0).ToDictionary(u => u.Id);

        Assert.NotEmpty(fogged);
        Assert.Equal(revealed.Keys.OrderBy(k => k), fogged.Keys.OrderBy(k => k));

        var props = typeof(UnitDto).GetProperties();
        foreach (var (id, f) in fogged)
        {
            var r = revealed[id];
            foreach (var prop in props)
            {
                var a = prop.GetValue(r);
                var b = prop.GetValue(f);
                if (a is Array aa && b is Array bb)
                {
                    Assert.Equal(aa.Length, bb.Length);
                    continue;
                }
                Assert.True(Equals(a, b),
                    $"unit {id}: {prop.Name} is {a} when revealed but {b} through the fog — " +
                    "a field was added to one projection overload and not the other");
            }
        }
    }

    // Training is gated on standing INSIDE the right building, so the command panel
    // must know which building teaches what. Inverted server-side from
    // RoleTrainerCatalog rather than copied into the client, where it would drift the
    // first time a role moved between the School and the Barracks.
    [Fact]
    public void BuildCatalog_SaysWhichRolesEachBuildingTrains()
    {
        var (_, projector, _) = MakeWorld();
        var world = projector.BuildWorldDto();

        foreach (var opt in world.Buildable)
        foreach (var role in opt.TrainsRoles)
            Assert.Equal((Sim.Core.World.StructureKind)opt.Kind,
                Sim.Core.Population.RoleTrainerCatalog.TrainerFor((Sim.Core.World.UnitRole)role));

        var school = world.Buildable.Single(b => b.Kind == (int)Sim.Core.World.StructureKind.School);
        var barracks = world.Buildable.Single(b => b.Kind == (int)Sim.Core.World.StructureKind.Barracks);

        Assert.Contains((int)Sim.Core.World.UnitRole.Farmer, school.TrainsRoles);
        Assert.Contains((int)Sim.Core.World.UnitRole.Builder, school.TrainsRoles);
        Assert.DoesNotContain((int)Sim.Core.World.UnitRole.Soldier, school.TrainsRoles);

        Assert.Equal(
            new[] { (int)Sim.Core.World.UnitRole.Soldier, (int)Sim.Core.World.UnitRole.Archer }
                .OrderBy(r => r).ToArray(),
            barracks.TrainsRoles.OrderBy(r => r).ToArray());

        // A Boat is dock-produced, never trained from a citizen, so no building may
        // offer it. A Farm teaches nobody at all.
        foreach (var opt in world.Buildable)
            Assert.DoesNotContain((int)Sim.Core.World.UnitRole.Boat, opt.TrainsRoles);
        Assert.Empty(world.Buildable
            .Single(b => b.Kind == (int)Sim.Core.World.StructureKind.Farm).TrainsRoles);
    }

    // The three kinds a single tile cannot express. PlaceSiteIntent rejects all of
    // them outright, so a one-click build button for any of them is a button that
    // can only ever fail — the flag is what keeps them out of that menu.
    [Fact]
    public void BuildCatalog_FlagsTheKindsASingleTileCannotPlace()
    {
        var (_, projector, _) = MakeWorld();
        var world = projector.BuildWorldDto();

        int ModeOf(Sim.Core.World.StructureKind k) =>
            world.Buildable.Single(b => b.Kind == (int)k).PlacementMode;

        // The Dock is placeable — it just wants a second click for its slip. Calling
        // it merely "special" was what kept it out of the menu entirely.
        Assert.Equal(PlacementModes.SiteAndSlip, ModeOf(Sim.Core.World.StructureKind.Dock));

        // Whole-route builds with their own intents; PlaceSiteIntent rejects them.
        Assert.Equal(PlacementModes.Path, ModeOf(Sim.Core.World.StructureKind.Canal));
        Assert.Equal(PlacementModes.Path, ModeOf(Sim.Core.World.StructureKind.Wall));

        // Cleared, never built.
        Assert.Equal(PlacementModes.NotBuildable, ModeOf(Sim.Core.World.StructureKind.Rubble));

        // Everything else is one click.
        Assert.Equal(PlacementModes.Single, ModeOf(Sim.Core.World.StructureKind.Farm));
        Assert.Equal(PlacementModes.Single, ModeOf(Sim.Core.World.StructureKind.House));
    }
}