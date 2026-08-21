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

    // Grave markers ride every projected view when a tracker is attached
    // (GameHost wires its GraveTracker here at construction). This is what
    // puts graves in front of the AI BRAINS: AiPlayerDriver projects its
    // own view, so attaching graves only in GameHost's HTTP path would
    // leave the brains blind to battlefields (the SalvageRung's whole
    // input). Null (standalone projector, most tests) = no graves. NOTE:
    // GraveTracker.Project advances the per-player seen gate, so every
    // caller must hold the host lock — which they do: AI thinks and
    // BuildViewJson both run under GameHost._gate.
    public GraveTracker? GraveSource { get; set; }

    // The automation driver's journal, attached by GameHost the same way as
    // GraveSource above. Presentation-only and never hashed — a server that
    // leaves this null projects a world byte-identical to one that doesn't;
    // the client just loses the resting-vs-starving distinction on its
    // order dashboard. Null in tests and in the AI brains' own projector
    // (brains read the world, not their own paperwork).
    public Automation.OrderJournal? OrderSource { get; set; }

    public ViewProjector(WorldBuild build)
    {
        _map = build.Map;
        _elevation = build.Elevation;
        _waterLevel = (int)Math.Round(build.Config.WaterMax * 1000.0); // sea level on the 0..1000 scale
    }

    // The FULL per-tile elevation grid (whole map, NOT fog-filtered) for client-side
    // terrain synthesis (erosion). Immutable, generation-time data — terrain never
    // changes — so this reads nothing live and needs no sim lock. Flattened row-major
    // (index = y * Width + x) to match the client's heightmap layout; values are the
    // raw quantized elevation, [0, 1000].
    public ElevationDto BuildElevationDto()
    {
        var w = _map.Width;
        var h = _map.Height;
        var flat = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                flat[y * w + x] = _elevation[x, y];
        return new ElevationDto { Width = w, Height = h, WaterLevel = _waterLevel, Elevation = flat };
    }

    // ── v2 wire (Wire/WireV2.cs) ──────────────────────────────────────────────
    // GET /v2/world — the static genesis payload, fetched ONCE per session. Same
    // immutable, lock-free read as BuildElevationDto: terrain and the genesis biome
    // grid are generation-time facts. Everything a tick can change lives on the view.
    public WorldDto BuildWorldDto() => BuildWorldDto(new Sim.Core.Population.PopulationConfig());

    /// The genesis payload, with the world's own demographic rules folded in. The
    /// parameterless overload above uses defaults and exists for tests and tooling
    /// that have no world in hand.
    public WorldDto BuildWorldDto(Sim.Core.Population.PopulationConfig population)
    {
        var w = _map.Width;
        var h = _map.Height;
        var elev = new int[w * h];
        var biome = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                elev[i] = _elevation[x, y];
                biome[i] = (int)_map.Grid[x, y];
            }
        return new WorldDto
        {
            Width = w,
            Height = h,
            WaterLevel = _waterLevel,
            MapSeed = _map.Seed,
            TicksPerDay = Sim.Core.Time.Day,
            Elevation = elev,
            Biome = biome,
            Buildable = BuildCatalog(),
            Population = new PopulationRulesDto
            {
                TicksPerYear = population.TicksPerYear,
                MinTrainAge = population.MinTrainAge,
                MinFertileAge = population.MinFertileAge,
                MaxFertileAge = population.MaxFertileAge,
                GestationTicks = population.GestationTicks,
                BirthFoodCost = population.BirthFoodCost,
            },
        };
    }

    // The player-buildable kinds, read straight off StructureCatalog so the client's
    // build menu and the sim's validator can never disagree. Ordered by kind for a
    // deterministic payload.
    private static BuildOptionDto[] BuildCatalog()
    {
        var options = new List<BuildOptionDto>();
        foreach (var kind in Enum.GetValues<StructureKind>().OrderBy(k => (int)k))
        {
            var spec = StructureCatalog.Spec(kind);
            if (!spec.IsPlayerBuildable) continue;

            options.Add(new BuildOptionDto
            {
                Kind = (int)kind,
                Name = kind.ToString(),
                RequiredBiome = (int)spec.RequiredBiome,
                Cost = spec.BuildCost
                    .OrderBy(kv => (int)kv.Key)
                    .Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value })
                    .ToArray(),
                BuildersRequired = spec.RequiredBuilderCount,
                BuildDurationTicks = spec.BuildDurationTicks,
                ClaimCount = spec.ClaimCount,
                ClaimRange = spec.ClaimRange,
                // The placement gesture. This must track the by-name rejections at
                // the top of PlaceSiteIntent.Resolve — Canal and Wall are whole-path
                // builds with their own intents, Rubble is cleared rather than built
                // — plus the Dock, which resolves only when given a slip tile.
                PlacementMode = kind switch
                {
                    StructureKind.Dock => PlacementModes.SiteAndSlip,
                    StructureKind.Canal or StructureKind.Wall => PlacementModes.Path,
                    StructureKind.Rubble => PlacementModes.NotBuildable,
                    _ => PlacementModes.Single,
                },
                OutputResource = (int)spec.OutputResource,
                PreferredRole = (int)spec.PreferredRole,
                WorkerCap = spec.WorkerCap,
                StorageCapacity = spec.StorageCapacity,
                ResidentCap = spec.ResidentCap,
                TrainsRoles = TrainableAt(kind),
            });
        }
        return options.ToArray();
    }

    // Which roles this building trains, by inverting RoleTrainerCatalog. Boat maps
    // to no trainer (dock-produced, never trained from a citizen) and None is not a
    // role anyone asks for, so both are skipped. Ordered by role id, deterministic.
    private static int[] TrainableAt(StructureKind kind) =>
        Enum.GetValues<UnitRole>()
            .Where(r => r != UnitRole.None && r != UnitRole.Boat
                        && r != UnitRole.Bandit
                        && Sim.Core.Population.RoleTrainerCatalog.TrainerFor(r) == kind)
            .OrderBy(r => (int)r)
            .Select(r => (int)r)
            .ToArray();

    // GET /v2/view/{playerId} — the per-tick payload. Same sim reads as Project(),
    // but the tile arrays (96% of a v1 view, and almost entirely constant) become an
    // RLE'd fog-state grid plus the handful of tiles whose BELIEVED biome has drifted
    // from genesis. Must be called under the host lock, exactly like Project: it reads
    // live world state and advances GraveSource's seen gate.
    public ViewV2Dto ProjectV2(Simulation sim, long now, int playerId, bool reveal)
    {
        var world = sim.World;
        var cfg = world.BiomeDegradationConfig;
        var w = _map.Width;
        var h = _map.Height;
        var view = View.BuildPlayerView(world, playerId, now);

        // Fog RLE and biome drift in ONE row-major sweep, so both come from a single
        // definition of "what this player believes about this tile".
        var runState = new List<int>();
        var runLen = new List<int>();
        var overrides = new List<BiomeOverrideDto>();
        var cur = -1;
        var run = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var tile = new TileCoord(x, y);
                int state, believed;
                if (reveal || view.Visible.Contains(tile))
                {
                    state = FogState.Live;
                    believed = (int)BiomeDegradation.BiomeAt(world, tile, now, cfg);
                }
                else if (view.RememberedTerrain.TryGetValue(tile, out var remembered))
                {
                    state = FogState.Remembered;
                    believed = (int)remembered;   // what the player LAST SAW, not what is true
                }
                else
                {
                    state = FogState.Unknown;
                    believed = -1;                // unknown tiles carry no biome at all
                }

                if (believed >= 0 && believed != (int)_map.Grid[x, y])
                    overrides.Add(new BiomeOverrideDto { X = x, Y = y, Biome = believed });

                if (state == cur) run++;
                else
                {
                    if (run > 0) { runState.Add(cur); runLen.Add(run); }
                    cur = state;
                    run = 1;
                }
            }
        if (run > 0) { runState.Add(cur); runLen.Add(run); }

        // Roads are terrain memory (design 8.6): they persist through re-fog, so any
        // EXPLORED road tile ships with its live decayed condition. Iterating the sparse
        // Roads dict keeps this bounded by road count, not map size.
        var roads = new List<RoadDto>();
        foreach (var tile in world.Roads.Keys)
        {
            if (!reveal && !view.Explored.Contains(tile)) continue;
            var cond = Road.ConditionAt(world, tile, now);
            if (cond > 0) roads.Add(new RoadDto { X = tile.X, Y = tile.Y, Condition = cond });
        }

        var dto = new ViewV2Dto
        {
            PlayerId = playerId,
            Width = w,
            Height = h,
            WaterLevel = _waterLevel,
            Tick = now,
            FogRunState = runState.ToArray(),
            FogRunLength = runLen.ToArray(),
            BiomeOverrides = overrides.ToArray(),
            // Fog-limited by Sim.Core even under reveal (OngoingCombats is scoped to the
            // viewer's Visible set). Acceptable: reveal is a dev switch, not a play mode.
            Combats = view.OngoingCombats
                .Select(c => new CombatDto
                {
                    X = c.Tile.X, Y = c.Tile.Y,
                    RoundNumber = c.RoundNumber, NextRoundTick = c.NextRoundTick,
                })
                .ToArray(),
            Units = reveal
                ? world.Units.Values.Select(u => ToUnitDto(u, playerId, world, now)).ToArray()
                : view.VisibleUnits.Select(u => ToUnitDto(u, playerId, world, now)).ToArray(),
            Structures = reveal
                ? world.Structures.Values.Select(s => ToStructDto(s, playerId, world, now)).ToArray()
                : view.VisibleStructures.Select(s => ToStructDto(s, playerId, world, now)).ToArray(),
            Roads = roads.ToArray(),
        };

        // Everything below reuses the v1 fill helpers verbatim — they take a ViewDto and
        // a ViewV2Dto is one. That reuse is the whole reason ViewDto is no longer sealed.
        FillFood(dto, sim, now, playerId);
        FillDiplomacy(dto, world, playerId);
        FillOrders(dto, world, playerId, OrderSource);
        FillPiles(dto, world, reveal);
        if (GraveSource is not null)
        {
            // GraveTracker.Project reads only X/Y off this array (its visibility gate).
            var visTiles = view.Visible.Select(t => new TileDto { X = t.X, Y = t.Y }).ToArray();
            dto.Graves = GraveSource.Project(playerId, reveal, visTiles);
        }
        return dto;
    }

    public ViewDto Project(Simulation sim, long now, int playerId, bool reveal)
    {
        var dto = reveal ? ProjectRevealed(sim.World, now, playerId) : ProjectFogged(sim.World, now, playerId);
        dto.Tick = now;   // world age in ticks (= game-minutes); the client's clock reads from this
        FillFood(dto, sim, now, playerId);
        FillDiplomacy(dto, sim.World, playerId);
        FillOrders(dto, sim.World, playerId, OrderSource);
        FillPiles(dto, sim.World, reveal);
        if (GraveSource is not null)
            dto.Graves = GraveSource.Project(playerId, reveal, dto.Visible);
        return dto;
    }

    // Ground piles for tiles in CURRENT sight (the M23 cache stance: a
    // visible container's contents are public, because looting requires
    // naming a resource to LoadCargoIntent — and the AI brains read the
    // same rows). Pure read of world.GroundResources; no remembered
    // reveal — a pile can be looted at any time, so only live sight may
    // vouch for its contents. Deterministic order: piles (Y, X), rows by
    // resource id (GroundResources' inner map is sorted).
    private static void FillPiles(ViewDto dto, GameWorld world, bool reveal)
    {
        if (world.GroundResources.Count == 0) return;
        HashSet<long>? vis = null;
        if (!reveal)
        {
            vis = new HashSet<long>();
            foreach (var t in dto.Visible) vis.Add(((long)t.X << 32) ^ (uint)t.Y);
        }
        List<PileDto>? piles = null;
        foreach (var (tile, pile) in world.GroundResources
                     .OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X))
        {
            if (vis is not null && !vis.Contains(((long)tile.X << 32) ^ (uint)tile.Y)) continue;
            var holdings = pile.Where(kv => kv.Value > 0)
                .Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value })
                .ToArray();
            if (holdings.Length == 0) continue;
            (piles ??= new List<PileDto>()).Add(new PileDto { X = tile.X, Y = tile.Y, Holdings = holdings });
        }
        if (piles is not null) dto.Piles = piles.ToArray();
    }

    // M25 — diplomatic state onto the wire. Mirrors View.BuildPlayerView's
    // projection rules (docs/diplomacy-model.md): Factions / Relationships /
    // PendingWars are public knowledge, identical for every viewer, fog or
    // no fog; IncomingProposals is the one per-viewer slice. Negative
    // sentinel owners (bandits -1 / caches -2 / rubble -3) sit outside
    // diplomacy and are never listed — the same id >= 0 discipline as
    // PlayerDefeatedEvent's live-player count. Pure read, same as FillFood;
    // factions sorted by id so the payload is order-stable.
    private static void FillDiplomacy(ViewDto dto, GameWorld world, int playerId)
    {
        var factions = new List<FactionDto>();
        foreach (var (id, p) in world.Players.OrderBy(kv => kv.Key))
            if (id >= 0)
                factions.Add(new FactionDto { Id = id, Defeated = p.Defeated });

        var relationships = new List<RelationshipDto>();
        var pendingWars = new List<PendingWarDto>();
        foreach (var (pair, rel) in world.Diplomacy.Relationships) // sorted by pair key
        {
            relationships.Add(new RelationshipDto
            {
                LoId = pair.Lo,
                HiId = pair.Hi,
                State = (int)rel.State,
                PendingEffectiveTick = rel.PendingEffectiveTick ?? -1,
            });
            if (rel.PendingEffectiveTick is { } tick)
                pendingWars.Add(new PendingWarDto { LoId = pair.Lo, HiId = pair.Hi, EffectiveTick = tick });
        }

        var proposals = new List<ProposalDto>();
        foreach (var (_, p) in world.Diplomacy.Proposals) // sorted by id
            if (p.TargetId == playerId)
                proposals.Add(new ProposalDto
                {
                    Id = p.Id,
                    ProposerId = p.ProposerId,
                    TargetId = p.TargetId,
                    DesiredState = (int)p.DesiredState,
                    ExpiryTick = p.ExpiryTick,
                });

        dto.Factions = factions.ToArray();
        dto.Relationships = relationships.ToArray();
        dto.PendingWars = pendingWars.ToArray();
        dto.IncomingProposals = proposals.ToArray();
    }

    // The viewer's OWN automation orders, definition + live status.
    // Owner-only by construction (we filter on OwnerId); reveal mode does
    // not change this — automation plans are private strategy, not terrain.
    private static void FillOrders(ViewDto dto, GameWorld world, int playerId,
        Automation.OrderJournal? journal)
    {
        if (world.Orders.Count == 0) return;
        var orders = new List<OrderDto>();
        foreach (var (id, o) in world.Orders) // ascending id — canonical
        {
            if (o.OwnerId != playerId) continue;
            var last = journal?.Last(id);
            orders.Add(new OrderDto
            {
                Id = id,
                Priority = o.Priority,
                Program = (int)o.Program,
                Recipe = (int)o.Recipe,
                SubjectKind = (int)o.SubjectKind,
                SubjectX = o.SubjectTile.X,
                SubjectY = o.SubjectTile.Y,
                SubjectRole = (int)o.SubjectRole,
                SubjectGroupId = o.SubjectGroupId,
                Target = o.Target,
                SourceX = o.SourceTile.X,
                SourceY = o.SourceTile.Y,
                Resource = (int)o.Resource,
                CrewMode = (int)o.CrewMode,
                NamedCrew = o.NamedCrew.ToArray(),
                // Live: who this order is actually holding right now.
                HeldUnits = Sim.Core.Automation.ClaimLedger.UnitsOf(world, id).ToArray(),
                Selector = new SelectorDto
                {
                    Role = (int)o.Selector.Role,
                    AnyRole = o.Selector.AnyRole,
                    MinAgeYears = o.Selector.MinAgeYears,
                    MaxAgeYears = o.Selector.MaxAgeYears,
                    RequireDormant = o.Selector.RequireDormant,
                    AnchorX = o.Selector.Anchor.X,
                    AnchorY = o.Selector.Anchor.Y,
                    Radius = o.Selector.Radius,
                },
                Trigger = o.Trigger.Any.Select(c => new TriggerClauseDto
                {
                    All = c.All.Select(ToDto).ToArray(),
                }).ToArray(),
                Steps = o.Steps.Select(s => new RoutineStepDto
                {
                    X = s.Tile.X,
                    Y = s.Tile.Y,
                    Action = (int)s.Action,
                    Resource = (int)s.Resource,
                    DepartWhen = s.DepartWhen.Select(ToDto).ToArray(),
                }).ToArray(),
                EngageRadius = o.EngageRadius,
                LeashRadius = o.LeashRadius,
                Enabled = o.Enabled,
                RetryCount = o.RetryCount,
                LastFiredTick = o.LastFiredTick,
                CurrentStep = o.CurrentStep,
                LastOutcome = last is null ? 0 : (int)last.Value.Outcome,
                LastOutcomeTick = last?.Tick ?? 0,
                LastDetail = last?.Detail ?? "",
            });
        }
        if (orders.Count > 0) dto.Orders = orders.ToArray();
    }

    private static PredicateDto ToDto(Sim.Core.Automation.Predicate p) => new()
    {
        Kind = (int)p.Kind,
        X = p.Tile.X,
        Y = p.Tile.Y,
        Resource = (int)p.Resource,
        Role = (int)p.Role,
        Threshold = p.Threshold,
        CountInFlight = p.CountInFlight,
    };

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
        var dto = new UnitDto
        {
            Id = u.Id, X = u.Position.X, Y = u.Position.Y, Role = (int)u.Role, OwnerId = u.OwnerId,
            Age = Sim.Core.Population.Population.AgeYears(u, now, world.PopulationConfig),
            Health = mine ? u.Health : -1,
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
            // Private like Activity: you command your own formations, you do not
            // read the enemy's order of battle off the map.
            GroupId = mine ? u.GroupId ?? -1 : -1,
            // M30 — the pending-goal tag. Own units only, same rule as Activity.
            GoalKind = mine ? (int)(u.Goal?.Kind ?? 0) : 0,
            GoalState = mine ? GoalStateOf(u, world) : "",
            GoalX = mine ? u.Goal?.TargetTile.X ?? -1 : -1,
            GoalY = mine ? u.Goal?.TargetTile.Y ?? -1 : -1,
        };
        FillHop(dto, u, world, now);
        return dto;
    }

    // M30 — what a pending goal is DOING right now, in the words the visibility
    // contract asks for: "en route" while the body is still walking, and
    // "waiting: <precondition>" once it has arrived and stalled. The two read
    // differently on purpose — a stalled goal is waiting on something the
    // player controls, and that is the one the player may want to act on.
    private static string GoalStateOf(Unit u, GameWorld world)
    {
        if (u.Goal is not { } goal) return "";
        if (u.Activity != Activity.Waiting) return "en route";

        // Waiting: name the thing that has not happened yet. Today only the
        // breeding pair waits, and it waits on one of exactly two things.
        if (goal.Kind == GoalKind.Breed
            && world.Structures.TryGetValue(goal.TargetTile, out var s) && s is House house)
        {
            var cfg = world.PopulationConfig;
            if (house.AmountOf(Resource.Food) < cfg.BirthFoodCost) return "waiting: food";
            return "waiting: partner";
        }
        return "waiting";
    }

    // The hop a unit is in the middle of: which tile it is stepping to, the tick it
    // lands, and how many ticks the step takes. Everything the client needs to place
    // it BETWEEN tiles instead of snapping it to a centre four times a second.
    //
    // Pure reads throughout — the anchor fields the sim already keeps for recovery
    // (PathRemaining / NextArrivalTick) plus MovementCost.ExecutionCost, which is the
    // same function that priced the hop when it was scheduled. So the duration is the
    // sim's own number, not an estimate.
    //
    // Emitted for EVERY visible unit. See UnitDto.HopToX for why a step is public
    // where a destination is not.
    private static void FillHop(UnitDto dto, Unit u, GameWorld world, long now)
    {
        if (u.NextArrivalTick is not { } arriveAt) return;
        if (u.PathRemaining is not { Count: > 0 } path) return;

        var to = path[0];
        var cost = Sim.Core.Movement.MovementCost.ExecutionCost(
            world, u.Position, to, now, u.Traversal);

        // Impassable comes back as a sentinel cost; a hop that cannot be priced is
        // one the client should not try to animate.
        if (cost <= 0 || cost >= Sim.Core.World.Biomes.Impassable) return;

        dto.HopToX = to.X;
        dto.HopToY = to.Y;
        dto.HopArriveTick = arriveAt;
        dto.HopTotalTicks = cost;
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
        var groupId = -1;
        // The hop is public, so the real unit is looked up for EVERY visible unit,
        // not only the viewer's own. The own-only enrichment stays inside the branch.
        world.Units.TryGetValue(uv.Id, out var live);
        if (uv.OwnerId == viewerPlayerId && world.Units.TryGetValue(uv.Id, out var real))
        {
            groupId = real.GroupId ?? -1;
            activity = (int)real.Activity;
            cap = real.PassengerCap;
            pax = real.Passengers.Count;
            cargoRes = (int)real.CargoResource;
            cargoAmt = real.CargoAmount;
            power = Sim.Core.Combat.CombatRules.EffectivePower(real, now);
            buffs = real.Buffs.Select(b => b.Kind).ToArray();
            if (FinalDestOf(real, world) is { } dest) { destX = dest.X; destY = dest.Y; }
        }
        var dto2 = new UnitDto
        {
            Id = uv.Id, X = uv.Position.X, Y = uv.Position.Y, Role = (int)uv.Role, OwnerId = uv.OwnerId,
            Age = uv.AgeYears,
            Health = uv.OwnerId == viewerPlayerId ? uv.Health : -1,
            Activity = activity,
            PassengerCap = cap,
            Passengers = pax,
            CargoResource = cargoRes,
            CargoAmount = cargoAmt,
            Power = power,
            Buffs = buffs,
            DestX = destX,
            DestY = destY,
            GroupId = groupId,
        };
        if (live is not null) FillHop(dto2, live, world, now);
        return dto2;
    }

    // Reveal path: the real Structure is in hand.
    private static StructDto ToStructDto(Structure s, int viewerPlayerId, GameWorld world, long now)
    {
        var dto = new StructDto { X = s.At.X, Y = s.At.Y, Kind = (int)s.Kind, OwnerId = s.OwnerId };
        if (s.OwnerId == viewerPlayerId) EnrichOwned(dto, s, world, now);
        FillClaims(dto, s);
        FillCacheLoot(dto, s);
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
            FillCacheLoot(dto, real);
        }
        return dto;
    }

    // M23 — a discovered cache's contents are revealed to whoever can SEE it
    // (an unowned Cache reaches the DTO only when its tile is visible). Same
    // "a visible structure's contents are public" stance as FillClaims, and
    // necessary so the player can name which resource to LootCacheIntent.
    // Stays fogged like the cache itself — no Remembered reveal, so it
    // vanishes when you look away.
    private static void FillCacheLoot(StructDto dto, Structure s)
    {
        if (s is not Cache cache) return;
        dto.Holdings = cache.Holdings
            .Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value })
            .ToArray();
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
                // Soil visibility (own-only): live fertility per claim
                // tile, parallel to the ClaimX/ClaimY arrays FillClaims
                // emits (same source list, same order). Pure read.
                if (ex.ClaimTiles.Count > 0)
                    dto.ClaimFertility = ex.ClaimTiles
                        .Select(t => Sim.Core.Biomes.BiomeDegradation.FertilityAt(
                            world, t, now, world.BiomeDegradationConfig))
                        .ToArray();
                break;

            // M19 — a house is a FOOD HOME: expose its live SIGNED local
            // food (negative during a local famine by exactly the unpaid
            // debt — same contract as the top-level CastleFood), its
            // resident headcount, and the local-famine flag. Own-only
            // like every enrichment; this is what lets a player (and
            // therefore the brain, fairly) see a hungry house. Must come
            // before StorageStructure — a House IS one.
            case House h:
                dto.Capacity = h.Capacity;
                dto.Holdings = h.Holdings.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                dto.LocalFood = FoodConsumption.CurrentLevel(h, world, now);
                dto.Residents = h.ResidentCount;
                dto.LocalFamine = h.FamineStartTick.HasValue;
                break;

            // The castle is a FOOD HOME too, and needs the same treatment a house
            // already gets. Holdings[Food] at a food home is NOT a second view of
            // the larder — it is a lazily-caught-up internal that is simply STALE
            // between rate-changing events, and it can sit at its genesis value
            // for a long time while residents eat (a castle still reading 200
            // when the realm had 88). Never show it as food; LocalFood is the
            // only honest number, and it is the one automation now triggers on.
            // Must come before StorageStructure: a Castle IS one.
            case Castle castle:
                dto.Capacity = castle.Capacity;
                dto.Holdings = castle.Holdings.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                dto.LocalFood = FoodConsumption.CurrentLevel(castle, world, now);
                dto.LocalFamine = castle.FamineStartTick.HasValue;
                // Residents is left 0: the castle feeds everyone without a house,
                // and the realm panel's Population already carries that count.
                break;

            case StorageStructure ss:
                dto.Capacity = ss.Capacity;
                dto.Holdings = ss.Holdings.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                break;
        }
    }
}
