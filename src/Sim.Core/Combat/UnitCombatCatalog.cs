using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 — per-role combat baseline catalog. Mirrors StructureCatalog: a static
// table mapping UnitRole → UnitCombatSpec, with Spec(role) lookup.
//
// Today every role is uniform (BaseHealth = 10, BasePower = 1) — civilian
// units chip at each other; combat is decided by force size and Health,
// not by role-typing. Trainable combat units (Soldier, Archer, Knight, ...)
// land later as new rows with higher BaseHealth and BasePower; the rollup
// never changes.
//
// Buff modifiers (armor, training) layer on top via Unit.Buffs without
// touching the catalog.
public static class UnitCombatCatalog
{
    // Uniform civilian baseline per M7 plan. Append new rows; never edit
    // existing values without considering snapshot determinism (a stored
    // unit's Health is just an int — but a freshly-spawned unit's initial
    // Health is read from this table at AddUnit time).
    private static readonly Dictionary<UnitRole, UnitCombatSpec> Specs = new()
    {
        [UnitRole.None]       = new UnitCombatSpec { Role = UnitRole.None,       BaseHealth = 10, BasePower = 1 },
        [UnitRole.Builder]    = new UnitCombatSpec { Role = UnitRole.Builder,    BaseHealth = 10, BasePower = 1 },
        [UnitRole.Farmer]     = new UnitCombatSpec { Role = UnitRole.Farmer,     BaseHealth = 10, BasePower = 1 },
        [UnitRole.Miner]      = new UnitCombatSpec { Role = UnitRole.Miner,      BaseHealth = 10, BasePower = 1 },
        [UnitRole.Lumberjack] = new UnitCombatSpec { Role = UnitRole.Lumberjack, BaseHealth = 10, BasePower = 1 },
        [UnitRole.Quarryman]  = new UnitCombatSpec { Role = UnitRole.Quarryman,  BaseHealth = 10, BasePower = 1 },
        [UnitRole.Hauler]     = new UnitCombatSpec { Role = UnitRole.Hauler,     BaseHealth = 10, BasePower = 1 },
        [UnitRole.Scout]      = new UnitCombatSpec { Role = UnitRole.Scout,      BaseHealth = 10, BasePower = 1 },
        // M12 — boats are bulkier and don't fight back from the deck
        // (the design defers passenger combat). BaseHealth high enough
        // that a small force can't one-shot a transport; BasePower = 0
        // so a passenger-empty boat doesn't add to a force.
        [UnitRole.Boat]       = new UnitCombatSpec { Role = UnitRole.Boat,       BaseHealth = 40, BasePower = 0 },
        // Military roles (docs/military-training.md) — Soldier is the
        // tank (dies last under lowest-Health-first), Archer the glass
        // cannon. Both numbers are balance knobs.
        [UnitRole.Soldier]    = new UnitCombatSpec { Role = UnitRole.Soldier,    BaseHealth = 30, BasePower = 3 },
        [UnitRole.Archer]     = new UnitCombatSpec { Role = UnitRole.Archer,     BaseHealth = 15, BasePower = 5 },
    };

    public static UnitCombatSpec Spec(UnitRole role) =>
        Specs.TryGetValue(role, out var s)
            ? s
            : throw new KeyNotFoundException($"No combat spec for role {role}");
}
