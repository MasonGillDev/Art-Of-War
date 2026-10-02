using Sim.Core.Battlefields;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Groups;

// M49 — WHAT EACH STANCE MEANS (docs/m49-group-stance-spec.md). The player picks one of
// three; everything below is defined here, in one place:
//
//   Passive    — never starts a fight, nobody comes when a member is attacked, and on a
//                battlefield every member withdraws.
//   Defensive  — never starts a fight; when a member is attacked, every company of the same
//                army within AidRadius marches to the fight and joins it, then goes back.
//                On a battlefield: the role defaults (soldiers hold, archers support).
//   Aggressive — as Defensive, and it charges a visible hostile within EngageRadius,
//                chasing no further than LeashRadius from where it was
//                (Sim.Server GroupStanceDriver spots; ChargeGroupIntent marches).
//                On a battlefield: soldiers advance, archers support.
//
// A stance acts only while its group has its members under command; a dismissed group's
// members are free individuals.
public static class GroupStances
{
    // The stance a unit fights under, or null when it isn't under a group's command.
    public static GroupStance? Of(GameWorld world, Unit u) =>
        u.GroupId is { } gid && world.Groups.TryGetValue(gid, out var g) && g.State != GroupState.Dismissed
            ? g.Stance
            : null;

    // What a stance makes of a role on a battlefield.
    public static BattleDoctrine DoctrineFor(GroupStance stance, UnitRole role) => stance switch
    {
        GroupStance.Passive => BattleDoctrine.Withdraw,
        GroupStance.Aggressive when role == UnitRole.Soldier => BattleDoctrine.Advance,
        _ => BattleDoctrine.DefaultFor(role),
    };

    // ---- aid ------------------------------------------------------------------------

    // A member of a group under command was just put on a battlefield at `tile`: every
    // company of its army that is near enough, and not Passive, comes to help.
    internal static void OnMemberEnrolled(Simulation sim, Unit u, TileCoord tile)
    {
        var world = sim.World;
        if (u.GroupId is not { } gid || !world.Groups.TryGetValue(gid, out var leaf)) return;
        if (leaf.State == GroupState.Dismissed || leaf.Stance == GroupStance.Passive) return;
        foreach (var company in GroupMuster.Leaves(world, Root(world, leaf)))
        {
            if (company.Stance == GroupStance.Passive || company.State is not (GroupState.Idle or GroupState.Moving)) continue;
            if (company.ReturnTo is not null || AnyFighting(world, company)) continue;
            if (Chebyshev(company.Position, tile) > GroupConstants.AidRadius) continue;
            if (GroupMarch.Plan(world, company, tile, sim.Now) is not { } path) continue;
            var back = company.State == GroupState.Moving && company.PathFinalDest is { } dest ? dest : company.Position;
            MoveGroupIntent.Halt(sim, company);
            company.BumpEpoch();
            company.ReturnTo = back;
            GroupMarch.Begin(sim, company, path);
        }
    }

    // ---- return ---------------------------------------------------------------------

    // A group away helping or charging goes back once it stands still, with nobody in a
    // fight and no battle where it is. A route crew goes back to its route instead (the
    // hauling driver marches it to its stop).
    internal static void ReturnIfDone(Simulation sim, Group group)
    {
        var world = sim.World;
        if (group.ReturnTo is not { } back || group.State != GroupState.Idle) return;
        if (AnyFighting(world, group) || world.Battlefields.ContainsKey(group.Position)) return;
        group.ReturnTo = null;
        if (back == group.Position || Sim.Core.Hauling.RouteCrews.Find(world, group) is not null && !group.RouteSuspended) return;
        if (GroupMarch.Plan(world, group, back, sim.Now) is not { } path) return;
        group.BumpEpoch();
        GroupMarch.Begin(sim, group, path);
    }

    // After a board closes: every group that went to help and now stands still goes back.
    // Bounded by the number of groups; runs only when a board closes.
    internal static void ReturnAllDone(Simulation sim)
    {
        foreach (var group in sim.World.Groups.Values.ToList())
            if (group.ReturnTo is not null) ReturnIfDone(sim, group);
    }

    // ---- charge ---------------------------------------------------------------------

    // Where an Aggressive group's leash is measured from: where it was before it set off.
    public static TileCoord LeashAnchor(Group group) =>
        group.ReturnTo ?? (group.State == GroupState.Moving && group.PathFinalDest is { } dest ? dest : group.Position);

    // Why `group` may not charge `target` now, or null. Pure read (fog-fair: the target
    // must stand where the group's owner can see it).
    public static string? ChargeRefusal(GameWorld world, Group group, Unit? target, int targetId)
    {
        if (group.Kind != GroupKind.Units) return $"group {group.Id} holds groups, not units";
        if (group.State is not (GroupState.Idle or GroupState.Moving)) return $"group {group.Id} is not standing or marching";
        if (group.Stance != GroupStance.Aggressive) return $"group {group.Id} is not Aggressive";
        if (AnyFighting(world, group)) return $"group {group.Id} is already fighting";
        if (target is null) return $"unit {targetId} does not exist";
        if (target.IsEmbarked) return $"unit {targetId} is aboard a boat";
        if (!world.Diplomacy.AreHostile(group.OwnerId, target.OwnerId)) return $"unit {targetId} is not hostile";
        if (!View.VisibleTiles(world, group.OwnerId).Contains(target.Position)) return $"unit {targetId} can't be seen";
        if (Chebyshev(LeashAnchor(group), target.Position) > GroupConstants.LeashRadius)
            return $"unit {targetId} is beyond the leash";
        return null;
    }

    internal static void Charge(Simulation sim, Group group, Unit target, List<WorldSubtile> path)
    {
        var anchor = LeashAnchor(group);
        MoveGroupIntent.Halt(sim, group);
        group.BumpEpoch();
        group.ReturnTo = anchor;
        GroupMarch.Begin(sim, group, path);
    }

    // ---- reads ----------------------------------------------------------------------

    public static bool AnyFighting(GameWorld world, Group group)
    {
        foreach (var id in group.Members)
            if (world.Units.TryGetValue(id, out var m) && UnitAvailability.Busy(world, m) == BusyReason.Fighting) return true;
        return false;
    }

    // The army a group belongs to: its topmost group.
    public static Group Root(GameWorld world, Group group)
    {
        var g = group;
        while (g.ParentId is { } p && world.Groups.TryGetValue(p, out var parent)) g = parent;
        return g;
    }

    private static int Chebyshev(TileCoord a, TileCoord b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
