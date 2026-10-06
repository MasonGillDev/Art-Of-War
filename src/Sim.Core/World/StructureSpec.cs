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
    // M44 — the site tile must be an ore vein the placing faction knows
    // (Sim.Core.Mining.Veins). docs/stone-and-ore-land.md.
    // M51 — and the vein must hold THIS ore: one mine per ore
    // (docs/m51-ore-tiers-spec.md). None = not a mine.
    public Resource RequiredOre { get; init; } = Resource.None;
    public bool RequiresVein => RequiredOre != Resource.None;
    public Resource OutputResource { get; init; } = Resource.None;
    public int BaseRatePerWorker { get; init; }
    public int ProductionPeriodTicks { get; init; }
    public int WorkerCap { get; init; }
    public int BufferCap { get; init; }
    public UnitRole PreferredRole { get; init; } = UnitRole.None;
    public int RoleBonusNumerator { get; init; } = 1;
    public int RoleBonusDenominator { get; init; } = 1;

    // Refining (docs/refining-structures.md). A REFINER is an Extractor
    // whose production tick EATS from its own input store before it deposits
    // output. M51 (docs/m51-ore-tiers-spec.md): it has RECIPES, best first —
    // each one batch's inputs and the one unit of output it makes — and a tick
    // makes the first recipe it can afford. InputCap bounds how much of EACH
    // input it will accept from haulers (per input line, so one supply line can
    // never crowd out another). No recipes = ordinary extractor. A refiner has
    // no RequiredBiome — WHERE it sits is the player's siting decision — and no
    // claim, and no OutputResource of its own (its recipes name the outputs).
    public IReadOnlyList<RefineRecipe> Recipes { get; init; } = Array.Empty<RefineRecipe>();
    public int InputCap { get; init; }
    public bool IsRefiner => Recipes.Count > 0;

    // An Extractor kind: one that works land (a required biome) or refines.
    // The one place that rule lives — the constructors (Construction,
    // Snapshot) and the Extractor itself read it (M51 added two mines and
    // three hand-kept kind lists missed them).
    public bool IsExtractor => RequiredBiome != Biome.None || IsRefiner;

    // Does any recipe take `r` as an input?
    public bool TakesInput(Resource r)
    {
        foreach (var recipe in Recipes)
            if (recipe.Inputs.ContainsKey(r)) return true;
        return false;
    }

    // Build requirements. Empty BuildCost + zero RequiredBuilderCount means
    // not buildable (paired with IsPlayerBuildable = false).
    public IReadOnlyDictionary<Resource, int> BuildCost { get; init; } =
        new SortedDictionary<Resource, int>();
    public int BuildDurationTicks { get; init; }
    public int RequiredBuilderCount { get; init; }

    // M9 — fertility degrade contribution while actively producing. Combined
    // with BiomeDegradationConfig.DegradePeriod (global) to give a rate.
    // Zero = this extractor type does NOT degrade (Quarry, Mine — M44:
    // stone and ore are slow but never scarce, and Hills/Mountain stay off
    // the F/G/D ladder for good; docs/stone-and-ore-land.md).
    // M15: degradation applies to the extractor's CLAIMED tiles
    // (Claims.ClaimantDegradeAmount); overlap is structurally impossible
    // (one claimant per tile) but the fold stays MAX, never sum.
    public int DegradeAmount { get; init; }

    // M15 — extraction claims (docs/extraction-claims.md). Number of
    // RequiredBiome tiles the extractor must claim at placement; the claim
    // is the degradation footprint, the exclusion territory, and the
    // production-taper denominator. Zero = non-claiming kind (Mine — its
    // vein IS its land): own-tile behavior. M44: the Quarry claims Hills
    // but never degrades them — the claim is pure exclusion territory, and
    // off-ladder Hills hold their baseline so the taper stays at 1.
    public int ClaimCount { get; init; }

    // M15 — Chebyshev range (from the building tile) within which claim
    // tiles may be chosen. Meaningless when ClaimCount == 0.
    public int ClaimRange { get; init; }

    // M24 — siege HP. The hit-point pool combat damages once defenders
    // are cleared from the tile (CombatRoundEvent.Apply). Zero = the
    // SENTINEL "indestructible" (Cache, Canal, Rubble, and the transient
    // internal kinds that should never take siege damage). Live values
    // come through StructureCatalog.Spec(kind).BaseHealth and are written
    // onto Structure.Health by GameWorld.AddStructure at insertion time,
    // mirroring UnitCombatCatalog → Unit.Health.
    public int BaseHealth { get; init; }

    // M26 — fortifications (docs/walls-and-gates.md). A blocking structure's
    // tile cannot be ENTERED by ground movement while it stands (entry-only:
    // a unit already on the tile can walk off). Enforced fog-split in
    // MovementCost.PlanCost (planner sees own + visible blockers) and
    // ground-truth at hop-schedule / arrival-fire time. Blocking kinds are
    // besieged from adjacent tiles (Fortifications.FortSiege) since nobody
    // can stand on them.
    public bool BlocksMovement { get; init; }

    // M26 — the Gate: a blocking kind the owner and RelationshipState.Ally
    // factions pass through freely. Evaluated live at every enforcement
    // point, so a broken alliance closes the gate mid-march. Meaningless
    // when BlocksMovement is false.
    public bool AlliedPassage { get; init; }

    // Rest healing (docs/unit-healing.md). The owner's wounded units standing
    // on this kind's tile, off any battlefield, heal at the flat
    // RestConstants rate. Castle, House and Barracks only.
    public bool Shelters { get; init; }
}

// M51 — one refiner recipe: what one batch consumes, and the one unit of
// `Output` it makes (docs/m51-ore-tiers-spec.md).
public sealed record RefineRecipe(Resource Output, IReadOnlyDictionary<Resource, int> Inputs);
