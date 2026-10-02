using Sim.Core.World;

namespace Sim.Core.Automation;

// Append-only enum (serialized in snapshots AND in durable intent JSON).
//
// Why the purpose is part of the claim (not a separate flag): the two
// lifetimes are genuinely different, and conditions need to tell them
// apart. A Crew claim is STANDING — the unit belongs to this order until
// released (a caravan's haulers, a farm's assigned workers). An InFlight
// claim is TRANSIENT — the order committed this unit to a specific
// errand (walk to the school and train; walk to the house and breed) and
// releases it when the errand lands.
//
// The distinction is what makes in-flight-aware conditions work:
// "count(Hauler) + inflight(Hauler) < 6" counts trainees already walking
// to a school, so a second school reads the quota as met and stands down
// instead of racing (docs/automation-substrate.md, Layer 0).
public enum ClaimPurpose : byte
{
    Crew     = 1, // standing membership — released on Clear / re-crew
    InFlight = 2, // transient errand — released when the errand completes
}

// THE CLAIMS LEDGER (docs/automation-substrate.md) — the keystone of the
// automation substrate.
//
// A unit is claimed the instant an order COMMITS to it ("claim-on-commit"),
// not when its work finishes. That single rule is what stops two orders
// from grabbing the same dormant unit and routing it in opposite
// directions: the pool shrinks the moment a decision is made, and every
// later evaluation in the same think pass sees the smaller pool.
//
// A unit may hold AT MOST ONE claim (enforced by the ledger's unit-keyed
// index — see GameWorld.Claims). Claims do NOT lock a unit: manual player
// intents always win, exactly as M18's ClaimedUnits behaved. A claim is a
// statement about which order is RESPONSIBLE for a unit, not a mutex on
// the unit's body.
//
// Serialized state, mutated ONLY by ClaimUnitIntent (server-internal,
// durable) — so a pull that was in flight when the process died resumes
// after restore instead of being re-issued against a unit that is already
// walking. Same durability contract as M18's AdvanceOrderCursorIntent.
public readonly record struct Claim(int UnitId, int OrderId, ClaimPurpose Purpose);

// Read helpers over the ledger. Pure — never mutate. The single place
// "is this unit available?" is answered, so every selector, condition,
// and test agrees by construction.
public static class ClaimLedger
{
    // THE DORMANCY RULE (docs/automation-substrate.md, Layer 0): dormant is
    // DERIVED, never stored — a stored flag would need a mutation point at
    // every activity/group/claim transition and would drift from the truth
    // the first time one was missed.
    //
    // Dormant = idle body, free hands, nobody's responsibility:
    //   * Activity.Idle          — not working / building / hauling
    //   * not marching           — anchors are the truth, never Activity
    //                             (the M16 pitfall: a marching unit reads
    //                             Idle). Unit.IsWalking.
    //   * not in a Group         — group members move as one; pulling one
    //                             out from under an order is not dormancy
    //   * not embarked           — aboard a boat is not available
    //   * not breeding           — locked in a house for the gestation
    //   * not claimed            — someone already committed to them
    //   * not on a haul route    — M36: a route crew belongs to its loop
    //
    // "Alive" is NOT a clause, because PRESENCE IN world.Units IS LIVENESS:
    // every death path (age, combat, starvation) removes the unit from the
    // table. Unit.DeathTick is the opposite of a tombstone — it is the
    // SCHEDULED future death from old age, pre-rolled at genesis, so on a
    // real server EVERY unit carries one from tick 0. The first cut of this
    // predicate read it as "already dead" and every unit in a real game was
    // filtered as a corpse — the whole substrate ran only in unit tests,
    // whose hand-built worlds skip the lifespan roll. Pinned by
    // SubstrateLifespanTests.
    //
    // Age/role/protection are NOT part of dormancy: those are per-selector
    // FILTERS (a breeding pull wants fertile adults; a training pull wants
    // no-role trainables). Dormancy answers "are they available?", the
    // selector answers "are they the right kind?".
    public static bool IsDormant(GameWorld world, Unit unit) =>
        unit.Activity == Activity.Idle
        && !unit.IsWalking
        && !Sim.Core.Groups.GroupRules.UnderCommand(world, unit)
        && !unit.IsEmbarked
        && !world.Claims.ContainsKey(unit.Id)
        && unit.HaulPlan is null  // M36: mid-trip is not available, whatever Activity says
        && Sim.Core.Population.Population.GetActiveBreedingFor(world, unit.Id) is null;

    // Is this unit spoken for by any order?
    public static bool IsClaimed(GameWorld world, int unitId) =>
        world.Claims.ContainsKey(unitId);

    public static Claim? ClaimOf(GameWorld world, int unitId) =>
        world.Claims.TryGetValue(unitId, out var c) ? c : null;

    // How many units this order currently holds, optionally of one purpose.
    // O(claims) — the ledger is small (bounded by units under automation)
    // and this runs inside driver thinks, never in a hot loop.
    public static int HeldBy(GameWorld world, int orderId, ClaimPurpose? purpose = null)
    {
        var n = 0;
        foreach (var (_, c) in world.Claims)
            if (c.OrderId == orderId && (purpose is null || c.Purpose == purpose)) n++;
        return n;
    }

    // The in-flight count for a role — the number the quota conditions add
    // to the live headcount so redundant producers don't race. Counts units
    // an order has committed to PRODUCING as this role, which is recorded on
    // the claim's order, not on the unit (a trainee walking to school is
    // still role None). Callers pass the predicate that maps an order id to
    // "is this order producing role R" — the substrate's recipe layer owns
    // that mapping; the ledger just counts.
    public static int InFlight(GameWorld world, Func<int, bool> orderProducesRole)
    {
        var n = 0;
        foreach (var (_, c) in world.Claims)
            if (c.Purpose == ClaimPurpose.InFlight && orderProducesRole(c.OrderId)) n++;
        return n;
    }

    // Units held by an order, ascending by unit id (canonical iteration for
    // any caller that needs a stable sequence).
    public static List<int> UnitsOf(GameWorld world, int orderId)
    {
        var ids = new List<int>();
        foreach (var (unitId, c) in world.Claims)   // SortedDictionary → id order
            if (c.OrderId == orderId) ids.Add(unitId);
        return ids;
    }
}
