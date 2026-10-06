using Sim.Core.World;

namespace Sim.Core.Equipment;

// Per-item equipment spec (docs/equipment-model.md). Mirrors the
// UnitCombatCatalog / StructureCatalog pattern: a static table keyed by
// the item's Resource value, with Spec(item) lookup.
//
// The modifiers here are copied INTO the Buff instance at equip time
// (EquipUnitIntent) and snapshot-carried there — retuning this catalog
// never mutates already-equipped units; it only affects future equips.
public sealed record EquipmentSpec
{
    public required Resource Item { get; init; }
    // Stable buff identity: which item a worn buff turns back into when it is
    // stripped (death, retrain), so every item has its own. One buff per Kind
    // per unit (BuffRules).
    public required string BuffKind { get; init; }
    // M51 — the gear slot it fills: one item per slot. Unset = its own BuffKind
    // (every other item is its own slot). The three swords share "sword": a
    // soldier carries one, and a higher Rank swaps in for a lower one, the old
    // sword going back to the store (docs/m51-ore-tiers-spec.md).
    public string? Slot { get; init; }
    public string SlotName => Slot ?? BuffKind;
    public int Rank { get; init; }
    public int PowerModifier { get; init; }
    // Applied to current Health at equip time; reversed (clamped to
    // min 1) when the equipment is stripped. See docs/equipment-model.md.
    public int HealthModifier { get; init; }
    // M-cart — non-combat modifiers. CargoModifier adds to carry capacity
    // (rolled up live in Unit.CargoCapacity); MoveCostPercent adds to each
    // hop's move cost (the unit moves slower). See docs/cart.md.
    public int CargoModifier { get; init; }
    public int MoveCostPercent { get; init; }
    public required IReadOnlySet<UnitRole> AllowedRoles { get; init; }
    // Consumed from the crafting building's own holdings by
    // CraftEquipmentIntent.
    public required SortedDictionary<Resource, int> CraftCost { get; init; }

    // WHICH building forges it. Named per item rather than hard-typed in the
    // intent, because the intent used to test `is not Barracks` and that is
    // already strained: a Cart is a HAULER's tool and has no business being
    // made in a barracks. With this field a Workshop or a Smithy is a catalog
    // row plus a StructureKind, not a second intent.
    public required StructureKind CraftedAt { get; init; }
}

public static class EquipmentCatalog
{
    // Balance knobs — each weapon sits on a different supply chain
    // (Sword: Ore→Iron, Bow: Wood, Shield: Stone) so military pressure pulls
    // on every extractor type. Since docs/refining-structures.md the
    // weapons are forged at the SMITHY and the Cart at the WORKSHOP; the
    // Barracks trains and holds gear but forges nothing.
    private static readonly Dictionary<Resource, EquipmentSpec> Specs = new()
    {
        // M51 — the sword ladder (docs/m51-ore-tiers-spec.md): each metal a
        // straight stronger sword. The two-hop chain: ore + fuel → a bar at the
        // Smelter, then bars + haft here. Ore itself is never a craft input.
        [Resource.BronzeSword] = Sword(Resource.BronzeSword, "bronze-sword", rank: 1, power: 3, Resource.Bronze),
        [Resource.IronSword] = Sword(Resource.IronSword, "iron-sword", rank: 2, power: 5, Resource.Iron),
        [Resource.SteelSword] = Sword(Resource.SteelSword, "steel-sword", rank: 3, power: 7, Resource.Steel),
        [Resource.Bow] = new EquipmentSpec
        {
            Item = Resource.Bow,
            CraftedAt = StructureKind.Smithy,
            BuffKind = "bow",
            PowerModifier = 4,
            AllowedRoles = new HashSet<UnitRole> { UnitRole.Archer },
            CraftCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 10,
            },
        },
        [Resource.Shield] = new EquipmentSpec
        {
            Item = Resource.Shield,
            CraftedAt = StructureKind.Smithy,
            BuffKind = "shield",
            HealthModifier = 10,
            AllowedRoles = new HashSet<UnitRole> { UnitRole.Soldier, UnitRole.Archer },
            CraftCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 5,
                [Resource.Stone] = 5,
            },
        },
        // M-cart — a hauler's cart: +25 carry (doubles the Hauler's 25 base)
        // at the cost of +50% move time per hop (the tradeoff that makes it a
        // choice, not a free upgrade). Crafted from a frame (Wood) + wheels
        // (Stone). Equippable by Haulers — the carry role; widen AllowedRoles
        // if other roles should pull a cart. It shares the 2 generic buff
        // slots (no separate gear slot). docs/cart.md.
        [Resource.Cart] = new EquipmentSpec
        {
            Item = Resource.Cart,
            // The balance call was made in docs/refining-structures.md: a
            // cart at a workshop unbinds haulage from military build-out.
            CraftedAt = StructureKind.Workshop,
            BuffKind = "cart",
            CargoModifier = 25,
            MoveCostPercent = 50,
            AllowedRoles = new HashSet<UnitRole> { UnitRole.Hauler },
            CraftCost = new SortedDictionary<Resource, int>
            {
                [Resource.Wood] = 20,
                [Resource.Stone] = 10,
            },
        },
    };

    private static EquipmentSpec Sword(Resource item, string buffKind, int rank, int power, Resource metal) => new()
    {
        Item = item,
        CraftedAt = StructureKind.Smithy,
        BuffKind = buffKind,
        Slot = "sword",
        Rank = rank,
        PowerModifier = power,
        AllowedRoles = new HashSet<UnitRole> { UnitRole.Soldier },
        CraftCost = new SortedDictionary<Resource, int>
        {
            [Resource.Wood] = 2,
            [metal] = 3,
        },
    };

    public static EquipmentSpec Spec(Resource item) =>
        Specs.TryGetValue(item, out var s)
            ? s
            : throw new KeyNotFoundException($"No equipment spec for {item}");

    public static bool TryGetSpec(Resource item, out EquipmentSpec spec) =>
        Specs.TryGetValue(item, out spec!);

    // Reverse lookup: is this buff Kind an equipment buff, and which
    // item does it convert back to (death / retrain drop)?
    public static bool TryGetByKind(string kind, out EquipmentSpec spec)
    {
        foreach (var s in Specs.Values)
        {
            if (s.BuffKind == kind) { spec = s; return true; }
        }
        spec = null!;
        return false;
    }

    public static bool IsEquipmentKind(string kind) => TryGetByKind(kind, out _);
}
