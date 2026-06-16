namespace Sim.Core.World;

// Hand-authored spec table. Numbers are placeholders — they'll get tuned once
// the production + haul loop runs and we can feel the pacing. The shape stays.
public static class StructureCatalog
{
    private static readonly Dictionary<StructureKind, StructureSpec> Specs = new()
    {
        [StructureKind.Castle] = new StructureSpec
        {
            Kind = StructureKind.Castle,
            IsPlayerBuildable = false,   // placed at genesis only
            StorageCapacity = 5000,
        },
        [StructureKind.Stockpile] = new StructureSpec
        {
            Kind = StructureKind.Stockpile,
            IsPlayerBuildable = true,
            StorageCapacity = 500,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 20 },
            BuildDurationTicks = 20 * Time.Hour,
            RequiredBuilderCount = 1,
        },
        [StructureKind.LumberCamp] = new StructureSpec
        {
            Kind = StructureKind.LumberCamp,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Forest,
            OutputResource = Resource.Wood,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 30 * Time.Minute,
            WorkerCap = 3,
            BufferCap = 30,
            PreferredRole = UnitRole.Lumberjack,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 10 },
            BuildDurationTicks = 10 * Time.Hour,
            RequiredBuilderCount = 1,
            // M9 — degrades at 2 fertility per DegradePeriod (logging
            // strips land faster than farming exhausts it). Forest (10000)
            // crosses below ForestThreshold 7500 after ~1250 hourly periods
            // ≈ 52 days of continuous production, then snaps to Grassland —
            // a durable but REVERSIBLE loss (~208 days of rest to regrow).
            // M15 — the loss lands on the camp's 6 CLAIMED forest tiles
            // (docs/extraction-claims.md), not a radius.
            DegradeAmount = 2,
            ClaimCount = 8,
            ClaimRange = 2,
        },
        [StructureKind.Quarry] = new StructureSpec
        {
            Kind = StructureKind.Quarry,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Mountain,
            OutputResource = Resource.Stone,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 12 * Time.Hour,
            WorkerCap = 3,
            BufferCap = 20,
            PreferredRole = UnitRole.Quarryman,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 15 },
            BuildDurationTicks = 15 * Time.Hour,
            RequiredBuilderCount = 2,
        },
        [StructureKind.Mine] = new StructureSpec
        {
            Kind = StructureKind.Mine,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Hills,
            OutputResource = Resource.Ore,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 1 * Time.Day,
            WorkerCap = 3,
            BufferCap = 20,
            PreferredRole = UnitRole.Miner,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 15,
                [Resource.Stone] = 10,
            },
            BuildDurationTicks = 25 * Time.Hour,
            RequiredBuilderCount = 2,
        },
        [StructureKind.Farm] = new StructureSpec
        {
            Kind = StructureKind.Farm,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Grassland,
            OutputResource = Resource.Food,
            // 2026-06-11 retune (the M17 balance lab caught the old 2/1h
            // feeding 72 mouths per farm — food was solved by one build).
            // 1 per 2h × 2:1 farmer bonus × 3 workers = 72 food/game-day
            // = 18 mouths per farm: a farmer feeds 6, a growing town needs
            // a GROWING number of farms, and the M15 claim burn becomes a
            // real rotation economy instead of a chore.
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 2 * Time.Hour,
            WorkerCap = 3,
            BufferCap = 40,
            PreferredRole = UnitRole.Farmer,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 10 },
            BuildDurationTicks = 10 * Time.Hour,
            RequiredBuilderCount = 1,
            // M9 — Farm drives Grassland into PERMANENT Desert (latch fires
            // when current fertility crosses below DesertThreshold 2500).
            // 1 fertility per hourly DegradePeriod × Grassland headroom
            // (5000→2499 = ~2500 points) → ~104 days (~3.5 game-months) of
            // continuous production make a fresh Grassland tile permanently
            // dead. The PERMANENCE is the punishment (LumberCamp's
            // Forest→Grassland is reversible; this is not). M15 — the
            // damage lands on the farm's 4 CLAIMED grassland tiles
            // (docs/extraction-claims.md); rotating farmland is the long
            // game.
            DegradeAmount = 1,
            ClaimCount = 15,
            ClaimRange = 2,
        },
        [StructureKind.ConstructionSite] = new StructureSpec
        {
            Kind = StructureKind.ConstructionSite,
            // Transient internal state; no player intent targets it directly.
        },
        [StructureKind.Tower] = new StructureSpec
        {
            Kind = StructureKind.Tower,
            IsPlayerBuildable = true,
            // No biome requirement — towers can go anywhere.
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 20,
                [Resource.Stone] = 10,
            },
            BuildDurationTicks = 30 * Time.Hour,
            RequiredBuilderCount = 1,
            // Vision contribution is read from Sight.RadiusFor — not duplicated here.
        },
        [StructureKind.House] = new StructureSpec
        {
            Kind = StructureKind.House,
            IsPlayerBuildable = true,
            // Holdings cap kept small; the House is a breeding gate and
            // (M19) its residents' larder, not a general food store —
            // days of local food, not seasons. The castle is the deep
            // reserve.
            StorageCapacity = 100,
            // M19 — beds (user, 2026-06-12): five mouths call a house
            // home; the cap shapes expansion (more people → more houses
            // → more neighborhoods to stock and defend). Never blocks
            // breeding — overflow newborns home at the next free bed,
            // castle fallback.
            ResidentCap = 5,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 30 },
            BuildDurationTicks = 30 * Time.Hour,
            RequiredBuilderCount = 1,
        },
        // Training — School. A placeable seam where TrainUnitIntent
        // resolves. No production, no storage. Cheap-ish: training is a
        // capital investment more than an ongoing one.
        [StructureKind.School] = new StructureSpec
        {
            Kind = StructureKind.School,
            IsPlayerBuildable = true,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 80 },
            BuildDurationTicks = 80 * Time.Minute,
            RequiredBuilderCount = 1,
        },
        // Military — Barracks. Trains Soldier/Archer (RoleTrainerCatalog
        // routes military roles here, civilian roles to the School) and
        // crafts equipment (CraftEquipmentIntent consumes raw resources
        // from its own holdings). Storage holds craft inputs + finished
        // weapons; haul materials in, haul weapons out.
        // docs/military-training.md + docs/equipment-model.md.
        [StructureKind.Barracks] = new StructureSpec
        {
            Kind = StructureKind.Barracks,
            IsPlayerBuildable = true,
            StorageCapacity = 200,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 100,
                [Resource.Stone] = 20,
            },
            BuildDurationTicks = 100 * Time.Minute,
            RequiredBuilderCount = 1,
        },
        // M20 — Lodge. The intelligence structure: a placeable seam (like the
        // School) whose completed presence gates DispatchScoutIntent. No
        // production, no storage. Mid-cost — reconnaissance is a capital
        // investment that unlocks a whole automation surface.
        [StructureKind.Lodge] = new StructureSpec
        {
            Kind = StructureKind.Lodge,
            IsPlayerBuildable = true,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 60,
                [Resource.Stone] = 20,
            },
            BuildDurationTicks = 50 * Time.Minute,
            RequiredBuilderCount = 1,
        },
        // M12 — Dock. Expensive: a long write-down up front that pays
        // off forever in fast water travel (the design contract from
        // docs/boats.md). Phase C wires boat production from this
        // structure to the slip tile. ProductionPeriodTicks doubles as
        // the boat-production cadence.
        [StructureKind.Dock] = new StructureSpec
        {
            Kind = StructureKind.Dock,
            IsPlayerBuildable = true,
            // No RequiredBiome: PlaceSiteIntent does the dock-specific
            // "land tile with adjacent water" validation directly.
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 200,
                [Resource.Stone] = 50,
            },
            BuildDurationTicks = 10 * Time.Day,
            RequiredBuilderCount = 2,
            // M12 — boat-production cadence. One boat per 5 game-hours
            // while the slip is free; stalls when slip is occupied.
            ProductionPeriodTicks = 10 * Time.Day,
        },
        // M21 — Canal. A terrain-mutation build job: it converts a chosen
        // PATH of land tiles into Water (docs/canals.md). The cost and build
        // time below are PER DUG TILE — PlaceCanalIntent multiplies both by
        // the path length, so a long canal is a proportionally huge
        // investment ("a real investment" — the user). Stone-heavy (digging
        // and lining the channel) with timber for shoring; three builders.
        // A finished canal leaves NO structure — the tiles simply become
        // Water (BuildCompleteEvent's canal branch). IsPlayerBuildable is
        // true so the ConstructionSite ctor accepts it, but PlaceSiteIntent
        // rejects Canal — the whole-path validation lives in PlaceCanalIntent.
        // No RequiredBiome: the intent does its own diggable-land checks.
        [StructureKind.Canal] = new StructureSpec
        {
            Kind = StructureKind.Canal,
            IsPlayerBuildable = true,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Stone] = 150,
                [Resource.Wood] = 50,
            },
            BuildDurationTicks = 1 * Time.Day,
            RequiredBuilderCount = 3,
        },
        // M23 — Cache. An unowned loot container scattered in the fog at
        // genesis (CacheScatter); never player-buildable. StorageCapacity is a
        // stable ceiling for any rolled loot bundle (the snapshot drift check
        // pins it), not a gameplay number — keep it comfortably above the
        // max loot a CacheConfig can roll.
        [StructureKind.Cache] = new StructureSpec
        {
            Kind = StructureKind.Cache,
            IsPlayerBuildable = false,
            StorageCapacity = 1000,
        },
    };

    public static StructureSpec Spec(StructureKind kind) =>
        Specs.TryGetValue(kind, out var s)
            ? s
            : throw new KeyNotFoundException($"No spec for {kind}");

    public static bool TryGetSpec(StructureKind kind, out StructureSpec spec) =>
        Specs.TryGetValue(kind, out spec!);
}
