using Sim.Core.Combat;

namespace Sim.Core.Healing;

// Fires at the end of one rest period (docs/unit-healing.md).
//
// Fencing: the unit's stored (NextRestHealTick, NextRestHealSeq) must match
// (At, Seq). Rest.Interrupt clears the anchor when the rest is broken, so an
// event scheduled for a rest that ended no-ops here.
//
// On fire: still resting → heal HealPerPeriod (clamped to MaxHealth) and, if
// still below full, schedule the next period. Otherwise go dormant (anchor
// null); Rest.ArmIfDormant restarts it from the event that changes things.
public sealed class RestHealEvent : ScheduledEvent
{
    public int UnitId { get; }

    public RestHealEvent(int unitId) { UnitId = unitId; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
        {
            Outcome = IntentOutcome.Reject($"unit {UnitId} no longer exists");
            return;
        }
        if (unit.NextRestHealTick != At || unit.NextRestHealSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale rest heal for unit {UnitId} " +
                $"(stored=({unit.NextRestHealTick},{unit.NextRestHealSeq}), event=({At},{Seq}))");
            return;
        }

        Rest.Interrupt(unit);
        if (!Rest.IsResting(world, unit, sim.Now))
        {
            Outcome = IntentOutcome.Reject($"unit {UnitId} is not resting");
            return;
        }

        var max = CombatRules.MaxHealth(unit, sim.Now);
        unit.Health = Math.Min(max, unit.Health + RestConstants.HealPerPeriod);
        if (unit.Health < max) Rest.Schedule(sim, unit);
    }

    public override string Describe() => $"RestHeal(unit={UnitId})";
}
