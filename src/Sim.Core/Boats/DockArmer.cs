using Sim.Core.World;

namespace Sim.Core.Boats;

// M12 Phase C — slip-clear hook + initial production schedule.
//
// Docks stall their boat production when their Slip is occupied (set
// ProductionArmed = false). To re-arm we need a deterministic event-
// driven signal that "the slip is now clear" — which happens when the
// occupant moves away. The natural seam is MoveArrivalEvent.Apply
// (and MoveBoatIntent's equivalent in Phase D): after the unit's
// position is updated, call DockArmer.OnUnitLeftTile with the tile
// they just left. If any dock's Slip matches that tile, that dock
// re-evaluates and re-arms.
//
// O(structures) per move arrival; fine at current scale. A per-slip
// index becomes worthwhile when world size demands.
public static class DockArmer
{
    // Schedule the initial production tick after a Dock finishes
    // building. Called from BuildCompleteEvent.
    public static void OnDockBuilt(Simulation sim, Dock dock)
    {
        dock.LastProductionTick = sim.Now;
        ArmIfDormant(sim, dock);
    }

    // Slip just freed up — try re-arming any dock whose slip is `tile`.
    public static void OnUnitLeftTile(Simulation sim, TileCoord tile)
    {
        // Iterate canonical (y, x) by sorting the values; structures
        // dict iteration order isn't guaranteed by .NET.
        foreach (var s in sim.World.Structures.Values)
        {
            if (s is not Dock dock) continue;
            if (dock.Slip != tile) continue;
            ArmIfDormant(sim, dock);
        }
    }

    // Idempotent: if already armed, does nothing. If slip is still
    // occupied, does nothing. Otherwise schedules the next production
    // tick.
    public static void ArmIfDormant(Simulation sim, Dock dock)
    {
        if (dock.ProductionArmed) return;
        // Don't schedule if the slip is still occupied (defensive — the
        // caller should usually only call us after the slip clears).
        if (HasAnyUnitOn(sim.World, dock.Slip)) return;
        var period = StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        var fireAt = sim.Now + period;
        dock.ProductionArmed = true;
        dock.NextProductionTickSeq = sim.Schedule(
            fireAt, new BoatProductionTickEvent(dock.At));
    }

    private static bool HasAnyUnitOn(GameWorld world, TileCoord tile)
    {
        foreach (var u in world.Units.Values)
            if (u.Position == tile) return true;
        return false;
    }
}
