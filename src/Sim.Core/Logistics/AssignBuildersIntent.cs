namespace Sim.Core.Logistics;

// GOAL-SHAPED (M30): a named builder already standing on the site starts
// building now; one standing anywhere else WALKS THERE AND STARTS ON ARRIVAL
// (docs/goal-shaped-intents.md). Then — if the site's conditions are fully met
// (materials + builder count) — triggers StartOrResume on the site (which
// schedules the BuildCompleteEvent).
//
// The other half of that conjunction was already sim-driven: a materials
// delivery arriving last starts the build by itself (CargoTransfer). M30 makes
// the builder-arriving-last case symmetrical, so the two orders converge.
//
// Per-id validation (per docs/intent-validation.md):
//   * Unit exists, owned, not grouped, not embarked, of training age.
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

    [System.Text.Json.Serialization.JsonConstructor]
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
        if (site.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"construction site at {SiteTile.X},{SiteTile.Y} not owned by player {PlayerId}");

        var assigned = 0;
        var dispatched = 0;
        foreach (var id in BuilderIds)
        {
            if (!world.Units.TryGetValue(id, out var unit)) continue;
            if (unit.OwnerId != PlayerId) continue;  // skip non-owned silently per per-id pattern
            if (unit.GroupId is not null) continue;  // grouped units can't be assigned solo
            if (unit.IsEmbarked) continue;            // embarked units are off-tile
            if (unit.Role != UnitRole.Builder) continue;
            if (unit.Activity != Activity.Idle) continue;

            if (unit.Position == SiteTile)
            {
                if (WorkAssignment.TryAssignBuilder(sim, site, unit)) assigned++;
            }
            else if (GoalRules.Begin(sim, unit, new GoalPlan(GoalKind.AssignBuilder, SiteTile)))
            {
                dispatched++;
            }
        }

        var triggered = false;
        if (!site.IsActive && site.ConditionsMet(world))
        {
            site.StartOrResume(sim);
            triggered = true;
        }

        if (assigned == 0 && dispatched == 0 && !triggered)
            return IntentOutcome.Reject("no eligible builders and no build start triggered");

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"AssignBuilders(@ {SiteTile.X},{SiteTile.Y}, ids=[{string.Join(",", BuilderIds)}])";
}
