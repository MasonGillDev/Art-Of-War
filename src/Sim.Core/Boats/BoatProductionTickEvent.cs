using Sim.Core.World;

namespace Sim.Core.Boats;

// M12 Phase C — self-rescheduling event that produces a Boat unit on a
// Dock's slip tile every ProductionPeriodTicks while the slip is free.
//
// Fencing: (At, Seq) match the Dock's (NextProductionTick implied from
// LastProductionTick + period, NextProductionTickSeq). On stall + re-arm,
// the previously-queued event would fence by Seq mismatch.
//
// Stall: if Slip is occupied at fire time, set ProductionArmed = false
// and NextProductionTickSeq = null. The MoveArrivalEvent slip-clear
// hook (Boats.DockArmer.OnUnitLeftTile) re-arms by scheduling a new
// production tick at sim.Now + period.
public sealed class BoatProductionTickEvent : ScheduledEvent
{
    public TileCoord DockTile { get; }

    public BoatProductionTickEvent(TileCoord dockTile) { DockTile = dockTile; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(DockTile, out var s) || s is not Dock dock)
        {
            Outcome = IntentOutcome.Reject($"no Dock at {DockTile}");
            return;
        }
        if (!dock.ProductionArmed || dock.NextProductionTickSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale BoatProductionTick at {DockTile} " +
                $"(armed={dock.ProductionArmed}, " +
                $"storedSeq={dock.NextProductionTickSeq}, eventSeq={Seq})");
            return;
        }

        // Check slip — count any unit on the slip tile.
        if (HasAnyUnitOn(world, dock.Slip))
        {
            // Stall. Re-arm happens via OnUnitLeftTile.
            dock.ProductionArmed = false;
            dock.NextProductionTickSeq = null;
            return;
        }

        // Slip is free — spawn the boat. Use a fresh id from the
        // world's monotonic counter (same allocator as births).
        var boatId = world.NextUnitId;
        world.NextUnitId++;
        world.AddUnit(new Unit(boatId, dock.Slip)
        {
            Role = UnitRole.Boat,
            OwnerId = dock.OwnerId,
            Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap,
            // CargoCapacity is derived from Role via UnitCargoCatalog
            // (Role=Boat → BoatCapacity).
            BornTick = sim.Now,
        });

        // Reschedule the next production cycle.
        dock.LastProductionTick = sim.Now;
        var next = sim.Now + StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        dock.NextProductionTickSeq = sim.Schedule(next, new BoatProductionTickEvent(dock.At));
    }

    public override string Describe() => $"BoatProductionTick(@ {DockTile.X},{DockTile.Y})";

    private static bool HasAnyUnitOn(GameWorld world, TileCoord tile)
    {
        foreach (var u in world.Units.Values)
            if (u.Position == tile) return true;
        return false;
    }
}
