using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 — per-role combat baseline. Same shape as StructureSpec: a flat
// record with init properties; live values come through
// UnitCombatCatalog.Spec(role). Equipment / armor / training all layer
// on top via Unit.Buffs without rewriting the rollup.
public sealed record UnitCombatSpec
{
    public required UnitRole Role { get; init; }
    public required int BaseHealth { get; init; }
    public required int BasePower { get; init; }

    // 2026-09-24 — a RANGED unit fights from behind its own side's line: on a
    // contested tile it takes no damage while any non-ranged unit of its owner
    // still stands there (the enemy has to get through the line first). It
    // still deals its full power every round. See docs/combat-model.md,
    // "Update 2026-09-24 — archers fight behind the line".
    public bool Ranged { get; init; }
}
