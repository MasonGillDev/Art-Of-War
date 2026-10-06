namespace Sim.Core.Logistics;

// THE ONE completion path for a ConstructionSite. Called from
// BuildCompleteEvent (the ordinary route: materials hauled, builders
// assigned, the projected finish tick fired and passed its three gates) and
// from the placement intents when the owner is in god mode (docs/god-mode.md:
// the site never enters the pending state, so the gates have nothing to
// check). Everything that "a finished build" MEANS lives here exactly once —
// site removal, builder release, canal flooding, rubble clearing, the built
// structure with its claim transfer, manning, vision, dock arming and house
// move-in — so the two routes cannot drift.
//
// Precondition: `site` is in world.Structures at site.At. The caller has
// already decided the build is done; this never re-checks materials or
// builders.
public static class Construction
{
    // God mode (docs/god-mode.md): a flagged player's placements skip the
    // haul + build phases and complete on the spot. Read by the placement
    // intents right after their ordinary validation and site insertion.
    public static bool IsGodBuild(GameWorld world, int playerId) =>
        world.Players.TryGetValue(playerId, out var p) && p.GodMode;

    public static void Complete(Simulation sim, ConstructionSite site)
    {
        var world = sim.World;
        // Free the builders, swap site for the built structure.
        var builderIds = new List<int>();
        foreach (var u in world.Units.Values)
        {
            if (u.Position == site.At && u.Activity == Activity.Building && u.Assignment == site.At)
                builderIds.Add(u.Id);
        }
        foreach (var id in builderIds)
            world.Units[id].TrySetActivity(Activity.Idle);

        world.Structures.Remove(site.At);

        // M30 — the site is gone; any builder still walking toward it has
        // nothing to build. Dissolve now rather than letting them arrive at a
        // finished structure and puzzle it out there.
        Sim.Core.Intents.GoalRules.OnStructureRemoved(sim, site.At, "build already finished");

        // M21 — a Canal build produces NO structure: it floods its path of
        // tiles into Water and irrigates the surrounding land. Handle it
        // before the structure-producing path and return.
        if (site.TargetKind == StructureKind.Canal)
        {
            CompleteCanal(sim, site);
            return;
        }

        // M26 — a rubble-CLEARING job produces no structure either: the site
        // was the work vehicle (ClearRubbleIntent swapped it in over the
        // pile), and completion leaves the tile EMPTY — reclaimed ground,
        // buildable again. Builders were already freed above.
        if (site.TargetKind == StructureKind.Rubble)
            return;

        // OwnerId inherits from the ConstructionSite (which got it from
        // PlaceSiteIntent's PlayerId at submission time).
        var built = BuildStructure(site.TargetKind, site.At, site.OwnerId, site.DockSlip);
        built.Facing = site.Facing;   // as placed (docs/structure-footprints.md)
        // M15 — the claim reserved at placement transfers to the finished
        // extractor. COPY (AddRange of value-type coords), never alias the
        // site's list. Sites never produce, so no degradation rate changes
        // at transfer — no catch-up needed (docs/extraction-claims.md).
        if (built is Extractor builtExtractor && site.ClaimTiles.Count > 0)
            builtExtractor.ClaimTiles.AddRange(site.ClaimTiles);
        world.AddStructure(built);
        // M30 — THE MANNING SUB-GOAL. The player bound this worker when they
        // placed the site; completion is the moment that decision executes.
        // They are released from whatever they were doing (the player already
        // chose this post over that one) and walk over. Nobody is substituted
        // if they are gone — the structure simply stands unmanned and says so.
        // docs/goal-shaped-intents.md.
        if (site.WorkerToManId is { } manId && built is Extractor)
        {
            if (world.Units.TryGetValue(manId, out var manner)
                && manner.OwnerId == built.OwnerId
                && !Sim.Core.Groups.GroupRules.UnderCommand(world, manner)
                && !manner.IsEmbarked)
            {
                if (manner.Activity != Activity.Idle) WorkAssignment.Release(sim, manner);
                Sim.Core.Intents.GoalRules.Begin(sim, manner,
                    new GoalPlan(GoalKind.AssignWorker, site.At));
            }
            else
            {
                sim.Schedule(sim.Now, new Sim.Core.Intents.GoalDissolvedEvent(
                    manId, GoalKind.AssignWorker, site.At, "bound worker is gone"));
            }
        }

        // M3 Phase B: if the new structure is a vision source (Castle /
        // Tower), reveal its area for the owner.
        var visionRadius = Sight.RadiusFor(built.Kind);
        if (visionRadius > 0)
        {
            Sight.Reveal(world, built.OwnerId, built.At, visionRadius, sim.Now);
            Sim.Core.Scouting.Charts.OnSight(sim, built.OwnerId, built.At, visionRadius);   // M38 (Bump below checks progression)
        }
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
        // Rest healing: a shelter finished under its owner's wounded (its
        // builders, say) starts them healing (docs/unit-healing.md).
        if (StructureCatalog.Spec(built.Kind).Shelters)
            Sim.Core.Healing.Rest.ArmAllOn(sim, built.At);

        // M37 — last, once the structure fully stands: it counts toward the
        // owner's progress, and a tower's reveal may have moved the explored
        // gauge (Bump runs the check).
        Sim.Core.Progression.Progression.Bump(sim, built.OwnerId,
            Sim.Core.Progression.ProgressKey.Completed(built.Kind));
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

    // M21 — flood a finished canal (docs/canals.md). THE ORDER IS LOAD-
    // BEARING (architecture §2.5, biome-degradation transition discipline):
    //
    //   1. Catch up the fertility of every tile whose water proximity is about
    //      to change, UNDER THE OLD (pre-water) rate, anchoring lastUpdate=now
    //      — while the grid is still un-mutated. Doing this AFTER the flood
    //      would re-interpret the whole pre-canal elapsed time under the new
    //      recovery rate (the anchor-discipline trap).
    //   2. Only then mutate the grid: each path tile becomes Water and leaves
    //      the F/G/D ladder. Drop its now-meaningless Fertility entry (keeps
    //      the sparse dict canonical) and every road arc touching it (roads
    //      never live on water).
    //   3. Reveal the freshly-flooded tiles for the owner.
    //
    // No resulting structure: a canal "is just more water to sail through"
    // (docs/boats.md). The site was already removed by Complete; builders were
    // already freed. Existing boat movement (Biome.Water == cost-6 for boats)
    // and the M21 water-restores-land rule both compose for free.
    private static void CompleteCanal(Simulation sim, ConstructionSite site)
    {
        var world = sim.World;
        var path = site.CanalPath;

        // 1. Transition catch-up under the OLD rate (grid still un-mutated).
        BiomeDegradation.OnWaterProximityChanged(
            world, path, sim.Now, world.BiomeDegradationConfig);

        // 2. Mutate terrain.
        foreach (var p in path)
        {
            world.Grid.SetBiome(p, Biome.Water);
            world.Fertility.Remove(p);
            Sim.Core.Roads.Road.RemoveOnTile(world, p);
        }

        // 2b. Banks (docs/structure-footprints.md): each dug tile carries a Canal
        // structure, whose channel feet can't cross.
        foreach (var p in path)
            world.AddStructure(new Canal(p) { OwnerId = site.OwnerId });

        // 3. Reveal the new water for the owner.
        foreach (var p in path)
        {
            Sight.Reveal(world, site.OwnerId, p, 1, sim.Now);
            Sim.Core.Scouting.Charts.OnSight(sim, site.OwnerId, p, 1);   // M38
        }
        Sim.Core.Progression.Progression.Check(sim, site.OwnerId);   // M37 — explored gauge
    }

    // Catalog dispatch. Every player-buildable kind needs a row here.
    // Every extractor kind (farms, camps, quarries, the mines, the smelter) is one
    // row: StructureSpec.IsExtractor.
    private static Structure BuildStructure(StructureKind kind, TileCoord at, int ownerId, TileCoord? dockSlip) => kind switch
    {
        _ when StructureCatalog.TryGetSpec(kind, out var spec) && spec.IsExtractor
                                 => new Extractor(kind, at) { OwnerId = ownerId },
        StructureKind.Stockpile  => new Stockpile(at) { OwnerId = ownerId },
        StructureKind.Tower      => new Tower(at) { OwnerId = ownerId },
        StructureKind.Bridge     => new Bridge(at) { OwnerId = ownerId },
        StructureKind.House      => new House(at) { OwnerId = ownerId },
        StructureKind.School     => new School(at) { OwnerId = ownerId },
        StructureKind.Barracks   => new Barracks(at) { OwnerId = ownerId },
        StructureKind.Lodge      => new Lodge(at) { OwnerId = ownerId },
        StructureKind.Workshop   => new Workshop(at) { OwnerId = ownerId },
        StructureKind.Smithy     => new Smithy(at) { OwnerId = ownerId },
        // M26 — fortifications. Blocking starts NOW (entry-only): a builder
        // still standing here can walk off, but nobody re-enters while the
        // wall stands. docs/walls-and-gates.md.
        StructureKind.Wall       => new Wall(at) { OwnerId = ownerId },
        StructureKind.Gate       => new Gate(at) { OwnerId = ownerId },
        StructureKind.Dock       => new Dock(at, dockSlip
            ?? throw new InvalidOperationException(
                $"Dock at {at.X},{at.Y} has no DockSlip recorded on its ConstructionSite."))
            { OwnerId = ownerId },
        _ => throw new InvalidOperationException(
            $"Construction has no constructor for {kind} — extend BuildStructure when a new player-buildable kind lands."),
    };

}
