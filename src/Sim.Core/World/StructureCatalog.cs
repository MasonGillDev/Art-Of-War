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
            BuildDurationTicks = 50,
            RequiredBuilderCount = 1,
        },
        [StructureKind.LumberCamp] = new StructureSpec
        {
            Kind = StructureKind.LumberCamp,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Forest,
            OutputResource = Resource.Wood,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 10,
            WorkerCap = 3,
            BufferCap = 30,
            PreferredRole = UnitRole.Lumberjack,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 10 },
            BuildDurationTicks = 40,
            RequiredBuilderCount = 1,
        },
        [StructureKind.Quarry] = new StructureSpec
        {
            Kind = StructureKind.Quarry,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Mountain,
            OutputResource = Resource.Stone,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 15,
            WorkerCap = 3,
            BufferCap = 20,
            PreferredRole = UnitRole.Quarryman,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 15 },
            BuildDurationTicks = 50,
            RequiredBuilderCount = 2,
        },
        [StructureKind.Mine] = new StructureSpec
        {
            Kind = StructureKind.Mine,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Hills,
            OutputResource = Resource.Ore,
            BaseRatePerWorker = 1,
            ProductionPeriodTicks = 15,
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
            BuildDurationTicks = 60,
            RequiredBuilderCount = 2,
        },
        [StructureKind.Farm] = new StructureSpec
        {
            Kind = StructureKind.Farm,
            IsPlayerBuildable = true,
            RequiredBiome = Biome.Grassland,
            OutputResource = Resource.Food,
            BaseRatePerWorker = 2,
            ProductionPeriodTicks = 10,
            WorkerCap = 3,
            BufferCap = 40,
            PreferredRole = UnitRole.Farmer,
            RoleBonusNumerator = 2,
            RoleBonusDenominator = 1,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 10 },
            BuildDurationTicks = 30,
            RequiredBuilderCount = 1,
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
            BuildDurationTicks = 60,
            RequiredBuilderCount = 1,
            // Vision contribution is read from Sight.RadiusFor — not duplicated here.
        },
        [StructureKind.House] = new StructureSpec
        {
            Kind = StructureKind.House,
            IsPlayerBuildable = true,
            // Holdings cap kept small; the House is a breeding gate, not
            // a general food store. Big enough to hold a few cycles' worth
            // of BirthFoodCost so a player can pre-stock.
            StorageCapacity = 100,
            BuildCost = new SortedDictionary<Resource, int> { [Resource.Wood] = 30 },
            BuildDurationTicks = 60,
            RequiredBuilderCount = 1,
        },
    };

    public static StructureSpec Spec(StructureKind kind) =>
        Specs.TryGetValue(kind, out var s)
            ? s
            : throw new KeyNotFoundException($"No spec for {kind}");

    public static bool TryGetSpec(StructureKind kind, out StructureSpec spec) =>
        Specs.TryGetValue(kind, out spec!);
}
