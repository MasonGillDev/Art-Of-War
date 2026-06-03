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
}
