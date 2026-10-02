namespace Sim.Core.World;

// THE ONE ANSWER to "is this unit busy, and with what?" (2026-10-02).
//
// Before this, every system worked it out for itself from Unit.Activity plus its own
// pick of extra checks — the automation's dormancy, the hauling driver, the bandit
// driver, a route stop's serve, a group's muster, the AI — and the picks drifted
// apart. Only the bandit driver knew that a unit in a fight is not free, so a route
// crew could be loaded mid-battle and marched off without its fighter. Activity alone
// can't say: walking, fighting and riding a boat all read Idle.
//
// DERIVED, NOT STORED. Every reason below is read off state that already exists, so
// the answer can never fall out of step with the unit; a stored flag would have to be
// written at every step, every battle joined or left, every haul event. Pure read.
//
// What this does NOT answer is whose the unit is: under a group's command
// (GroupRules.UnderCommand) or claimed by an automation order (ClaimLedger) are the
// callers' own business, layered on top.
public static class UnitAvailability
{
    // Why the unit is busy, or None when it is free to be given something to do.
    // First match wins, most pressing first.
    public static BusyReason Busy(GameWorld world, Unit u)
    {
        // In a fight until it dies or leaves the battle's tile: on the board, or waiting
        // at its edge for room (docs/battlefield-grid.md).
        if (u.Board is not null || (!u.IsEmbarked && world.Battlefields.ContainsKey(u.Position)))
            return BusyReason.Fighting;
        if (u.IsEmbarked) return BusyReason.Aboard;
        if (Sim.Core.Population.Population.GetActiveBreedingFor(world, u.Id) is not null) return BusyReason.Breeding;
        if (u.Survey is not null) return BusyReason.Surveying;
        if (world.ScoutMissions.TryGetValue(u.Id, out var mission)
            && mission.State != Sim.Core.Scouting.ScoutMissionState.Returned) return BusyReason.Scouting;
        if (u.Pursuit is not null) return BusyReason.Chasing;
        if (u.HaulPlan is not null) return BusyReason.Hauling;
        if (u.Goal is not null) return BusyReason.OnAnErrand;
        if (u.Activity == Activity.Working) return BusyReason.Working;
        if (u.Activity == Activity.Building) return BusyReason.Building;
        if (u.Activity != Activity.Idle) return BusyReason.OnAnErrand;   // Hauling/Waiting without a plan: an anchor's tail
        if (u.IsWalking) return BusyReason.Walking;
        return BusyReason.None;
    }

    public static bool IsFree(GameWorld world, Unit u) => Busy(world, u) == BusyReason.None;

    // The player-facing word for a reason ("fighting", "aboard a boat", ...).
    public static string Describe(BusyReason reason) => reason switch
    {
        BusyReason.Fighting   => "fighting",
        BusyReason.Aboard     => "aboard a boat",
        BusyReason.Breeding   => "breeding",
        BusyReason.Surveying  => "surveying",
        BusyReason.Scouting   => "scouting",
        BusyReason.Chasing    => "chasing",
        BusyReason.Hauling    => "hauling",
        BusyReason.OnAnErrand => "on an errand",
        BusyReason.Working    => "working",
        BusyReason.Building   => "building",
        BusyReason.Walking    => "walking",
        _                     => "free",
    };
}

// Append-only (crosses the wire as an int on UnitDto.Busy).
public enum BusyReason : byte
{
    None       = 0,
    Fighting   = 1,
    Aboard     = 2,
    Breeding   = 3,
    Surveying  = 4,
    Scouting   = 5,
    Chasing    = 6,
    Hauling    = 7,
    OnAnErrand = 8,
    Working    = 9,
    Building   = 10,
    Walking    = 11,
}
