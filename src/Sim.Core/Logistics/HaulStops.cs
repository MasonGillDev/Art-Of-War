namespace Sim.Core.Logistics;

// M28 — boat freight (docs/boats.md update). A Water-traversal hauler
// serves DOCKS: it stands on the dock's SLIP at each stop (a boat can
// never enter the dock's land tile) while the cargo moves against the
// dock structure itself. These helpers are the whole amphibious seam —
// where a hauler must STAND for a stop at `tile`, and whether it is
// standing there now. Both are PURE READS (one dictionary lookup), called
// from HaulIntent resolution and the pickup/deposit/arrival events.
public static class HaulStops
{
    // Where the carrier physically travels for a haul stop at `tile`: the
    // tile itself on foot; the dock's slip over water. Null = the stop is
    // not water-servable (no dock there) — boat hauls validate their
    // endpoints against this at resolve time and fail clean if the dock
    // vanishes mid-trip.
    public static TileCoord? MoveTarget(GameWorld world, Unit hauler, TileCoord tile)
    {
        if (hauler.Traversal != Traversal.Water) return tile;
        return world.Structures.TryGetValue(tile, out var s) && s is Dock d
            ? d.Slip : null;
    }

    // Is the carrier standing at its stop? On the tile on foot; on the
    // dock's slip over water. (Also true for a foot hauler ON `tile`
    // regardless of what stands there — the pre-M28 rule, unchanged.)
    public static bool AtStop(GameWorld world, Unit hauler, TileCoord tile)
    {
        if (hauler.Position == tile) return true;
        return hauler.Traversal == Traversal.Water
            && world.Structures.TryGetValue(tile, out var s) && s is Dock d
            && hauler.Position == d.Slip;
    }

    // The own dock whose SLIP is `slip`, if any — canonical (y, x) scan,
    // the FindEmbarkDockTile discipline. Used by UnloadCargoIntent so a
    // laden boat parked on a slip can empty into its quay.
    public static Dock? OwnDockBySlip(GameWorld world, TileCoord slip, int ownerId)
    {
        Dock? best = null;
        foreach (var s in world.Structures.Values)
        {
            if (s is not Dock d || d.OwnerId != ownerId || d.Slip != slip) continue;
            if (best is null || d.At.Y < best.At.Y
                || (d.At.Y == best.At.Y && d.At.X < best.At.X))
                best = d;
        }
        return best;
    }
}
