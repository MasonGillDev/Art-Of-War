using Sim.Core.World;

namespace Sim.Core.Battlefields;

// The doctrines a unit may stand on (docs/battle-sandbox.md, "Doctrines are a
// catalogue"). ONE table, read by the intent that sets a doctrine, the genesis wire
// (so the game's battle panel and the sandbox inspector build their choices from
// it), and nothing else. A new doctrine is its rule in TurnPlanner.Doctrine plus one
// entry here; no client names it.
//
// Roles: null = anyone may stand on it. A non-null list is a whitelist, for a
// doctrine that only makes sense for some bodies. Default doctrines
// (BattleDoctrine.DefaultFor) are always allowed, so a list can never lock a unit
// out of its own default.
//
// TakesWithdrawBelow: whether the "withdraw below N of us" threshold means anything
// on top of this behaviour. On Withdraw it doesn't: the unit is leaving anyway.
public sealed record DoctrineSpec(
    DoctrineBehaviour Behaviour,
    string Name,
    string Description,
    bool TakesWithdrawBelow,
    IReadOnlyList<UnitRole>? Roles = null);

public static class DoctrineCatalog
{
    // In DoctrineBehaviour order; the wire sends it in this order.
    public static readonly IReadOnlyList<DoctrineSpec> All =
    [
        new(DoctrineBehaviour.Hold, "Hold",
            "With no order: stay and fight whatever comes.", TakesWithdrawBelow: true),
        new(DoctrineBehaviour.Advance, "Advance",
            "With no order: close on the nearest enemy.", TakesWithdrawBelow: true),
        new(DoctrineBehaviour.Support, "Support",
            "With no order: stand next to a friend who is duelling.", TakesWithdrawBelow: true),
        new(DoctrineBehaviour.Withdraw, "Withdraw",
            "With no order: leave the battle.", TakesWithdrawBelow: false),
    ];

    public static bool TryGet(DoctrineBehaviour behaviour, out DoctrineSpec spec)
    {
        foreach (var s in All)
            if (s.Behaviour == behaviour) { spec = s; return true; }
        spec = null!;
        return false;
    }

    // Why this role can't stand on this doctrine, or null when it can.
    public static string? Blocker(UnitRole role, DoctrineBehaviour behaviour)
    {
        if (!TryGet(behaviour, out var spec)) return $"unknown doctrine {(byte)behaviour}";
        if (spec.Roles is null || spec.Roles.Contains(role)) return null;
        if (BattleDoctrine.DefaultFor(role).Behaviour == behaviour) return null;
        return $"a {role} cannot use the {spec.Name} doctrine";
    }
}
