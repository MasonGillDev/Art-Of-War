using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Hauling;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Tests;

// TEST-ONLY shorthand for the two-step "make a group here": CreateGroupIntent, then
// MusterGroupIntent at a tile, in one resolution. The game has no such intent: the
// player creates a group once and musters it when needed (FormGroupIntent was removed,
// docs/client-intent-migration.md). Tests whose subject is something else — a march,
// the wire, a battle — use this so they read as before.
public sealed class CreateAndMuster : Intent
{
    public IReadOnlyList<int> UnitIds { get; }
    public TileCoord At { get; }

    public CreateAndMuster(IReadOnlyList<int> unitIds, TileCoord at)
    {
        UnitIds = unitIds;
        At = at;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var id = sim.World.NextGroupId;
        var created = new CreateGroupIntent("", UnitIds) { PlayerId = PlayerId }.Resolve(sim);
        if (created.IsRejected) return created;
        return new MusterGroupIntent(id, At) { PlayerId = PlayerId }.Resolve(sim);
    }
}

public static class TestGroups
{
    // A crew of exactly `members` on `route`, starting at `startStop`: a group created for
    // them, then put on the route. Returns the group's id.
    public static int Crew(Simulation sim, int route, int startStop, IEnumerable<int> members, int player = 0)
    {
        var id = sim.World.NextGroupId;
        var created = new CreateGroupIntent("", members.ToList()) { PlayerId = player }.Resolve(sim);
        Xunit.Assert.True(created.IsApplied, created.Reason);
        var assigned = new AssignGroupToRouteIntent(id, route, startStop) { PlayerId = player }.Resolve(sim);
        Xunit.Assert.True(assigned.IsApplied, assigned.Reason);
        return id;
    }
}
