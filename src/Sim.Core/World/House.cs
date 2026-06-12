namespace Sim.Core.World;

// M8 — breeding structure. Mirrors the extractor *pattern* (occupied
// units + food draw + scheduled completion) but the output is a Unit, not
// a Resource. Storage holds food the breeding cycle consumes.
//
// A House is empty when Occupation == null. While Occupation is non-null,
// the two named parents are bound to this house (Activity.Working,
// Assignment = HouseTile) and a BirthEvent is queued at Occupation.BirthTick.
// The M4 anchor is (BirthTick, BirthSeq) — RegenerateQueue reconstructs
// the BirthEvent from Occupation on snapshot restore.
//
// Stop-on-removal: if either parent is removed from world.Units (combat
// or old-age death), Population.OnUnitRemoved clears Occupation; the
// queued BirthEvent then fences on anchor-mismatch when it fires.
// M19 — House is a FOOD HOME (Sim.Core.Food.IFoodHome): its residents
// eat from its cache, and a dry house runs the FULL famine-debt
// machinery — debt, grace, death cadence among its own residents —
// even when the castle larder is full (the user's harsh-doctrine
// lock; docs/m19-per-house-food-spec.md).
public sealed class House : StorageStructure, Sim.Core.Food.IFoodHome
{
    public override StructureKind Kind => StructureKind.House;

    // Null = vacant; non-null = breeding in progress.
    public BreedingOccupation? Occupation { get; internal set; }

    // M19 — how many units call this house HOME (their food demand
    // point; see Unit.Home). Capped by StructureSpec.ResidentCap.
    // Mutated ONLY via Population.SetHome (plus the death path in
    // Population.OnUnitRemoved); serialized (and hashed) so restore
    // can't drift from the units' Home fields.
    public int ResidentCount { get; internal set; }

    // M19 Phase 2 — the food-home anchors, field-for-field the Castle's
    // M13 set (see Castle for the per-field contracts). Serialized in
    // the House payload at FormatVersion 13.
    public long LastFoodConsumedTick { get; set; }
    public int FoodDebt { get; set; }
    public long? FamineStartTick { get; set; }
    public long? NextFamineCheckTick { get; set; }
    public long? NextFamineCheckSeq { get; set; }
    public long? NextStarvationDeathTick { get; set; }
    public long? NextStarvationDeathSeq { get; set; }

    public House(TileCoord at) : base(at, StructureCatalog.Spec(StructureKind.House).StorageCapacity) { }
}

// Per-house breeding anchor. Class (not struct) so the null-vacant
// pattern is unambiguous.
public sealed class BreedingOccupation
{
    public int ParentAId { get; init; }
    public int ParentBId { get; init; }
    public long BirthTick { get; init; }
    public long BirthSeq { get; init; }

    public bool ContainsParent(int unitId) =>
        unitId == ParentAId || unitId == ParentBId;

    public int OtherParent(int unitId)
    {
        if (unitId == ParentAId) return ParentBId;
        if (unitId == ParentBId) return ParentAId;
        throw new InvalidOperationException(
            $"{unitId} is not a parent in this occupation ({ParentAId},{ParentBId}).");
    }
}
