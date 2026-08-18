using Sim.Core.Automation;
using Sim.Core.World;

namespace Sim.Server.Automation;

// Resolves a Selector to units — THE mechanical enforcement of the
// zero-judgment law (docs/automation-as-core-game.md).
//
// A WHERE CLAUSE, NEVER A RANKING. The filter decides who is ELIGIBLE; the
// canonical order (distance from anchor → y → x → id) decides who comes
// first. There is no scoring function, no "best fit," no weighing of
// alternatives anywhere in this file, and there must never be one: the
// moment a selector prefers a unit for a *reason*, the tool has started
// making the player's decisions for them.
//
// "Nearest" is a TIEBREAK, not a preference — it exists because the
// sequence must be total and deterministic (two runs over the same world
// must pick identically), and because walking distance is the one ordering
// that is physically obvious to a player watching from the map.
//
// PURE READ: never mutates. Claiming is the caller's job (via
// ClaimUnitIntent), which is what makes claim-on-commit visible to every
// later order in the same think pass.
public static class SelectorResolver
{
    // Eligible units in canonical order. `excluded` lets a caller take
    // several units in one firing without re-reading claims it has only
    // just submitted (intents resolve after the think).
    public static List<Unit> Resolve(
        GameWorld world,
        Order order,
        in Selector selector,
        long now,
        IReadOnlySet<int>? excluded = null)
    {
        var matches = new List<Unit>();
        foreach (var (id, u) in world.Units)   // ascending id
        {
            // Presence in world.Units IS liveness — every death path removes
            // the unit. DeathTick is the SCHEDULED old-age death, set on every
            // genesis unit, and must never be read as "dead" (the bug that
            // made every selector on a real server return nobody).
            if (u.OwnerId != order.OwnerId) continue;
            if (excluded is not null && excluded.Contains(id)) continue;

            // ---- filter: role ----
            if (!selector.AnyRole && u.Role != selector.Role) continue;

            // ---- filter: age WINDOW (trainability floor / fertility band) ----
            if (selector.MinAgeYears > 0 || selector.MaxAgeYears > 0)
            {
                var age = Sim.Core.Population.Population.AgeYears(u, now, world.PopulationConfig);
                if (selector.MinAgeYears > 0 && age < selector.MinAgeYears) continue;
                if (selector.MaxAgeYears > 0 && age > selector.MaxAgeYears) continue;
            }

            // ---- filter: availability ----
            if (selector.RequireDormant)
            {
                if (!ClaimLedger.IsDormant(world, u)) continue;
            }
            else
            {
                // Conscription reach: a working unit may be taken, but a
                // claimed one never is (it belongs to another order), and a
                // Protected one never is (the player marked it sacred).
                if (ClaimLedger.IsClaimed(world, id)) continue;
                if (u.Protected) continue;
            }

            // ---- filter: reach ----
            var dist = Chebyshev(u.Position, selector.Anchor);
            if (selector.Radius > 0 && dist > selector.Radius) continue;

            matches.Add(u);
        }

        // THE CANONICAL ORDER — total, deterministic, judgment-free.
        var anchor = selector.Anchor;   // `in` params can't be captured by a lambda
        matches.Sort((a, b) =>
        {
            var d = Chebyshev(a.Position, anchor)
                .CompareTo(Chebyshev(b.Position, anchor));
            if (d != 0) return d;
            if (a.Position.Y != b.Position.Y) return a.Position.Y.CompareTo(b.Position.Y);
            if (a.Position.X != b.Position.X) return a.Position.X.CompareTo(b.Position.X);
            return a.Id.CompareTo(b.Id);
        });
        return matches;
    }

    // First eligible unit, or null. The common case — one firing, one hand.
    public static Unit? First(
        GameWorld world,
        Order order,
        in Selector selector,
        long now,
        IReadOnlySet<int>? excluded = null)
    {
        var all = Resolve(world, order, selector, now, excluded);
        return all.Count > 0 ? all[0] : null;
    }

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
