using Sim.Core.Automation;
using Sim.Core.World;

namespace Sim.Server.Automation;

// Evaluates the substrate's dumb predicates against what the order's OWNER
// can see. PURE READ — computed fresh from current state, never writes.
//
// THE FOG CONTRACT (inherited from M18, non-negotiable): a predicate about
// a tile the owner cannot currently see evaluates NOT-MET. Unknown is never
// true, so automation can never react to fogged state. Own-unit facts
// (role counts) need no visibility check — a player always knows their own
// people.
//
// One case per PredicateKind; a new predicate is one enum value + one case
// here + one line of Set-time validation. That is the entire cost of
// growing the trigger vocabulary.
public static class PredicateEvaluator
{
    // `pendingClaims` (unit → order) carries claims the driver has SUBMITTED
    // this pass but which have not resolved into the ledger yet. Quota
    // predicates must count them or two producers racing on one shortfall
    // each see the slot as still empty and both fill it.
    public static bool IsMet(
        GameWorld world,
        Order order,
        in Predicate p,
        IReadOnlySet<TileCoord> visible,
        long now,
        IReadOnlyDictionary<int, int>? pendingClaims = null)
    {
        switch (p.Kind)
        {
            case PredicateKind.Always:
                return true;

            case PredicateKind.StockBelow:
                if (!visible.Contains(p.Tile)) return false;
                return StoredAmount(world, p.Tile, p.Resource, now) < p.Threshold;

            case PredicateKind.StockAtLeast:
                if (!visible.Contains(p.Tile)) return false;
                return StoredAmount(world, p.Tile, p.Resource, now) >= p.Threshold;

            case PredicateKind.RoleCountBelow:
                return RoleCount(world, order, p, pendingClaims)< p.Threshold;

            case PredicateKind.RoleCountAtLeast:
                return RoleCount(world, order, p, pendingClaims)>= p.Threshold;

            // Whole-population gates. Own-faction fact — no fog check (a
            // player always knows how many people they have).
            case PredicateKind.PopulationBelow:
                return Population(world, order.OwnerId) < p.Threshold;

            case PredicateKind.PopulationAtLeast:
                return Population(world, order.OwnerId) >= p.Threshold;

            case PredicateKind.WorkersBelow:
                if (!visible.Contains(p.Tile)) return false;
                return WorkerCount(world, p.Tile) < p.Threshold;

            case PredicateKind.WorkersAtLeast:
                if (!visible.Contains(p.Tile)) return false;
                return WorkerCount(world, p.Tile) >= p.Threshold;

            default:
                // Set-time validation rejects unknown kinds, so this is a
                // can't-happen — fail loudly at the cause.
                throw new InvalidOperationException(
                    $"PredicateEvaluator has no case for PredicateKind {(byte)p.Kind} — " +
                    "was a new predicate added to the enum without an evaluator case?");
        }
    }

    // Whole trigger: DNF — any clause whose predicates ALL hold fires it.
    // An empty trigger is always-fire (the "run continuously" shape).
    public static bool IsMet(
        GameWorld world,
        Order order,
        IReadOnlySet<TileCoord> visible,
        long now,
        IReadOnlyDictionary<int, int>? pendingClaims = null)
    {
        if (order.Trigger.Any.Count == 0) return true;
        foreach (var clause in order.Trigger.Any)
        {
            var all = true;
            foreach (var p in clause.All)
            {
                if (IsMet(world, order, p, visible, now, pendingClaims)) continue;
                all = false;
                break;
            }
            if (all) return true;
        }
        return false;
    }

    // The owner's headcount of a role. With CountInFlight, adds units that
    // OTHER orders have already committed to producing — the anti-race
    // number: a second school reads the quota as covered and stands down
    // instead of training a redundant hauler.
    //
    // In-flight claims count toward the role their ORDER produces (a
    // trainee walking to a school is still role None), which is why the
    // lookup goes through the order table rather than the unit.
    private static int RoleCount(
        GameWorld world, Order order, in Predicate p, IReadOnlyDictionary<int, int>? pendingClaims)
    {
        var n = 0;
        foreach (var (_, u) in world.Units)
            if (u.OwnerId == order.OwnerId && u.Role == p.Role) n++;  // presence == alive

        if (!p.CountInFlight) return n;

        var role = p.Role;   // `in` params can't be captured by a lambda
        var ownerId = order.OwnerId;
        bool Produces(int orderId) =>
            world.Orders.TryGetValue(orderId, out var producer)
            && producer.OwnerId == ownerId
            && producer.SubjectKind == SubjectKind.RoleCount
            && producer.SubjectRole == role;

        n += ClaimLedger.InFlight(world, Produces);

        // Plus this pass's not-yet-resolved commitments. Without these, two
        // orders evaluated in the same think both see the shortfall and both
        // act on it — the trainee walking to school #1 is invisible to #2.
        if (pendingClaims is not null)
            foreach (var (unitId, orderId) in pendingClaims)
                if (!ClaimLedger.IsClaimed(world, unitId) && Produces(orderId)) n++;

        return n;
    }

    // Assigned workers at an extractor. A non-extractor reads 0 — the same
    // "observable truth" rule StoredAmount uses.
    private static int WorkerCount(GameWorld world, TileCoord tile) =>
        world.Structures.TryGetValue(tile, out var s) && s is Extractor e ? e.Workers.Count : 0;

    // Living head count for a faction. Read from the units table rather
    // than Player.PopulationCount so it agrees exactly with what the
    // selectors can actually pull (the same living-units definition).
    private static int Population(GameWorld world, int ownerId)
    {
        var n = 0;
        foreach (var (_, u) in world.Units)
            if (u.OwnerId == ownerId) n++;   // presence == alive
        return n;
    }

    // WHAT A TRIGGER SEES ON A SHELF. StorageStructure holdings, or an
    // Extractor's buffer when the resource matches its output; no structure
    // (or a stockless kind) reads 0 — observable truth, as in M18.
    //
    // For FOOD at a FOOD HOME (Castle/House) this must be the EFFECTIVE level,
    // not raw Holdings. Consumption is lazily caught up (architecture §2.5): a
    // home's Holdings are only decremented at rate-changing events, so between
    // them they overstate the larder by up to a whole period of eating — and
    // during famine the debt is not in Holdings at all. Reading raw Holdings
    // makes a supply line sit still while its destination starves (the castle
    // reads 200 while the realm reads 88), and makes a Breed order think the
    // larder is fuller than it is. FoodConsumption.CurrentLevel is the same
    // pure read the Realm panel shows, and it is period-quantised, so it stays
    // deterministic for a given (world, now).
    public static long StoredAmount(GameWorld world, TileCoord tile, Resource r, long now)
    {
        if (!world.Structures.TryGetValue(tile, out var s)) return 0;
        if (r == Resource.Food && s is Sim.Core.Food.IFoodHome home)
            return Sim.Core.Food.FoodConsumption.CurrentLevel(home, world, now);
        return s switch
        {
            StorageStructure storage => storage.AmountOf(r),
            Extractor e => e.Spec.OutputResource == r ? e.Buffer : 0,
            _ => 0,
        };
    }
}
