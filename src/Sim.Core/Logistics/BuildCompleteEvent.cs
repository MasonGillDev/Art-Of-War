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
        world.AddStructure(BuildStructure(site.TargetKind, SiteTile));
    }

    // Catalog dispatch. Every player-buildable kind needs a row here.
    private static Structure BuildStructure(StructureKind kind, TileCoord at) => kind switch
    {
        StructureKind.Stockpile  => new Stockpile(at),
        StructureKind.LumberCamp => new Extractor(StructureKind.LumberCamp, at),
        StructureKind.Quarry     => new Extractor(StructureKind.Quarry, at),
        StructureKind.Mine       => new Extractor(StructureKind.Mine, at),
        StructureKind.Farm       => new Extractor(StructureKind.Farm, at),
        _ => throw new InvalidOperationException(
            $"BuildCompleteEvent has no constructor for {kind} — extend BuildStructure when a new player-buildable kind lands."),
    };

    public override string Describe() => $"BuildComplete(@ {SiteTile.X},{SiteTile.Y})";
}
