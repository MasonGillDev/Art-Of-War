namespace Sim.Core.Logistics;

// Assigns one or more units to build at a ConstructionSite, then — if the
// site's conditions are now fully met (materials + builder count) — triggers
// StartOrResume on the site (which schedules the BuildCompleteEvent).
//
// Per-id validation (per docs/intent-validation.md):
//   * Unit exists.
//   * Unit is on SiteTile.
//   * Unit.Role == UnitRole.Builder.
//   * Unit.Activity == Idle.
// Failing ids are skipped; valid ones still assign ("partial success" — the
// "fail cleanly" rule applies per assignment, not per intent).
//
// Intent rejected only when nothing changed: site missing, or zero
// assignments were made AND no start was triggered.
public sealed class AssignBuildersIntent : Intent
{
    public TileCoord SiteTile { get; }
    public IReadOnlyList<int> BuilderIds { get; }

    public AssignBuildersIntent(TileCoord siteTile, IReadOnlyList<int> builderIds)
    {
        SiteTile = siteTile;
        BuilderIds = builderIds;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(SiteTile, out var s) || s is not ConstructionSite site)
            return IntentOutcome.Reject($"no construction site at {SiteTile.X},{SiteTile.Y}");

        var assigned = 0;
        foreach (var id in BuilderIds)
        {
            if (!world.Units.TryGetValue(id, out var unit)) continue;
            if (unit.Position != SiteTile) continue;
            if (unit.Role != UnitRole.Builder) continue;
            if (unit.Activity != Activity.Idle) continue;
            if (!unit.TrySetActivity(Activity.Building, SiteTile)) continue;
            assigned++;
        }

        var triggered = false;
        if (!site.IsActive && site.ConditionsMet(world))
        {
            site.StartOrResume(sim);
            triggered = true;
        }

        if (assigned == 0 && !triggered)
            return IntentOutcome.Reject("no eligible builders and no build start triggered");

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"AssignBuilders(@ {SiteTile.X},{SiteTile.Y}, ids=[{string.Join(",", BuilderIds)}])";
}
