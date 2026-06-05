namespace Sim.Core.World;

// Static description of a structure kind. All Phase-A consumers read from here
// rather than hard-coding constants.
//
// Fields default to neutral values so each kind only sets what's meaningful:
//   - Storage-only types (Castle, Stockpile) set StorageCapacity.
//   - Extractors set RequiredBiome, OutputResource, production fields, BufferCap.
//   - All buildable types set BuildCost / BuildDurationTicks / RequiredBuilderCount.
public sealed record StructureSpec
{
    public required StructureKind Kind { get; init; }

    // Player-buildable means the player can submit a BuildIntent for this kind.
    // Castle = false (placed at genesis). ConstructionSite = false (internal).
    // Tower = false (reserved).
    public bool IsPlayerBuildable { get; init; }

    // Storage (Castle, Stockpile). Zero for non-storage kinds.
    public int StorageCapacity { get; init; }

    // Extractor fields. Default values mark "not an extractor."
    public Biome RequiredBiome { get; init; } = Biome.None;
    public Resource OutputResource { get; init; } = Resource.None;
    public int BaseRatePerWorker { get; init; }
    public int ProductionPeriodTicks { get; init; }
    public int WorkerCap { get; init; }
    public int BufferCap { get; init; }
    public UnitRole PreferredRole { get; init; } = UnitRole.None;
    public int RoleBonusNumerator { get; init; } = 1;
    public int RoleBonusDenominator { get; init; } = 1;

    // Build requirements. Empty BuildCost + zero RequiredBuilderCount means
    // not buildable (paired with IsPlayerBuildable = false).
    public IReadOnlyDictionary<Resource, int> BuildCost { get; init; } =
        new SortedDictionary<Resource, int>();
    public int BuildDurationTicks { get; init; }
    public int RequiredBuilderCount { get; init; }

    // M9 — fertility degrade contribution while actively producing. Combined
    // with BiomeDegradationConfig.DegradePeriod (global) to give a rate.
    // Zero = this extractor type does NOT degrade in M9 (Quarry, Mine — out
    // of scope; Hills/Mountain don't participate in the F/G/D ladder).
    // MAX over overlapping in-range producers (NEVER sum) is enforced in
    // BiomeDegradation.MaxInRangeProducingDegradeAmount.
    public int DegradeAmount { get; init; }
}
