using Sim.Core.Automation;
using Sim.Core.Equipment;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;

namespace Sim.Server.Automation;

// M18 — turns an ActionSpec into the ordinary intent it stands for, voiced
// as the order's owner. THE WHOLE TABLE: every action atom is exactly one
// existing intent with parameters filled in — no new sim semantics live
// here, ever (the growth rule, docs/m18-automation-engine-spec.md). The
// emitted intent's own Resolve does the real validation against the live
// world when it fires.
public static class IntentFactory
{
    public static Intent Create(in ActionSpec a, int ownerId) => a.Kind switch
    {
        ActionKind.MoveTo =>
            new MoveIntent(a.UnitId, a.TargetTile) { PlayerId = ownerId },
        ActionKind.HaulTrip =>
            new HaulIntent(a.UnitId, a.TargetTile, a.SecondTile, a.Resource) { PlayerId = ownerId },
        ActionKind.LoadCargo =>
            new LoadCargoIntent(a.UnitId, a.Resource) { PlayerId = ownerId },
        ActionKind.UnloadCargo =>
            new UnloadCargoIntent(a.UnitId) { PlayerId = ownerId },
        ActionKind.Train =>
            new TrainUnitIntent(a.UnitId, a.Role) { PlayerId = ownerId },
        ActionKind.Craft =>
            new CraftEquipmentIntent(a.TargetTile, a.Resource) { PlayerId = ownerId },
        ActionKind.AssignWorkers =>
            new AssignWorkersIntent(a.TargetTile, new[] { a.UnitId }) { PlayerId = ownerId },
        ActionKind.UnassignWorkers =>
            new UnassignWorkersIntent(a.TargetTile, new[] { a.UnitId }) { PlayerId = ownerId },
        _ => throw new InvalidOperationException(
            $"IntentFactory has no mapping for ActionKind {(byte)a.Kind} — " +
            "was a new atom added to the enum without a factory case?"),
    };
}
