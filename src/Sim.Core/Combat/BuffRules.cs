using Sim.Core.World;

namespace Sim.Core.Combat;

// Generic buff-slot rules (docs/equipment-model.md). Lives in Combat —
// not the equipment layer — because every future buff source (training
// drills, well-fed, armor) shares the same slots.
public static class BuffRules
{
    // Balance knob: a unit's loadout size. Two slots of DISTINCT kinds
    // = customization (sword + shield), not stacking (sword + sword).
    public const int MaxBuffsPerUnit = 2;

    // Can `unit` accept a new buff of `kind`? Under the slot cap AND no
    // existing buff of the same kind. Callers granting buffs must gate
    // on this so the slot discipline holds across all sources.
    public static bool CanAccept(Unit unit, string kind)
    {
        if (unit.Buffs.Count >= MaxBuffsPerUnit) return false;
        foreach (var b in unit.Buffs)
            if (b.Kind == kind) return false;
        return true;
    }
}
