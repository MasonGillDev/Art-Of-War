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

    // M19 — how many units may call this structure HOME (their food
    // demand point; docs/m19-per-house-food-spec.md). Zero = uncapped
    // (the Castle: deep larder, mess hall for the mobile class) — only
    // the House sets a real cap. Capacity pressure never blocks
    // breeding; overflow newborns home at the nearest free bed, castle
    // fallback.
    public int ResidentCap { get; init; }

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
    // Zero = this extractor type does NOT degrade (Quarry, Mine — out of
    // scope; Hills/Mountain don't participate in the F/G/D ladder).
    // M15: degradation applies to the extractor's CLAIMED tiles
    // (Claims.ClaimantDegradeAmount); overlap is structurally impossible
    // (one claimant per tile) but the fold stays MAX, never sum.
    public int DegradeAmount { get; init; }

    // M15 — extraction claims (docs/extraction-claims.md). Number of
    // RequiredBiome tiles the extractor must claim at placement; the claim
    // is the degradation footprint, the exclusion territory, and the
    // production-taper denominator. Zero = non-claiming kind (Quarry,
    // Mine): fully legacy own-tile behavior.
    public int ClaimCount { get; init; }

    // M15 — Chebyshev range (from the building tile) within which claim
    // tiles may be chosen. Meaningless when ClaimCount == 0.
    public int ClaimRange { get; init; }
}
