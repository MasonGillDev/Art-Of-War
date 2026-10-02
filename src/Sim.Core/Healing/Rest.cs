using Sim.Core.Combat;

namespace Sim.Core.Healing;

// Rest healing (docs/unit-healing.md). A wounded unit heals by resting on a
// tile holding its OWN shelter (StructureSpec.Shelters: Castle, House,
// Barracks), off any battlefield and not aboard a boat. It heals a flat
// HealPerPeriod at the end of every COMPLETED period of uninterrupted rest,
// never past CombatRules.MaxHealth. Free: no food, no workers.
//
// Balance knobs, not world-serialized config: same precedent as
// HousingConstants / RoadConstants. Tests derive from these.
public static class RestConstants
{
    // One rest period: a game-hour.
    public const long PeriodTicks = 1 * Time.Hour;

    // HP restored per completed period. Flat for every role (user decision,
    // 2026-10-02): a 40-HP shielded soldier is a long patch-up, a 10-HP
    // citizen a short one.
    public const int HealPerPeriod = 1;
}

// The rules and the ONLY writer of the Unit.NextRestHeal* anchor (besides
// Snapshot restore). Self-rescheduling with re-arm (architecture §2.4): a
// resting unit owns one queued RestHealEvent; the event goes dormant when
// the unit is no longer resting; the events that change where a unit stands
// (TileEntry.Enter, a battle closing, a shelter finishing, a disembark) call
// ArmIfDormant.
public static class Rest
{
    // PURE READ. On its own shelter's tile, off the board, not embarked.
    public static bool IsSheltered(GameWorld world, Unit u) =>
        !u.IsEmbarked
        && u.Board is null
        && world.Structures.TryGetValue(u.Position, out var s)
        && s.OwnerId == u.OwnerId
        && StructureCatalog.Spec(s.Kind).Shelters;

    // PURE READ. Sheltered and below full health: rest has something to do.
    public static bool IsResting(GameWorld world, Unit u, long now) =>
        IsSheltered(world, u) && u.Health < CombatRules.MaxHealth(u, now);

    // Start the clock for a unit that has just come to rest. A unit already
    // armed keeps its running period.
    public static void ArmIfDormant(Simulation sim, Unit u)
    {
        if (u.NextRestHealTick is not null) return;
        if (!IsResting(sim.World, u, sim.Now)) return;
        Schedule(sim, u);
    }

    // Every own unit standing on `tile` (a shelter that just finished, a
    // battle that just closed). Units in id order.
    public static void ArmAllOn(Simulation sim, TileCoord tile)
    {
        foreach (var u in sim.World.Units.Values)
            if (u.Position == tile) ArmIfDormant(sim, u);
    }

    // The rest was broken (the unit left the tile, joined a battle, boarded a
    // boat). Clearing the anchor fences the queued event, so a fresh rest
    // always starts a full period: a passer-by never heals.
    public static void Interrupt(Unit u)
    {
        u.NextRestHealTick = null;
        u.NextRestHealSeq = null;
    }

    internal static void Schedule(Simulation sim, Unit u)
    {
        var at = sim.Now + RestConstants.PeriodTicks;
        u.NextRestHealTick = at;
        u.NextRestHealSeq = sim.Schedule(at, new RestHealEvent(u.Id));
    }
}
