using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung — Cart (2026-09-19, the food-wall post-mortem in
// docs/ai-players.md): the LOGISTICS MULTIPLIER. Raise a Workshop, forge
// a Cart for every Hauler, walk them in to hitch up. A carted Hauler
// moves 50 a trip against a citizen's 5 — ten far farms served by
// field hands on the road was the wall; this is what a human buys first.
//
// Sits right after Train (organs, then tools) in both ladders, above
// Muster and Grow: the belt is what the colony grows on. Gates are
// deliberately LIGHTER than Forge's — no labor-slack gate, because the
// point is to free labor — but bread still outranks it (no famine, no
// thin runway), a camp doesn't build a workshop (CartPopulationFloor),
// and the site waits for the castle to hold its cost (lesson #10).
// One site, crewed only once provisioned (the Forge ping-pong lesson).
public sealed class CartRung : IRung
{
    private static readonly Resource[] Wanted = { Resource.Cart };

    public Decision? TryClaim(ThinkContext ctx)
    {
        var cfg = ctx.Cfg;
        if (!cfg.Carts) return null;
        if (ctx.View.Population < cfg.CartPopulationFloor) return null;
        if (ctx.View.InFamine) return null;
        if (ctx.View.FoodRunwayTicks >= 0
            && ctx.View.FoodRunwayTicks < cfg.FoodRunwayFloorTicks) return null;

        if (ctx.OwnStructure(StructureKind.Workshop) is { } workshop)
            return Outfitter.Plan(ctx, "cart", workshop, Wanted);

        if (ctx.OwnSite(StructureKind.Workshop) is { } site)
        {
            if (ctx.EnsureBuilders(site, marchEarly: false) is { Count: > 0 } b)
                return new Decision("cart", "building the workshop", b);
            return null;
        }

        // Nobody to hitch a cart to yet: the hauler floor (TrainRung) comes
        // first, the workshop when there is a Hauler to serve.
        if (!ctx.OwnUnits.Any(u => (UnitRole)u.Role == UnitRole.Hauler)) return null;
        foreach (var (res, amt) in StructureCatalog.Spec(StructureKind.Workshop).BuildCost)
            if (ThinkContext.AmountOf(ctx.Castle!.Holdings, res) < amt) return null;
        if (ctx.NearestFreeTile(requiredBiome: null, cfg.SiteSearchRange) is { } t)
            return new Decision("cart", "no workshop — placing one",
                new List<Intent> { new PlaceSiteIntent(t, StructureKind.Workshop) { PlayerId = ctx.PlayerId } });
        return null;
    }
}
