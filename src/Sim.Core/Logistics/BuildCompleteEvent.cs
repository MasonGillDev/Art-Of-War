namespace Sim.Core.Logistics;

// Fires when a ConstructionSite's active run is projected to finish. Three
// gates before it actually completes (per docs/intent-validation.md — never
// trust scheduling-time facts):
//
//   1. Site still exists on the tile.
//   2. Fencing token: ScheduledCompletion == this.At. If the site was paused
//      (or paused-and-resumed with a new completion tick), this stale event
//      is now wrong about when to complete and must no-op.
//   3. Prereqs still hold: materials present, enough builders Building this
//      site on this tile.
//
// On success: the site is removed, the target structure is created in its
// place, and all assigned builders are released to Idle.
public sealed class BuildCompleteEvent : ScheduledEvent
{
    public TileCoord SiteTile { get; }

    public BuildCompleteEvent(TileCoord siteTile) { SiteTile = siteTile; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;

        if (!world.Structures.TryGetValue(SiteTile, out var s) || s is not ConstructionSite site)
        {
            Outcome = IntentOutcome.Reject($"no construction site at {SiteTile.X},{SiteTile.Y}");
            return;
        }

        if (site.ScheduledCompletion != At)
        {
            Outcome = IntentOutcome.Reject(
                $"stale completion (paused or rescheduled; scheduled={site.ScheduledCompletion}, fired at={At})");
            return;
        }

        if (!site.ConditionsMet(world))
        {
            // Mid-flight a builder may have died or materials been removed by
            // some future intent. Fail clean — the site stays, the event is just a no-op.
            Outcome = IntentOutcome.Reject("prereqs no longer met at completion");
            return;
        }

        // Success. Free the builders, swap site for the built structure.
        var builderIds = new List<int>();
        foreach (var u in world.Units.Values)
        {
            if (u.Position == SiteTile && u.Activity == Activity.Building && u.Assignment == SiteTile)
                builderIds.Add(u.Id);
        }
        foreach (var id in builderIds)
            world.Units[id].TrySetActivity(Activity.Idle);

        world.Structures.Remove(SiteTile);
        // OwnerId inherits from the ConstructionSite (which got it from
        // PlaceSiteIntent's PlayerId at submission time).
        var built = BuildStructure(site.TargetKind, SiteTile, site.OwnerId, site.DockSlip);
        // M15 — the claim reserved at placement transfers to the finished
        // extractor. COPY (AddRange of value-type coords), never alias the
        // site's list. Sites never produce, so no degradation rate changes
        // at transfer — no catch-up needed (docs/extraction-claims.md).
        if (built is Extractor builtExtractor && site.ClaimTiles.Count > 0)
            builtExtractor.ClaimTiles.AddRange(site.ClaimTiles);
        world.AddStructure(built);
        // M3 Phase B: if the new structure is a vision source (Castle /
        // Tower), reveal its area for the owner.
        var visionRadius = Sight.RadiusFor(built.Kind);
        if (visionRadius > 0)
            Sight.Reveal(world, built.OwnerId, built.At, visionRadius, sim.Now);
        // M12 — Dock starts producing boats immediately.
        if (built is Dock dock)
            Sim.Core.Boats.DockArmer.OnDockBuilt(sim, dock);
        // M19 — auto-assignment trigger 3 (house completion): frontier
        // crews are usually staffed BEFORE their house stands, and
        // assignments are sticky for months — without this, the new
        // house would sit empty while its intended residents kept
        // draining the castle. The new house scans own citizens at work
        // nearby whose home sits farther from their post than it does,
        // and moves them in nearest-first until the beds fill.
        if (built is House newHouse)
            MoveNearbyWorkersIn(sim, newHouse);
    }

    // Own citizens WORKING/BUILDING within HomeAssignRadius of the new
    // house (working units stand on their post, so unit.Position IS the
    // workplace), whose current home is farther from that post than the
    // new house is — re-homed in (distance-to-house, unit id) order
    // until ResidentCap fills. One discrete deterministic event; nobody
    // physically moves (homes are demand points, not destinations).
    private static void MoveNearbyWorkersIn(Simulation sim, House house)
    {
        var world = sim.World;
        var cap = StructureCatalog.Spec(StructureKind.House).ResidentCap;
        var radius = Sim.Core.Food.FoodConsumptionConstants.HomeAssignRadius;
        int Cheb(TileCoord a, TileCoord b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        var castle = Sim.Core.Food.FoodConsumption.FindCastleFor(world, house.OwnerId);
        var candidates = world.Units.Values
            .Where(u => u.OwnerId == house.OwnerId
                && u.Activity is Activity.Working or Activity.Building
                && Cheb(u.Position, house.At) <= radius)
            .Where(u =>
            {
                var homeTile = u.Home ?? castle?.At;
                var currentDist = homeTile is { } h ? Cheb(u.Position, h) : int.MaxValue;
                return Cheb(u.Position, house.At) < currentDist;
            })
            .OrderBy(u => Cheb(u.Position, house.At)).ThenBy(u => u.Id)
            .ToList();   // materialize — SetHome mutates resident counts mid-iteration
        foreach (var u in candidates)
        {
            if (cap > 0 && house.ResidentCount >= cap) break;
            Sim.Core.Population.Population.SetHome(sim, u, house.At);
        }
    }

    // Catalog dispatch. Every player-buildable kind needs a row here.
    private static Structure BuildStructure(StructureKind kind, TileCoord at, int ownerId, TileCoord? dockSlip) => kind switch
    {
        StructureKind.Stockpile  => new Stockpile(at) { OwnerId = ownerId },
        StructureKind.LumberCamp => new Extractor(StructureKind.LumberCamp, at) { OwnerId = ownerId },
        StructureKind.Quarry     => new Extractor(StructureKind.Quarry, at) { OwnerId = ownerId },
        StructureKind.Mine       => new Extractor(StructureKind.Mine, at) { OwnerId = ownerId },
        StructureKind.Farm       => new Extractor(StructureKind.Farm, at) { OwnerId = ownerId },
        StructureKind.Tower      => new Tower(at) { OwnerId = ownerId },
        StructureKind.House      => new House(at) { OwnerId = ownerId },
        StructureKind.School     => new School(at) { OwnerId = ownerId },
        StructureKind.Barracks   => new Barracks(at) { OwnerId = ownerId },
        StructureKind.Dock       => new Dock(at, dockSlip
            ?? throw new InvalidOperationException(
                $"Dock at {at.X},{at.Y} has no DockSlip recorded on its ConstructionSite."))
            { OwnerId = ownerId },
        _ => throw new InvalidOperationException(
            $"BuildCompleteEvent has no constructor for {kind} — extend BuildStructure when a new player-buildable kind lands."),
    };

    public override string Describe() => $"BuildComplete(@ {SiteTile.X},{SiteTile.Y})";
}
