using Sim.Core.World;

namespace Sim.Core.Logistics;

// Per-role cargo capacity. Lookup mirrors UnitCombatCatalog: a single
// static table, one Spec(role) entry point, append-only when new
// roles arrive.
//
// Why derived from role (not stored on the Unit):
//   Training is the gameplay reason haulers exist — turning a Builder
//   into a Hauler should *immediately* give them the carry buff. When
//   capacity was a stored init-only field, TrainUnitIntent flipped the
//   role but the cargo cap stayed at whatever the unit was spawned
//   with. Deriving it from the current Role means the buff flips with
//   the role, atomically, with no second mutation to keep in sync.
//
// Values:
//   - Hauler  : 25 — the role's reason for being.
//   - Boat    : 100 — bulk freight (matches the M12 BoatConstants value).
//   - everyone else (None/Builder/Farmer/Miner/Lumberjack/Quarryman/Scout)
//                : 5 — civilians can lug a small load when asked, but
//                hauling at scale is the Hauler's job.
//
// Tuning these later is fine; the *shape* (one number per role,
// catalog-style) is what this file pins.
public static class UnitCargoCatalog
{
    public const int HaulerCapacity = 25;
    public const int BoatCapacity = 100;
    public const int DefaultCapacity = 5;

    public static int CapacityFor(UnitRole role) => role switch
    {
        UnitRole.Hauler => HaulerCapacity,
        UnitRole.Boat => BoatCapacity,
        _ => DefaultCapacity,
    };
}
