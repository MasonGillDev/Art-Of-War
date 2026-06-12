using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Roads;
using Sim.Core.Vision;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server.Wire;

namespace Sim.Server;

// Maps Sim.Core's authoritative state onto the flat wire DTOs the dumb client reads.
// Reads only Sim.Core public APIs (View.BuildPlayerView, Road.ConditionAt, structure
// holdings/buffers/needs). PURE: it never mutates sim state — safe to call under the
// host's read lock. Nothing here touches Sim.Core internals.
public sealed class ViewProjector
{
    private readonly GeneratedMap _map;
    private readonly int[,] _elevation;
    private readonly int _waterLevel;

    public ViewProjector(WorldBuild build)
    {
        _map = build.Map;
        _elevation = build.Elevation;
        _waterLevel = (int)Math.Round(build.Config.WaterMax * 1000.0); // sea level on the 0..1000 scale
    }

    public ViewDto Project(Simulation sim, long now, int playerId, bool reveal)
    {
        var dto = reveal ? ProjectRevealed(sim.World, now, playerId) : ProjectFogged(sim.World, now, playerId);
        FillFood(dto, sim, now, playerId);
        FillOrders(dto, sim.World, playerId);
        return dto;
    }

    // M18 — the viewer's OWN standing orders, definition + live cursor.
    // Owner-only by construction (we filter on OwnerId); reveal mode does
    // not change this — automation plans are private strategy, not terrain.
    private static void FillOrders(ViewDto dto, GameWorld world, int playerId)
    {
        if (world.StandingOrders.Count == 0) return;
        var orders = new List<OrderDto>();
        foreach (var (id, o) in world.StandingOrders) // ascending id
        {
            if (o.OwnerId != playerId) continue;
            orders.Add(new OrderDto
            {
                Id = id,
                Kind = (int)o.Kind,
                Loop = (int)o.Loop,
                Enabled = o.Enabled,
                CurrentStep = o.CurrentStep,
                Dispatched = o.ActionDispatched,
                RetryCount = o.StepRetryCount,
                StepEnteredTick = o.StepEnteredTick,
                ClaimedUnits = o.ClaimedUnits.ToArray(),
                Steps = o.Steps.Select(step => new OrderStepDto
                {
                    Conditions = step.Conditions.Select(c => new ConditionDto
                    {
                        Kind = (int)c.Kind,
                        UnitId = c.SubjectUnitId,
                        X = c.SubjectTile.X,
                        Y = c.SubjectTile.Y,
                        Resource = (int)c.Resource,
                        Threshold = c.Threshold,
                    }).ToArray(),
                    ActionKind = (int)step.Action.Kind,
                    ActionUnit = step.Action.UnitId,
                    TargetX = step.Action.TargetTile.X,
                    TargetY = step.Action.TargetTile.Y,
                    SecondX = step.Action.SecondTile.X,
                    SecondY = step.Action.SecondTile.Y,
                    Resource = (int)step.Action.Resource,
                    Role = (int)step.Action.Role,
                }).ToArray(),
            });
        }
        if (orders.Count > 0) dto.Orders = orders.ToArray();
    }

    // M13 food consumption — project the viewing player's population, live castle food
    // (pure-read CurrentLevel), per-period drain, runway-to-dry, and famine/starvation
    // countdown. CastleFood is SIGNED: during famine it goes negative by exactly the
    // unpaid FoodDebt (the famine-debt model, docs/food-consumption.md Update
    // 2026-06-11) — the magnitude is the deposit needed to stop the deaths. All
    // ticks-until values are pre-computed against `now` so the client doesn't need
    // the sim clock. Enemy food is never exposed (we only read OUR castle).
    private static void FillFood(ViewDto dto, Simulation sim, long now, int playerId)
    {
        var world = sim.World;
        var pop = world.Players.TryGetValue(playerId, out var p) ? p.PopulationCount : 0;
        var rate = pop * FoodConsumptionConstants.FoodPerCitizenPerPeriod;

        dto.Population = pop;
        dto.FoodPerPeriod = rate;
        dto.FoodPeriodTicks = FoodConsumptionConstants.FoodConsumptionPeriod;

        var castle = FoodConsumption.FindCastleFor(world, playerId);
        if (castle == null)
        {
            dto.CastleFood = 0;
            dto.FoodRunwayTicks = -1;
            dto.InFamine = false;
            dto.StarvationInTicks = -1;
            return;
        }

        dto.CastleFood = FoodConsumption.CurrentLevel(castle, sim, now);
        dto.InFamine = castle.FamineStartTick.HasValue;

        if (dto.InFamine)
        {
            dto.FoodRunwayTicks = 0;
            dto.StarvationInTicks = castle.NextStarvationDeathTick.HasValue
                ? Math.Max(0, castle.NextStarvationDeathTick.Value - now)
                : -1;
        }
        else
        {
            dto.StarvationInTicks = -1;
            dto.FoodRunwayTicks = rate > 0
                ? (long)(dto.CastleFood / rate) * FoodConsumptionConstants.FoodConsumptionPeriod
                : -1;
        }
    }

    // DEV MODE: ignore fog, return the whole map. Heavy payload; reveal toggle only.
    private ViewDto ProjectRevealed(GameWorld world, long now, int playerId)
    {
        var cfg = world.BiomeDegradationConfig;
        var tiles = new List<TileDto>(_map.Width * _map.Height);
        for (var y = 0; y < _map.Height; y++)
            for (var x = 0; x < _map.Width; x++)
            {
                var tile = new TileCoord(x, y);
                tiles.Add(new TileDto { X = x, Y = y, Biome = (int)BiomeDegradation.BiomeAt(world, tile, now, cfg), Elevation = _elevation[x, y] });
            }

        var roads = new List<RoadDto>();
        foreach (var tile in world.Roads.Keys)
        {
            var cond = Road.ConditionAt(world, tile, now);
            if (cond > 0) roads.Add(new RoadDto { X = tile.X, Y = tile.Y, Condition = cond });
        }

        return new ViewDto
        {
            PlayerId = playerId,
            Width = _map.Width,
            Height = _map.Height,
            WaterLevel = _waterLevel,
            Visible = tiles.ToArray(),
            Remembered = Array.Empty<TileDto>(),
            Units = world.Units.Values.Select(u => ToUnitDto(u, playerId, world, now)).ToArray(),
            Structures = world.Structures.Values.Select(s => ToStructDto(s, playerId, world, now)).ToArray(),
            Roads = roads.ToArray(),
        };
    }

    private ViewDto ProjectFogged(GameWorld world, long now, int playerId)
    {
        var cfg = world.BiomeDegradationConfig;
        // Pass the live `now` so UnitView.AgeYears is the CURRENT age (the zero-now
        // overload would freeze every unit at its starting age).
        var view = View.BuildPlayerView(world, playerId, now);

        // Roads are terrain memory (design §8.6): they persist through re-fog, so we
        // include any EXPLORED road tile (not just currently visible) with its live
        // decayed condition via the pure-read ConditionAt. Iterating the sparse Roads
        // dict keeps this bounded by road count, not map size.
        var roads = new List<RoadDto>();
        foreach (var tile in world.Roads.Keys)
        {
            if (!view.Explored.Contains(tile)) continue;
            var cond = Road.ConditionAt(world, tile, now);
            if (cond > 0) roads.Add(new RoadDto { X = tile.X, Y = tile.Y, Condition = cond });
        }

        return new ViewDto
        {
            PlayerId = playerId,
            Width = _map.Width,
            Height = _map.Height,
            WaterLevel = _waterLevel,
            Visible = view.Visible
                .Select(t => new TileDto { X = t.X, Y = t.Y, Biome = (int)BiomeDegradation.BiomeAt(world, t, now, cfg), Elevation = _elevation[t.X, t.Y] })
                .ToArray(),
            Remembered = view.RememberedTerrain
                .Select(kv => new TileDto { X = kv.Key.X, Y = kv.Key.Y, Biome = (int)kv.Value, Elevation = _elevation[kv.Key.X, kv.Key.Y] })
                .ToArray(),
            Units = view.VisibleUnits.Select(u => ToUnitDto(u, playerId, world, now)).ToArray(),
            Structures = view.VisibleStructures.Select(s => ToStructDto(s, playerId, world, now)).ToArray(),
            Roads = roads.ToArray(),
        };
    }

    // Reveal path: the real Unit is in hand, so age + activity come straight off it.
    // Activity is the viewer's own private info — hidden (-1) for other players' units.
    private static UnitDto ToUnitDto(Unit u, int viewerPlayerId, GameWorld world, long now)
    {
        var mine = u.OwnerId == viewerPlayerId;
        var dest = mine ? FinalDestOf(u, world) : null;
        return new UnitDto
        {
            Id = u.Id, X = u.Position.X, Y = u.Position.Y, Role = (int)u.Role, OwnerId = u.OwnerId,
            Age = Sim.Core.Population.Population.AgeYears(u, now, world.PopulationConfig),
            Activity = mine ? (int)u.Activity : -1,
            PassengerCap = mine ? u.PassengerCap : 0,
            Passengers = mine ? u.Passengers.Count : 0,
            CargoResource = mine ? (int)u.CargoResource : 0,
            CargoAmount = mine ? u.CargoAmount : 0,
            // Loadout is private military info — same rule as Activity.
            // EffectivePower is a pure read.
            Power = mine ? Sim.Core.Combat.CombatRules.EffectivePower(u, now) : -1,
            Buffs = mine ? u.Buffs.Select(b => b.Kind).ToArray() : Array.Empty<string>(),
            DestX = dest?.X ?? -1,
            DestY = dest?.Y ?? -1,
        };
    }

    // Where is this unit ultimately headed? Solo movement carries its own
    // PathFinalDest anchor; a grouped unit rides its Group's. Pure read.
    private static TileCoord? FinalDestOf(Unit u, GameWorld world)
    {
        if (u.PathFinalDest is { } d) return d;
        if (u.GroupId is { } gid
            && world.Groups.TryGetValue(gid, out var g)
            && g.PathFinalDest is { } gd) return gd;
        return null;
    }

    // Fogged path: UnitView already carries a live AgeYears (ProjectFogged passes `now`).
    // Activity isn't in the projection, so for the viewer's OWN units we read it off the
    // real Unit; other players' activity stays hidden (-1).
    private static UnitDto ToUnitDto(UnitView uv, int viewerPlayerId, GameWorld world, long now)
    {
        var activity = -1;
        var cap = 0; var pax = 0;
        var cargoRes = 0; var cargoAmt = 0;
        var power = -1;
        var buffs = Array.Empty<string>();
        var destX = -1; var destY = -1;
        if (uv.OwnerId == viewerPlayerId && world.Units.TryGetValue(uv.Id, out var real))
        {
            activity = (int)real.Activity;
            cap = real.PassengerCap;
            pax = real.Passengers.Count;
            cargoRes = (int)real.CargoResource;
            cargoAmt = real.CargoAmount;
            power = Sim.Core.Combat.CombatRules.EffectivePower(real, now);
            buffs = real.Buffs.Select(b => b.Kind).ToArray();
            if (FinalDestOf(real, world) is { } dest) { destX = dest.X; destY = dest.Y; }
        }
        return new UnitDto
        {
            Id = uv.Id, X = uv.Position.X, Y = uv.Position.Y, Role = (int)uv.Role, OwnerId = uv.OwnerId,
            Age = uv.AgeYears,
            Activity = activity,
            PassengerCap = cap,
            Passengers = pax,
            CargoResource = cargoRes,
            CargoAmount = cargoAmt,
            Power = power,
            Buffs = buffs,
            DestX = destX,
            DestY = destY,
        };
    }

    // Reveal path: the real Structure is in hand.
    private static StructDto ToStructDto(Structure s, int viewerPlayerId, GameWorld world, long now)
    {
        var dto = new StructDto { X = s.At.X, Y = s.At.Y, Kind = (int)s.Kind, OwnerId = s.OwnerId };
        if (s.OwnerId == viewerPlayerId) EnrichOwned(dto, s, world, now);
        FillClaims(dto, s);
        return dto;
    }

    // Fogged path: the player view carries a lightweight StructureView (pos/kind/owner
    // only). For the viewer's OWN structures, look the real Structure back up by tile to
    // fill holdings/build status; enemy structures stay pos/kind/owner.
    private static StructDto ToStructDto(StructureView sv, int viewerPlayerId, GameWorld world, long now)
    {
        var dto = new StructDto { X = sv.At.X, Y = sv.At.Y, Kind = (int)sv.Kind, OwnerId = sv.OwnerId };
        if (world.Structures.TryGetValue(sv.At, out var real))
        {
            if (sv.OwnerId == viewerPlayerId) EnrichOwned(dto, real, world, now);
            // M15 — claims are NOT own-only (the one exception to the
            // enrichment rule): a visible structure's land use is physical
            // and scoutable; placement rejections reference it anyway.
            FillClaims(dto, real);
        }
        return dto;
    }

    // M15 — claimed tiles for either carrier (extractor or pending site).
    private static void FillClaims(StructDto dto, Structure s)
    {
        var claims = s switch
        {
            Extractor e => e.ClaimTiles,
            ConstructionSite c => c.ClaimTiles,
            _ => null,
        };
        if (claims is null || claims.Count == 0) return;
        dto.ClaimX = claims.Select(t => t.X).ToArray();
        dto.ClaimY = claims.Select(t => t.Y).ToArray();
    }

    // Holdings / extractor buffer / construction-site status — private activity, only
    // ever filled for the viewer's own structures. All via Sim.Core public APIs.
    private static void EnrichOwned(StructDto dto, Structure s, GameWorld world, long now)
    {
        switch (s)
        {
            case ConstructionSite cs:
                dto.TargetKind = (int)cs.TargetKind;
                dto.Needed = cs.Required.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                dto.Holdings = cs.Delivered.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                dto.BuildersRequired = cs.RequiredBuilderCount;
                dto.BuildersPresent = cs.BuildersPresent(world);
                dto.Building = cs.IsActive;
                // Live build progress: banked ProgressTicks + the current active run's delta.
                var done = cs.ProgressTicks + (cs.LastActiveAtTick is { } start ? now - start : 0);
                if (done > cs.BuildDurationTicks) done = cs.BuildDurationTicks;
                dto.BuildProgress = cs.BuildDurationTicks > 0 ? (int)(100L * done / cs.BuildDurationTicks) : 0;
                dto.BuildEtaTicks = cs.IsActive && cs.ScheduledCompletion is { } sc ? Math.Max(0, sc - now) : -1;
                break;

            case Extractor ex:
                dto.Capacity = ex.Spec.BufferCap;
                dto.Workers = ex.Workers.Count;
                dto.WorkerCap = ex.Spec.WorkerCap;
                if (ex.Buffer > 0 && ex.Spec.OutputResource != Resource.None)
                    dto.Holdings = new[] { new ResAmtDto { Resource = (int)ex.Spec.OutputResource, Amount = ex.Buffer } };
                break;

            case StorageStructure ss:
                dto.Capacity = ss.Capacity;
                dto.Holdings = ss.Holdings.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                break;
        }
    }
}
