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
// On success the build completes via Construction.Complete — the single
// completion path shared with god-mode placement (docs/god-mode.md).
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

        Construction.Complete(sim, site);
    }

    public override string Describe() => $"BuildComplete(@ {SiteTile.X},{SiteTile.Y})";
}
