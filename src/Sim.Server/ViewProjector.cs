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
    // M36 — the haul driver's last verdicts (presentation only).
    public Hauling.HaulingDriver? HaulSource { get; set; }

    // The world's time of day (Atmosphere/WorldClock.cs). Presentation-only: nothing
    // in the sim reads it. Genesis ships the parameters, every v2 view the phase.
    public Atmosphere.LightCycleConfig LightCycle { get; set; } = new();

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

    // M35 — the environmental fertility baseline of every tile AT GENESIS
    // (docs/environmental-fertility.md), row-major like Elevation/Biome. A pure
    // function of the generated grid + rivers under the world's fertility
    // config, so it is computed from the frozen map, not the live world, and
    // memoised per config (the config is immutable for a world's lifetime;
    // the parameterless BuildWorldDto uses the defaults). The live view sends
    // the tiles whose baseline has since moved (canal lifts) as overrides.
    private (Sim.Core.Biomes.BiomeDegradationConfig cfg, int[] baseline)? _genesisBaseline;

    private int[] GenesisBaseline(Sim.Core.Biomes.BiomeDegradationConfig cfg)
    {
        if (_genesisBaseline is { } cached && cached.cfg == cfg) return cached.baseline;
        var w = _map.Width;
        var h = _map.Height;
        var grid = new TileGrid(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var t = new TileCoord(x, y);
                grid.SetBiome(t, _map.Grid[x, y]);
                grid.SetRiverEdges(t, _map.Rivers[x, y]);
            }
        var genesisWorld = new GameWorld(
            grid, new Sim.Core.Diplomacy.DiplomacyConfig(), new Sim.Core.Combat.CombatConfig(),
            new Sim.Core.Population.PopulationConfig(), cfg);
        var baseline = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                baseline[y * w + x] = BiomeDegradation.BaselineFertility(genesisWorld, new TileCoord(x, y), cfg);
        _genesisBaseline = (cfg, baseline);
        return baseline;
    }

    // ── v2 wire (Wire/WireV2.cs) ──────────────────────────────────────────────
    // GET /v2/world — the static genesis payload, fetched ONCE per session. Same
    // immutable, lock-free read as BuildElevationDto: terrain and the genesis biome
    // grid are generation-time facts. Everything a tick can change lives on the view.
    public WorldDto BuildWorldDto() => BuildWorldDto(
        new Sim.Core.Population.PopulationConfig(), new Sim.Core.Royalty.RoyaltyConfig());

    /// The genesis payload, with the world's own rules folded in. The parameterless
    /// overload above uses defaults and exists for tests and tooling with no world in
    /// hand.
    public WorldDto BuildWorldDto(Sim.Core.Population.PopulationConfig population,
                                  Sim.Core.Royalty.RoyaltyConfig royalty = default,
                                  Sim.Core.Biomes.BiomeDegradationConfig? fertility = null)
    {
        // Null means "the config defaults" — the parameterless constructor is the
        // canonical 7500/2500 ladder — so tooling without a world in hand still
        // ships a truthful block.
        var fert = fertility ?? new Sim.Core.Biomes.BiomeDegradationConfig();
        var w = _map.Width;
        var h = _map.Height;
        var elev = new int[w * h];
        var biome = new int[w * h];
        var river = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                elev[i] = _elevation[x, y];
                biome[i] = (int)_map.Grid[x, y];
                river[i] = (int)_map.Rivers[x, y];
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
            River = river,
            Baseline = GenesisBaseline(fert),
            Buildable = BuildCatalog(),
            Footprints = FootprintCatalog(),
            Population = new PopulationRulesDto
            {
                TicksPerYear = population.TicksPerYear,
                MinTrainAge = population.MinTrainAge,
                MinFertileAge = population.MinFertileAge,
                MaxFertileAge = population.MaxFertileAge,
                GestationTicks = population.GestationTicks,
                BirthFoodCost = population.BirthFoodCost,
            },
            Royalty = new RoyaltyRulesDto
            {
                AuraRadius = royalty.AuraRadius,
                AuraPowerBonus = royalty.AuraPowerBonus,
                MajorityAge = royalty.MajorityAge,
            },
            Craftable = CraftCatalog(),
            LightCycle = new LightCycleDto
            {
                TicksPerCycle = LightCycle.TicksPerCycle,
                PhaseOffsetTicks = LightCycle.PhaseOffsetTicks,
            },
            Fertility = new FertilityRulesDto
            {
                ForestThreshold = fert.ForestThreshold,
                DesertThreshold = fert.DesertThreshold,
                GrasslandBaseline = fert.GrasslandBaseline,
                ForestBaseline = fert.ForestBaseline,
                GrasslandMaxBaseline = Sim.Core.Biomes.EnvironmentalFertility.MaxBaseline(Biome.Grassland, fert),
                ForestMaxBaseline = Sim.Core.Biomes.EnvironmentalFertility.MaxBaseline(Biome.Forest, fert),
            },
            Units = UnitCatalog(),
            Doctrines = DoctrineCatalog(),
        };
    }

    // The battle doctrines, in catalog order (docs/battle-sandbox.md).
    private static DoctrineOptionDto[] DoctrineCatalog() =>
        Sim.Core.Battlefields.DoctrineCatalog.All
            .Select(d => new DoctrineOptionDto
            {
                Id = (int)d.Behaviour,
                Name = d.Name,
                Description = d.Description,
                TakesWithdrawBelow = d.TakesWithdrawBelow,
                Roles = d.Roles?.Select(r => (int)r).ToArray() ?? [],
            })
            .ToArray();

    // P3 — every role but None, ordered by id for a deterministic payload. Bandits
    // are included: the player sees them and needs to read them.
    private static UnitOptionDto[] UnitCatalog() =>
        Enum.GetValues<UnitRole>()
            .Where(r => r != UnitRole.None)
            .OrderBy(r => (int)r)
            .Select(r =>
            {
                var spec = Sim.Core.Combat.UnitCombatCatalog.Spec(r);
                return new UnitOptionDto
                {
                    Role = (int)r,
                    BaseHealth = spec.BaseHealth,
                    BasePower = spec.BasePower,
                    CargoCapacity = Sim.Core.Logistics.UnitCargoCatalog.CapacityFor(r),
                };
            })
            .ToArray();

    // The forgeable items, read straight off EquipmentCatalog so the client's
    // armoury menu and the sim's validator can never disagree — the same
    // contract as BuildCatalog below, and added because the hand-written mirror
    // it replaces had already lost the Cart. Ordered by item for a
    // deterministic payload.
    private static EquipmentOptionDto[] CraftCatalog()
    {
        var options = new List<EquipmentOptionDto>();
        foreach (var item in Enum.GetValues<Resource>().OrderBy(r => (int)r))
        {
            if (!Sim.Core.Equipment.EquipmentCatalog.TryGetSpec(item, out var spec)) continue;
            options.Add(new EquipmentOptionDto
            {
                Item = (int)item,
                CraftedAt = (int)spec.CraftedAt,
                Cost = spec.CraftCost
                    .Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value })
                    .ToArray(),
                AllowedRoles = spec.AllowedRoles.Select(r => (int)r).OrderBy(r => r).ToArray(),
                PowerModifier = spec.PowerModifier,
                HealthModifier = spec.HealthModifier,
                CargoModifier = spec.CargoModifier,
                MoveCostPercent = spec.MoveCostPercent,
                BuffKind = spec.BuffKind,
            });
        }
        return options.ToArray();
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
                RequiresVein = spec.RequiresVein,
                BlocksMovement = spec.BlocksMovement,
                AlliedPassage = spec.AlliedPassage,
                BaseHealth = spec.BaseHealth,
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
                Inputs = spec.InputCost
                    .OrderBy(kv => (int)kv.Key)
                    .Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value })
                    .ToArray(),
                InputCap = spec.InputCap,
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
    public ViewV2Dto ProjectV2(Simulation sim, long now, int playerId, bool reveal, bool revealOrders = false)
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
        // M35 — tiles whose environmental baseline has moved since genesis
        // (a canal lift). LIVE tiles only: the client falls back to the
        // genesis array elsewhere, so a lift is shown while you can see it.
        var baselineOverrides = new List<BaselineOverrideDto>();
        var genesisBaseline = GenesisBaseline(cfg);
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
                    var baseline = BiomeDegradation.BaselineFertility(world, tile, cfg);
                    if (baseline != genesisBaseline[y * w + x])
                        baselineOverrides.Add(new BaselineOverrideDto { X = x, Y = y, Baseline = baseline });
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
        // road LINK with an explored endpoint ships with its live decayed condition.
        // Iterating the sparse Roads dict keeps this bounded by road count, not map size.
        var roads = new List<RoadDto>();
        foreach (var arc in world.Roads.Keys)
        {
            if (!reveal && !view.Explored.Contains(arc.A.Tile) && !view.Explored.Contains(arc.B.Tile)) continue;
            var cond = Road.ConditionAt(world, arc, now);
            if (cond > 0) roads.Add(RoadDtoFor(arc, cond));
        }

        var dto = new ViewV2Dto
        {
            PlayerId = playerId,
            Width = w,
            Height = h,
            WaterLevel = _waterLevel,
            Tick = now,
            LandingTick = world.LandingConfig.Tick,
            LightPhase = Atmosphere.WorldClock.Phase(now, LightCycle),
            FogRunState = runState.ToArray(),
            FogRunLength = runLen.ToArray(),
            BiomeOverrides = overrides.ToArray(),
            BaselineOverrides = baselineOverrides.ToArray(),
            // Fog-limited by Sim.Core even under reveal (OngoingCombats is scoped to the
            // viewer's Visible set). Acceptable: reveal is a dev switch, not a play mode.
            Combats = view.OngoingCombats
                .Select(c => ToCombatDto(c, world, now))
                .ToArray(),
            Units = reveal
                ? world.Units.Values.Select(u => ToUnitDto(u, playerId, world, now)).ToArray()
                : view.VisibleUnits.Select(u => ToUnitDto(u, playerId, world, now)).ToArray(),
            Structures = reveal
                ? WithFootprints(world.Structures.Values.Select(s => ToStructDto(s, playerId, world, now)).ToArray(), world)
                : WithFootprints(view.VisibleStructures.Select(s => ToStructDto(s, playerId, world, now)).ToArray(), world),
            Roads = roads.ToArray(),
        };

        // Everything below reuses the v1 fill helpers verbatim — they take a ViewDto and
        // a ViewV2Dto is one. That reuse is the whole reason ViewDto is no longer sealed.
        FillFood(dto, sim, now, playerId);
        FillDiplomacy(dto, world, playerId);
        FillOrders(dto, world, playerId, OrderSource);
        FillHauling(dto, world, now, playerId, HaulSource);
        FillOmens(dto, world, now, playerId);
        FillSecrets(dto, world, now, playerId);
        FillPiles(dto, world, reveal);
        dto.Battlefields = BattlefieldProjection.Project(sim, playerId, reveal, revealOrders, view.Visible);
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
        dto.LandingTick = sim.World.LandingConfig.Tick;   // two-act pacing: the brains' countdown too
        FillFood(dto, sim, now, playerId);
        FillDiplomacy(dto, sim.World, playerId);
        FillOrders(dto, sim.World, playerId, OrderSource);
        FillHauling(dto, sim.World, now, playerId, HaulSource);
        FillOmens(dto, sim.World, now, playerId);
        FillSecrets(dto, sim.World, now, playerId);
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

    // M38 — the viewer's OWN chart and idol circles (docs/scouting-secrets.md).
    // Pure read. The chart carries the hint a scout brought home, never what
    // the secret is or holds.
    private static void FillSecrets(ViewDto dto, GameWorld world, long now, int playerId)
    {
        dto.Chart = Sim.Core.Scouting.Charts.Of(world, playerId) is { } chart
            ? chart.Values.Select(e => new ChartEntryDto
            {
                X = e.Tile.X, Y = e.Tile.Y,
                Hint = (int)e.Hint,
                State = (int)e.State,
                SeenTick = e.SeenTick,
                GoneTick = e.GoneTick,
            }).ToArray()
            : [];
        dto.VisionGrants = world.VisionGrants.Values.Where(g => g.OwnerId == playerId)
            .Select(g => new VisionGrantDto
            {
                X = g.Center.X, Y = g.Center.Y, Radius = g.Radius,
                TicksLeft = Math.Max(0, g.EndsTick - now),
            }).ToArray();

        // M44 — the viewer's own ore knowledge. Pure read; (y, x) order.
        dto.Veins = world.KnownVeins.TryGetValue(playerId, out var known)
            ? known.Select(t => new VeinDto { X = t.X, Y = t.Y, Mined = MineStandsOn(world, t) }).ToArray()
            : [];
        if (world.SurveyedBarren.TryGetValue(playerId, out var barren))
        {
            dto.BarrenX = barren.Select(t => t.X).ToArray();
            dto.BarrenY = barren.Select(t => t.Y).ToArray();
        }
    }

    private static bool MineStandsOn(GameWorld world, TileCoord tile) =>
        world.Structures.TryGetValue(tile, out var s)
        && (s.Kind == StructureKind.Mine
            || (s is ConstructionSite c && c.TargetKind == StructureKind.Mine));

    // M37 — the viewer's OWN omens (docs/progression.md): live ones, and the
    // outcome of any that ended within OmenDto.RecentTicks. Pure read. A
    // rumour's ruin tile stays off the wire (only its bearing is told).
    private static void FillOmens(ViewDto dto, GameWorld world, long now, int playerId)
    {
        var rows = new List<OmenDto>();
        foreach (var o in world.Omens.Values)
        {
            if (o.OwnerId != playerId) continue;
            if (!o.IsLive && now - o.ResolvedTick > OmenDto.RecentTicks) continue;
            var pending = o.State == Sim.Core.Progression.OmenState.Pending;
            // A rumour's ruin and a rumoured camp tell their direction and a
            // search circle, never their tile.
            var told = o.Kind is not (Sim.Core.Progression.OmenKind.Rumour or Sim.Core.Progression.OmenKind.Camp);
            // M39 — a camp's countdown is to its first raid.
            var counting = pending || (o.Kind == Sim.Core.Progression.OmenKind.Camp && o.IsLive && o.DueTick > now);
            rows.Add(new OmenDto
            {
                Id = o.OmenId,
                Kind = (int)o.Kind,
                State = (int)o.State,
                From = (int)o.From,
                Size = o.Kind == Sim.Core.Progression.OmenKind.Rumour ? 0 : o.Size,
                DueTick = counting ? o.DueTick : 0,
                TicksLeft = counting ? Math.Max(0, o.DueTick - now) : 0,
                TargetX = told ? o.Target.X : -1,
                TargetY = told ? o.Target.Y : -1,
                AreaX = o.AreaCenter.X,
                AreaY = o.AreaCenter.Y,
                AreaRadius = o.AreaRadius,
                Remaining = o.State == Sim.Core.Progression.OmenState.Arrived ? o.PartyIds.Count : 0,
                ResolvedTick = o.ResolvedTick,
            });
        }
        dto.Omens = rows.ToArray();
        dto.LockedKinds = Sim.Core.Progression.Progression.LockedKinds(world, playerId).Select(k => (int)k).ToArray();
    }

    // M36 — the viewer's OWN haul queue (in line order) and routes, with the
    // driver's last verdicts. Owner-only, like orders: logistics plans are
    // private strategy.
    private static void FillHauling(ViewDto dto, GameWorld world, long now, int playerId,
        Hauling.HaulingDriver? driver)
    {
        var queue = new HaulQueueDto { FreeHaulers = Hauling.HaulingDriver.CountFree(world, playerId) };
        var jobs = world.HaulJobs.Values.Where(j => j.OwnerId == playerId)
            .OrderBy(j => j.QueueStamp).ThenBy(j => j.JobId);
        var rows = new List<HaulJobDto>();
        foreach (var j in jobs)
        {
            var report = driver is not null && driver.Reports.TryGetValue(j.JobId, out var r) ? r : null;
            var row = new HaulJobDto
            {
                Id = j.JobId,
                SourceX = j.Source.X, SourceY = j.Source.Y,
                DestX = j.Dest.X, DestY = j.Dest.Y,
                Resource = (int)j.Resource,
                Kind = (int)j.Kind,
                Target = j.Target,
                Delivered = j.Delivered,
                WaitTicks = Math.Max(0, now - j.QueuedAtTick),
                State = report is null ? 0 : (int)report.State,
                Need = report?.Need ?? 0,
                OnTheWay = report?.OnTheWay ?? 0,
                Haulers = report?.Haulers ?? 0,
            };
            if (report?.State == Hauling.HaulJobState.WaitingForHauler)
            {
                queue.Waiting++;
                queue.LongestWaitTicks = Math.Max(queue.LongestWaitTicks, row.WaitTicks);
            }
            rows.Add(row);
        }
        queue.Jobs = rows.ToArray();
        dto.HaulQueue = queue;

        var crewStates = new Dictionary<(int, int), Hauling.RouteCrewReport>();
        if (driver is not null)
            foreach (var c in driver.CrewReports) crewStates[(c.RouteId, c.CrewId)] = c;
        dto.Groups = world.Groups.Values.Where(g => g.OwnerId == playerId).Select(g => ToGroupDto(world, g)).ToArray();
        dto.HaulRoutes = world.HaulRoutes.Values.Where(r => r.OwnerId == playerId)
            .Select(r => new HaulRouteDto
            {
                Id = r.RouteId,
                Name = r.Name,
                Stops = r.Stops.Select(st => new HaulStopDto
                {
                    X = st.Tile.X, Y = st.Tile.Y,
                    Rules = st.Rules.Select(rule => new HaulStopRuleDto
                    {
                        Resource = (int)rule.Resource, Op = (int)rule.Op, Percent = rule.Percent,
                    }).ToArray(),
                }).ToArray(),
                Crews = r.Crews.Select(c => new HaulCrewDto
                {
                    Id = c.CrewId,
                    Members = c.Members.ToArray(),
                    CurrentStop = c.CurrentStop,
                    Living = Sim.Core.Hauling.RouteCrews.Living(world, r, c).Count,
                    State = crewStates.TryGetValue((r.RouteId, c.CrewId), out var cr) ? (int)cr.State : 0,
                    LastStop = c.LastServe?.Stop ?? -1,
                    LastTick = c.LastServe?.Tick ?? -1,
                    LastLoaded = c.LastServe?.Loaded ?? 0,
                    LastUnloaded = c.LastServe?.Unloaded ?? 0,
                    LastNotes = (int)(c.LastServe?.Notes ?? Sim.Core.Hauling.ServeNote.None),
                }).ToArray(),
            }).ToArray();
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
                tiles.Add(new TileDto
                {
                    X = x, Y = y,
                    Biome = (int)BiomeDegradation.BiomeAt(world, tile, now, cfg),
                    Elevation = _elevation[x, y],
                    Baseline = BiomeDegradation.BaselineFertility(world, tile, cfg),
                });
            }

        var roads = new List<RoadDto>();
        foreach (var arc in world.Roads.Keys)
        {
            var cond = Road.ConditionAt(world, arc, now);
            if (cond > 0) roads.Add(RoadDtoFor(arc, cond));
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
            Structures = WithFootprints(world.Structures.Values.Select(s => ToStructDto(s, playerId, world, now)).ToArray(), world),
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
        // include any road LINK with an explored endpoint (not just currently visible)
        // with its live decayed condition via the pure-read ConditionAt. Iterating the
        // sparse Roads dict keeps this bounded by road count, not map size.
        var roads = new List<RoadDto>();
        foreach (var arc in world.Roads.Keys)
        {
            // A link is known when EITHER of its tiles is explored.
            if (!view.Explored.Contains(arc.A.Tile) && !view.Explored.Contains(arc.B.Tile)) continue;
            var cond = Road.ConditionAt(world, arc, now);
            if (cond > 0) roads.Add(RoadDtoFor(arc, cond));
        }

        return new ViewDto
        {
            PlayerId = playerId,
            Width = _map.Width,
            Height = _map.Height,
            WaterLevel = _waterLevel,
            // M35 — Baseline is the tile's environmental fertility baseline
            // (docs/environmental-fertility.md), the number the brains site
            // farms and camps by. Emitted LIVE for remembered tiles too: the
            // genesis terrain it derives from is public on the v2 genesis
            // payload anyway, so the only thing a remembered tile could leak
            // is a canal lift the player hasn't seen — accepted, documented.
            Visible = view.Visible
                .Select(t => new TileDto
                {
                    X = t.X, Y = t.Y,
                    Biome = (int)BiomeDegradation.BiomeAt(world, t, now, cfg),
                    Elevation = _elevation[t.X, t.Y],
                    Baseline = BiomeDegradation.BaselineFertility(world, t, cfg),
                })
                .ToArray(),
            Remembered = view.RememberedTerrain
                .Select(kv => new TileDto
                {
                    X = kv.Key.X, Y = kv.Key.Y,
                    Biome = (int)kv.Value,
                    Elevation = _elevation[kv.Key.X, kv.Key.Y],
                    Baseline = BiomeDegradation.BaselineFertility(world, kv.Key, cfg),
                })
                .ToArray(),
            Units = view.VisibleUnits.Select(u => ToUnitDto(u, playerId, world, now)).ToArray(),
            Structures = WithFootprints(view.VisibleStructures.Select(s => ToStructDto(s, playerId, world, now)).ToArray(), world),
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
            MaxHealth = mine ? Sim.Core.Combat.CombatRules.MaxHealth(u, now) : -1,
            Resting = mine && Sim.Core.Healing.Rest.IsResting(world, u, now),
            Activity = mine ? (int)u.Activity : -1,
            PassengerCap = mine ? u.PassengerCap : 0,
            Passengers = mine ? u.Passengers.Count : 0,
            CargoResource = mine ? (int)u.CargoResource : 0,
            CargoAmount = mine ? u.CargoAmount : 0,
            Cargo = mine ? CargoItems(u) : Array.Empty<CargoItemDto>(),
            // Loadout is private military info — same rule as Activity.
            // EffectivePower is a pure read.
            Power = mine ? Sim.Core.Combat.CombatRules.EffectivePower(world, u, now) : -1,
            Buffs = mine ? u.Buffs.Select(b => b.Kind).ToArray() : Array.Empty<string>(),
            DestX = dest?.X ?? -1,
            DestY = dest?.Y ?? -1,
            // Private like Activity: you command your own formations, you do not
            // read the enemy's order of battle off the map.
            GroupId = mine ? u.GroupId ?? -1 : -1,
            GroupState = mine ? GroupStateOf(u, world) : 0,
            SavedTaskKind = mine ? (int)(u.SavedTask?.Kind ?? 0) : 0,
            SavedTaskX = mine ? u.SavedTask?.TargetTile.X ?? -1 : -1,
            SavedTaskY = mine ? u.SavedTask?.TargetTile.Y ?? -1 : -1,
            // M31 — crown / heir tag. Derived, own units only.
            Royal = mine ? RoyalTagOf(u, world) : 0,
            Settled = mine && Sim.Core.Population.Housing.IsSettled(world, u, now),
            // M30 — the pending-goal tag. Own units only, same rule as Activity.
            GoalKind = mine ? (int)(u.Goal?.Kind ?? 0) : 0,
            GoalState = mine ? GoalStateOf(u, world) : "",
            GoalX = mine ? u.Goal?.TargetTile.X ?? -1 : -1,
            GoalY = mine ? u.Goal?.TargetTile.Y ?? -1 : -1,
        };
        if (mine) { FillPursuit(dto, u); FillRoute(dto, u); FillSurvey(dto, u); FillHaul(dto, u); }
        FillSubtile(dto, u);
        FillSubtileStep(dto, u, world);
        return dto;
    }

    // P3 — the chase, own units only (the caller gates). Leaves the -1 defaults
    // when the unit is not pursuing anyone.
    // M36 — every resource aboard, in enum order (CargoHold is sorted).
    private static CargoItemDto[] CargoItems(Unit u) =>
        u.Cargo.Items.Select(kv => new CargoItemDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();

    // M42 — the subtile a unit stands on (public, physical).
    private static void FillSubtile(UnitDto dto, Unit u)
    {
        if (u.Subtile is not { } s) return;
        dto.SubX = s.X;
        dto.SubY = s.Y;
    }

    // M42/M43 — the subtile step in flight: the walk's next step, the tick it lands and how long
    // it takes. Public, like the hop it replaced: which way something is stepping is visible.
    private static void FillSubtileStep(UnitDto dto, Unit u, GameWorld world)
    {
        if (u.Subtile is not { } sub || u.SubtileRouteTick is not { } arrive || u.SubtileRoute is not { Count: > 0 } route) return;
        var here = Sim.Core.Battlefields.WorldSubtile.Of(u.Position, sub);
        var next = route[0];
        var total = Sim.Core.Battlefields.SubtileRoutes.StepCost(world, u, here, next, arrive);
        if (!here.IsAdjacentTo(next) || total <= 0) return;
        dto.SubStepX = next.X;
        dto.SubStepY = next.Y;
        dto.SubStepArriveTick = arrive;
        dto.SubStepTotalTicks = (int)total;
    }

    // M42 — the unit's drawn subtile route, own units only (the caller gates).
    private static void FillRoute(UnitDto dto, Unit u)
    {
        if (u.SubtileRoute is not { Count: > 0 } route) return;
        dto.RouteX = route.Select(r => r.X).ToArray();
        dto.RouteY = route.Select(r => r.Y).ToArray();
    }

    // M45 — the unit's live load and its named route. Own units only (callers gate).
    private static void FillHaul(UnitDto dto, Unit u)
    {
        dto.CargoCapacity = u.CargoCapacity;
        dto.HaulRouteId = u.RouteId ?? -1;
    }

    // M44 — the survey tag. Own units only (callers gate).
    private static void FillSurvey(UnitDto dto, Unit u)
    {
        if (u.Survey is not { } plan) return;
        dto.SurveyX = plan.Target.X;
        dto.SurveyY = plan.Target.Y;
        dto.SurveyDoneTick = plan.CompleteTick ?? -1;
    }

    private static void FillPursuit(UnitDto dto, Unit u)
    {
        if (u.Pursuit is not { } p) return;
        dto.PursuitTargetId = p.TargetUnitId;
        dto.PursuitLeashX = p.LeashTile.X;
        dto.PursuitLeashY = p.LeashTile.Y;
        dto.PursuitLeashRadius = p.LeashRadius;
    }

    // M31 — 1 = the reigning monarch, 2 = the heir-apparent, 0 = neither.
    //
    // The heir lookup is a scan over the owner's units, so it runs ONCE per
    // projected unit only because the alternative (precomputing per player)
    // would put mutable cache state on a pure-read path. Royal lines are tiny
    // and unit counts are in the hundreds; if this ever shows up in a profile,
    // the fix is a per-projection local, not a stored field.
    private static int RoyalTagOf(Unit u, GameWorld world)
    {
        if (Sim.Core.Royalty.Royalty.IsKing(world, u)) return 1;
        if (Sim.Core.Royalty.Royalty.HeirApparent(world, u.OwnerId)?.Id == u.Id) return 2;
        return 0;
    }

    // M30 — what a pending goal is DOING right now, in the words the visibility
    // contract asks for: "en route" while the body is still walking, and
    // "waiting: <precondition>" once it has arrived and stalled. The two read
    // differently on purpose — a stalled goal is waiting on something the
    // player controls, and that is the one the player may want to act on.
    private static string GoalStateOf(Unit u, GameWorld world)
    {
        // M44 — a survey is an errand too, and says so: walking to the slope,
        // then digging (never "waiting" — the dig is progress, not a stall).
        if (u.Goal is null && u.Survey is { } survey)
            return survey.CompleteTick is null
                ? $"en route to survey ({survey.Target.X},{survey.Target.Y})"
                : $"surveying ({survey.Target.X},{survey.Target.Y})";
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

    // M46 — one of the viewer's groups, with its muster's state of play. Pure read.
    private static GroupDto ToGroupDto(GameWorld world, Sim.Core.Groups.Group g)
    {
        var progress = Sim.Core.Groups.GroupMuster.Progress(world, g);
        return new GroupDto
        {
            Id = g.Id,
            Name = g.Name,
            Kind = (int)g.Kind,
            ParentId = g.ParentId ?? -1,
            Children = g.Children.ToArray(),
            Members = g.Members.ToArray(),
            State = (int)g.State,
            X = g.Position.X,
            Y = g.Position.Y,
            DestX = g.PathFinalDest?.X ?? -1,
            DestY = g.PathFinalDest?.Y ?? -1,
            Here = progress.Here,
            OnTheWay = progress.OnTheWay,
            NoRoom = progress.NoRoom,
            Finishing = progress.Finishing.Select(f => new GroupFinishingDto { UnitId = f.UnitId, Why = f.Why }).ToArray(),
        };
    }

    // The state of the group a unit belongs to, or 0 when it is in none. Pure read.
    private static int GroupStateOf(Unit u, GameWorld world) =>
        u.GroupId is { } gid && world.Groups.TryGetValue(gid, out var g) ? (int)g.State : 0;

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
        var cargoRes = 0; var cargoAmt = 0; var cargo = Array.Empty<CargoItemDto>();
        var power = -1;
        var buffs = Array.Empty<string>();
        var destX = -1; var destY = -1;
        var groupId = -1;
        // M30 — the visibility contract, on the path clients actually use.
        var goalKind = 0; var goalState = ""; var goalX = -1; var goalY = -1;
        var groupState = 0;
        var savedKind = 0; var savedX = -1; var savedY = -1;
        var royal = 0;
        var settled = false;
        var maxHealth = -1;
        var resting = false;
        // The hop is public, so the real unit is looked up for EVERY visible unit,
        // not only the viewer's own. The own-only enrichment stays inside the branch.
        world.Units.TryGetValue(uv.Id, out var live);
        if (uv.OwnerId == viewerPlayerId && world.Units.TryGetValue(uv.Id, out var real))
        {
            groupId = real.GroupId ?? -1;
            groupState = GroupStateOf(real, world);
            if (real.SavedTask is { } saved) { savedKind = (int)saved.Kind; savedX = saved.TargetTile.X; savedY = saved.TargetTile.Y; }
            royal = RoyalTagOf(real, world);
            settled = Sim.Core.Population.Housing.IsSettled(world, real, now);
            maxHealth = Sim.Core.Combat.CombatRules.MaxHealth(real, now);
            resting = Sim.Core.Healing.Rest.IsResting(world, real, now);
            goalKind = (int)(real.Goal?.Kind ?? 0);
            goalState = GoalStateOf(real, world);
            goalX = real.Goal?.TargetTile.X ?? -1;
            goalY = real.Goal?.TargetTile.Y ?? -1;
            activity = (int)real.Activity;
            cap = real.PassengerCap;
            pax = real.Passengers.Count;
            cargoRes = (int)real.CargoResource;
            cargoAmt = real.CargoAmount;
            cargo = CargoItems(real);
            power = Sim.Core.Combat.CombatRules.EffectivePower(world, real, now);
            buffs = real.Buffs.Select(b => b.Kind).ToArray();
            if (FinalDestOf(real, world) is { } dest) { destX = dest.X; destY = dest.Y; }
        }
        var dto2 = new UnitDto
        {
            Id = uv.Id, X = uv.Position.X, Y = uv.Position.Y, Role = (int)uv.Role, OwnerId = uv.OwnerId,
            Age = uv.AgeYears,
            Health = uv.OwnerId == viewerPlayerId ? uv.Health : -1,
            MaxHealth = maxHealth,
            Resting = resting,
            Activity = activity,
            PassengerCap = cap,
            Passengers = pax,
            CargoResource = cargoRes,
            CargoAmount = cargoAmt,
            Cargo = cargo,
            Power = power,
            Buffs = buffs,
            DestX = destX,
            DestY = destY,
            GroupId = groupId,
            GroupState = groupState,
            SavedTaskKind = savedKind,
            SavedTaskX = savedX,
            SavedTaskY = savedY,
            Royal = royal,
            Settled = settled,
            GoalKind = goalKind,
            GoalState = goalState,
            GoalX = goalX,
            GoalY = goalY,
        };
        if (uv.OwnerId == viewerPlayerId && live is not null) { FillPursuit(dto2, live); FillRoute(dto2, live); FillSurvey(dto2, live); FillHaul(dto2, live); }
        if (live is not null) { FillSubtile(dto2, live); FillSubtileStep(dto2, live, world); }
        return dto2;
    }

    // P2 — a combat row with its siege state. A pure read mirroring the damage
    // rule in FortSiege.TryResolveFortRound (Sim.Core is not touched; the sum is
    // recomputed here, so a change there must be echoed here — WireV2Tests pins
    // the equality against CombatRules.EffectivePower). Field battles carry 0/0/0.
    private static CombatDto ToCombatDto(CombatView c, GameWorld world, long now)
    {
        var dto = new CombatDto
        {
            X = c.Tile.X, Y = c.Tile.Y,
            RoundNumber = c.RoundNumber, NextRoundTick = c.NextRoundTick,
            Sides = SidesOn(c.Tile, world, now),
        };
        if (!world.Structures.TryGetValue(c.Tile, out var fort)
            || !Sim.Core.Fortifications.Fortification.IsStandingFortification(fort))
            return dto;

        dto.FortKind = (int)fort.Kind;
        var diplomacy = world.Diplomacy;
        foreach (var u in world.Units.Values)
        {
            if (u.IsEmbarked) continue;
            if (u.OwnerId == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
            if (!diplomacy.AreHostile(u.OwnerId, fort.OwnerId)) continue;
            var dx = Math.Abs(u.Position.X - c.Tile.X);
            var dy = Math.Abs(u.Position.Y - c.Tile.Y);
            if (dx + dy > 1) continue;   // on the tile or a 4-neighbour
            dto.Besiegers++;
            dto.SiegePower += Sim.Core.Combat.CombatRules.EffectivePower(world, u, now);
        }
        return dto;
    }

    // P3 — per-owner headcount and power for the units standing on `tile`.
    // Ordered by owner id; embarked passengers do not fight (M12) and are not
    // counted, matching CombatRules.ForcePower.
    private static CombatSideDto[] SidesOn(TileCoord tile, GameWorld world, long now)
    {
        var sides = new SortedDictionary<int, CombatSideDto>();
        foreach (var u in world.Units.Values)
        {
            if (u.Position != tile || u.IsEmbarked) continue;
            if (!sides.TryGetValue(u.OwnerId, out var side))
                sides[u.OwnerId] = side = new CombatSideDto { OwnerId = u.OwnerId };
            side.Units++;
            side.Power += Sim.Core.Combat.CombatRules.EffectivePower(world, u, now);
        }
        return sides.Values.ToArray();
    }

    // Every kind's footprint facing North, joining nothing: what the build
    // preview turns (docs/structure-footprints.md). Static reference data.
    private static FootprintPatternDto[] FootprintCatalog() =>
        Enum.GetValues<StructureKind>()
            .Select(k => (Kind: k, Layer: Sim.Core.Battlefields.Footprints.Pattern(k)))
            .Where(p => p.Layer is not null)
            .Select(p => new FootprintPatternDto { Kind = (int)p.Kind, Footprint = FootprintDto.Of(p.Layer!) })
            .ToArray();

    // Structure footprints (docs/structure-footprints.md): each structure's
    // facing and resolved layout, joined only to neighbours in this same list
    // (what the viewer can see), so a wall's shape never gives away a wall in
    // the fog. A remembered structure that has since changed kind is left bare.
    private static StructDto[] WithFootprints(StructDto[] dtos, GameWorld world)
    {
        var known = new HashSet<TileCoord>(dtos.Select(d => new TileCoord(d.X, d.Y)));
        foreach (var d in dtos)
        {
            if (!world.Structures.TryGetValue(new TileCoord(d.X, d.Y), out var s) || (int)s.Kind != d.Kind) continue;
            d.Facing = (int)Sim.Core.Battlefields.Footprints.FacingOf(s);
            var layer = Sim.Core.Battlefields.Footprints.For(world, s, known.Contains);
            if (layer.IsOpen) continue;
            d.HasFootprint = true;
            d.Footprint = FootprintDto.Of(layer);
        }
        return dtos;
    }

    // Reveal path: the real Structure is in hand.
    private static StructDto ToStructDto(Structure s, int viewerPlayerId, GameWorld world, long now)
    {
        var dto = new StructDto { X = s.At.X, Y = s.At.Y, Kind = (int)s.Kind, OwnerId = s.OwnerId };
        if (s.OwnerId == viewerPlayerId) EnrichOwned(dto, s, world, now);
        FillClaims(dto, s);
        FillCacheLoot(dto, s);
        FillHealth(dto, s, viewerPlayerId);
        return dto;
    }

    // P2 — health visibility (docs/siege-visibility.md). Own: always exact. Any
    // visible fortification: exact, whoever owns it — the besieger must see what
    // they are breaching. Other enemy kinds: private (-1/-1, the DTO default).
    // Indestructible kinds (catalog BaseHealth 0): 0/0, nothing to hide.
    private static void FillHealth(StructDto dto, Structure s, int viewerPlayerId)
    {
        var max = StructureCatalog.Spec(s.Kind).BaseHealth;
        if (max <= 0) { dto.Health = 0; dto.MaxHealth = 0; return; }
        if (s.OwnerId != viewerPlayerId && !IsFortificationKind(s.Kind)) return;
        dto.Health = s.Health;
        dto.MaxHealth = max;
    }

    // The kinds whose health is public. Broader than Fortification.IsStandingFortification
    // (which is "blocks movement and still stands"): a Tower and a Castle do not
    // block, but they are what a siege is FOR, so their state is public too.
    // M39 — and a bandit camp: burning it IS a siege (docs/bandit-camps.md).
    private static bool IsFortificationKind(StructureKind kind) => kind is
        StructureKind.Wall or StructureKind.Gate or StructureKind.Tower or StructureKind.Castle
        or StructureKind.BanditCamp;

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
            FillHealth(dto, real, viewerPlayerId);
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
                dto.HeldSlots = ex.HeldBy.Count;   // EnrichOwned: the owner's own
                // Analytics: the rate the tick would spend, per day. Pure read.
                if (ex.Spec.OutputResource != Resource.None)
                {
                    dto.OutputPerDay = (int)Math.Min(int.MaxValue,
                        Sim.Core.Logistics.ProductionRate.PerDay(world, ex, now));
                    dto.Producing = ex.TickArmed;
                }
                {
                    // Output buffer first, then (refiners) the input store —
                    // the smelter's ore and fuel on hand. Empty for ordinary
                    // extractors, so their payload is unchanged.
                    var held = new List<ResAmtDto>();
                    if (ex.Buffer > 0 && ex.Spec.OutputResource != Resource.None)
                        held.Add(new ResAmtDto { Resource = (int)ex.Spec.OutputResource, Amount = ex.Buffer });
                    foreach (var (r, amt) in ex.Inputs)
                        held.Add(new ResAmtDto { Resource = (int)r, Amount = amt });
                    if (held.Count > 0) dto.Holdings = held.ToArray();
                }
                // Soil visibility (own-only): live fertility per claim
                // tile, parallel to the ClaimX/ClaimY arrays FillClaims
                // emits (same source list, same order). Pure read.
                // M44 — only for kinds that WEAR their land: a quarry's hills
                // never change, so there is no soil to read (and the client's
                // soil ladder would misgrade off-ladder Hills).
                if (ex.ClaimTiles.Count > 0 && ex.Spec.DegradeAmount > 0)
                {
                    dto.ClaimFertility = ex.ClaimTiles
                        .Select(t => Sim.Core.Biomes.BiomeDegradation.FertilityAt(
                            world, t, now, world.BiomeDegradationConfig))
                        .ToArray();
                    // M35 — each claim tile's environmental BASELINE beside
                    // its live reading, so "how worn is this field" is
                    // (Baseline - Fertility) rather than a guess against a
                    // flat 5000 (docs/environmental-fertility.md decision 5).
                    dto.ClaimBaseline = ex.ClaimTiles
                        .Select(t => Sim.Core.Biomes.BiomeDegradation.BaselineFertility(
                            world, t, world.BiomeDegradationConfig))
                        .ToArray();
                }
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
                dto.EatsPerDay = FoodConsumption.DemandPerDay(world, h);
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
                dto.EatsPerDay = FoodConsumption.DemandPerDay(world, castle);
                // Residents is left 0: the castle feeds everyone without a house,
                // and the realm panel's Population already carries that count.
                break;

            case StorageStructure ss:
                dto.Capacity = ss.Capacity;
                dto.Holdings = ss.Holdings.Select(kv => new ResAmtDto { Resource = (int)kv.Key, Amount = kv.Value }).ToArray();
                break;
        }
    }

    private static RoadDto RoadDtoFor(SubtileLink arc, int condition) =>
        new() { X = arc.A.X, Y = arc.A.Y, Axis = (int)arc.Direction, Condition = condition };
}
