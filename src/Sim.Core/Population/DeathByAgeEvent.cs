using Sim.Core.World;

namespace Sim.Core.Population;

// M8 Phase B — fires at a unit's scheduled DeathTick to remove the unit
// from the world via the M7 OnUnitDeath pipeline (which clears in-flight
// obligations, group cleanup, drops cargo).
//
// Fencing: the event captures (UnitId, ExpectedTick, ExpectedSeq) at
// schedule time. On Apply, the unit's stored (DeathTick, DeathSeq) must
// match (At, Seq) — anything else means the anchor changed (shouldn't
// happen today; lifespan never re-rolls) and we no-op.
//
// Calls Population.OnUnitRemoved at the end (Phase E adds the body;
// for now it's a no-op).
public sealed class DeathByAgeEvent : ScheduledEvent
{
    public int UnitId { get; }

    public DeathByAgeEvent(int unitId) { UnitId = unitId; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
        {
            // Unit already removed by combat; the M7 OnUnitDeath path will
            // also have cleared death anchors, so this fence is just defense.
            Outcome = IntentOutcome.Reject($"unit {UnitId} no longer exists");
            return;
        }
        if (unit.DeathTick != At || unit.DeathSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale death-by-age event for unit {UnitId} " +
                $"(stored=({unit.DeathTick},{unit.DeathSeq}), event=({At},{Seq}))");
            return;
        }

        // Reuse the M7 clean-death pipeline: drops cargo, group cleanup,
        // clears in-flight obligations, removes from world.Units.
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, unit);

        // Phase E will wire breeding stop-on-removal here. Today: no-op.
        Population.OnUnitRemoved(sim, unit);
    }

    public override string Describe() => $"DeathByAge(unit={UnitId})";
}
