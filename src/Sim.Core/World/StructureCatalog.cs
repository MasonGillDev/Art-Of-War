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
            // M24 — siege HP. The seat-of-power figure: razing it costs an
            // attacker a long sustained presence (no defender shielding) and
            // razing it ends the owner. See docs/sieges-and-conquest.md.
            BaseHealth = 1000,
            // Rest healing (docs/unit-healing.md): the wounded heal at home.
            Shelters = true,
        },
        [StructureKind.Stockpile] = new StructureSpec
        {
            Kind = StructureKind.Stockpile,
            IsPlayerBuildable = true,
            StorageCapacity = 500,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 20 },
            BuildDurationTicks = 20 * Time.Hour,
            RequiredBuilderCount = 1,
            BaseHealth = 100,
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
            // M15 — the loss lands on the camp's 8 CLAIMED forest tiles
            // (docs/extraction-claims.md), not a radius.
            DegradeAmount = 2,
            ClaimCount = 8,
            ClaimRange = 2,
            BaseHealth = 50,
        },
        [StructureKind.Quarry] = new StructureSpec
        {
            Kind = StructureKind.Quarry,
            IsPlayerBuildable = true,
            // M44 — stone comes from the hills (docs/stone-and-ore-land.md).
            RequiredBiome = Biome.Hills,
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
            // M44 — a quarry takes up hill land the way a farm takes up
            // grassland: 6 claimed Hills tiles, exclusive across all owners
            // and kinds. DegradeAmount stays 0 — the hillside never wears
            // out; stone is slow, not scarce.
            ClaimCount = 6,
            ClaimRange = 2,
            BaseHealth = 50,
        },
        [StructureKind.Mine] = new StructureSpec
        {
            Kind = StructureKind.Mine,
            IsPlayerBuildable = true,
            // M44 — ore comes from mountain veins (docs/stone-and-ore-land.md):
            // only on a vein the placing faction has surveyed (or seen mined).
            RequiredBiome = Biome.Mountain,
            RequiresVein = true,
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
            BaseHealth = 50,
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
            // damage lands on the farm's 15 CLAIMED grassland tiles
            // (docs/extraction-claims.md); rotating farmland is the long
            // game.
            DegradeAmount = 1,
            ClaimCount = 15,
            ClaimRange = 2,
            BaseHealth = 50,
        },
        [StructureKind.ConstructionSite] = new StructureSpec
        {
            Kind = StructureKind.ConstructionSite,
            // Transient internal state; no player intent targets it directly.
            // M24 — an unfinished build is fragile (you're attacking
            // scaffolding, not walls). A few attackers level it.
            BaseHealth = 25,
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
            BaseHealth = 150,
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
            BaseHealth = 50,
            Shelters = true,   // rest healing (docs/unit-healing.md)
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
            BaseHealth = 75,
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
            BaseHealth = 200,
            Shelters = true,   // rest healing (docs/unit-healing.md)
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
            BaseHealth = 75,
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
            // M28 — the quay warehouse: freight staged between land haulers
            // and boats. Sized between the Barracks (200) and the Stockpile
            // (500) — a transshipment buffer, not a second castle.
            StorageCapacity = 400,
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
            BaseHealth = 75,
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
            // M28 retune (2026-07-07): per-tile stone cut 150 -> 50 so a canal
            // is a MID-game tool, not a late-game-only megaproject. A single
            // quarry (~8-12 stone/day) could never fund 150/tile against a war
            // + wall economy, so canals were never dug (observed live: 8
            // rivals, 0 canals in 150 days). At 50/tile a 2-tile canal is 100
            // stone — affordable in a week or two. Still a real investment
            // (wood + 3 builders + build time), just a reachable one. Applies
            // to the human game too (the user's call).
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Stone] = 50,
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
        // M39 — a bandit camp. Never built by players; raised by progression.
        // StorageCapacity is a ceiling for the hoard (raids stop well below it,
        // at CampConfig.HoardCap). Health 300: 17 h of uncontested siege for six
        // bare soldiers, 8 h for six swordsmen (docs/bandit-camps.md).
        [StructureKind.BanditCamp] = new StructureSpec
        {
            Kind = StructureKind.BanditCamp,
            IsPlayerBuildable = false,
            StorageCapacity = 1000,
            BaseHealth = 300,
        },
        // M38 — Idol. Scattered in the genesis fog (IdolScatter), never built,
        // never besieged (BaseHealth 0). See docs/scouting-secrets.md.
        [StructureKind.Idol] = new StructureSpec
        {
            Kind = StructureKind.Idol,
            IsPlayerBuildable = false,
        },
        // M24 — Rubble. The destroyed-structure tile occupant produced when a
        // structure's Health hits 0 (CombatRoundEvent). Indestructible
        // (BaseHealth = 0), no production, no holdings — its only job is
        // OCCUPYING THE TILE so PlaceSiteIntent / PlaceCanalIntent reject any
        // attempt to build on top of the wreckage. See
        // docs/sieges-and-conquest.md.
        //
        // M26 — rubble is CLEARABLE: ClearRubbleIntent swaps the pile for an
        // ordinary ConstructionSite targeting Rubble — a materials-free
        // labor job whose completion leaves the tile EMPTY (reclaimed
        // ground; the BuildCompleteEvent clearing branch). The build fields
        // below price that job: no materials, one laborer, a day of work.
        // IsPlayerBuildable=true only so the ConstructionSite ctor accepts
        // the target — PlaceSiteIntent rejects Rubble explicitly (you clear
        // wreckage, you don't build it).
        [StructureKind.Rubble] = new StructureSpec
        {
            Kind = StructureKind.Rubble,
            IsPlayerBuildable = true,
            BuildDurationTicks = 1 * Time.Day,
            RequiredBuilderCount = 1,
        },
        // M26 — Wall. One segment of a defensive line; placed ONLY via
        // PlaceWallIntent (PlaceSiteIntent rejects it — the line intent is
        // the single entry point, like Canal). Cost/time below are PER
        // SEGMENT; the line intent expands into N independent sites, so a
        // long wall is priced by honest multiplication, not scaling.
        // Stone-heavy, tough (between Barracks 200 and Castle 1000): razing
        // a segment is a real siege, and the Rubble it leaves is a permanent
        // breach (no rubble clearing yet). docs/walls-and-gates.md.
        // A deck across a straight canal tile (docs/structure-footprints.md,
        // 2026-09-28): the one way feet cross a canal; boats still pass under.
        // Built ON the canal (PlaceSiteIntent's bridge rule); razed or
        // demolished, it goes back to plain canal, not rubble. Numbers are a
        // first cut for tuning (user): timber-heavy, a day's work, as tough as
        // a barracks.
        [StructureKind.Bridge] = new StructureSpec
        {
            Kind = StructureKind.Bridge,
            IsPlayerBuildable = true,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 40,
                [Resource.Stone] = 10,
            },
            BuildDurationTicks = 24 * Time.Hour,
            RequiredBuilderCount = 1,
            BaseHealth = 200,
        },
        [StructureKind.Wall] = new StructureSpec
        {
            Kind = StructureKind.Wall,
            IsPlayerBuildable = true,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Stone] = 20,
                [Resource.Wood] = 5,
            },
            BuildDurationTicks = 12 * Time.Hour,
            RequiredBuilderCount = 1,
            BaseHealth = 500,
            BlocksMovement = true,
        },
        // M26 — Gate. The doorway in a wall line: blocks like a Wall but the
        // owner + allies pass through freely (AlliedPassage). Deliberately
        // softer than a Wall — the gate is the classic siege target — and
        // pricier per tile (moving parts). Placed via ordinary
        // PlaceSiteIntent on a land tile.
        [StructureKind.Gate] = new StructureSpec
        {
            Kind = StructureKind.Gate,
            IsPlayerBuildable = true,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Stone] = 25,
                [Resource.Wood] = 15,
            },
            BuildDurationTicks = 20 * Time.Hour,
            RequiredBuilderCount = 1,
            BaseHealth = 300,
            BlocksMovement = true,
            AlliedPassage = true,
        },

        // Refining (docs/refining-structures.md) — the second hop.
        //
        // Smelter: the only refiner. No RequiredBiome — WHERE it sits (near
        // the hills, near the forest, near the smithy) is the player's call,
        // which is the whole point. Ore + Wood (fuel) → Iron. Fuel is what
        // makes it a standing appetite rather than a renamed mine. Input
        // cap holds a few days of feed so one supply line per input keeps it
        // fed; output buffer is the extractor-standard 20.
        [StructureKind.Smelter] = new StructureSpec
        {
            Kind = StructureKind.Smelter,
            IsPlayerBuildable = true,
            OutputResource = Resource.Iron,
            InputCost = new SortedDictionary<Resource, int>
            {
                [Resource.Ore] = 2,
                // 10 wood (fuel) per ingot (user, 2026-09-23): iron is a
                // forestry economy as much as a mining one.
                [Resource.Wood] = 10,
            },
            // Per input. 200 keeps "a few days of feed" true at 10 wood an
            // ingot (60 starved a two-worker smelter inside two days).
            InputCap = 200,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 1 * Time.Day,
            WorkerCap = 2,
            BufferCap = 20,
            PreferredRole = UnitRole.Miner,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 40,
                [Resource.Stone] = 30,
            },
            BuildDurationTicks = 60 * Time.Minute,
            RequiredBuilderCount = 1,
            BaseHealth = 150,
        },
        // Workshop: civil crafting storage. The Cart is forged here, so
        // haulage no longer depends on a military build-out.
        [StructureKind.Workshop] = new StructureSpec
        {
            Kind = StructureKind.Workshop,
            IsPlayerBuildable = true,
            StorageCapacity = 200,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 60 },
            BuildDurationTicks = 60 * Time.Minute,
            RequiredBuilderCount = 1,
            BaseHealth = 150,
        },
        // Smithy: weapons crafting storage (Sword / Bow / Shield). Costs no
        // Iron on purpose — it must be buildable before the first ingot.
        [StructureKind.Smithy] = new StructureSpec
        {
            Kind = StructureKind.Smithy,
            IsPlayerBuildable = true,
            StorageCapacity = 200,
            BuildCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 60,
                [Resource.Stone] = 20,
            },
            BuildDurationTicks = 80 * Time.Minute,
            RequiredBuilderCount = 1,
            BaseHealth = 200,
        },
    };

    public static StructureSpec Spec(StructureKind kind) =>
        Specs.TryGetValue(kind, out var s)
            ? s
            : throw new KeyNotFoundException($"No spec for {kind}");

    public static bool TryGetSpec(StructureKind kind, out StructureSpec spec) =>
        Specs.TryGetValue(kind, out spec!);
}
