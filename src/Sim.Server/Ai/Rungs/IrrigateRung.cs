using Sim.Core.Canals;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung — Irrigate (M27, docs/canals.md update): dig a canal from known
// water into the farm belt. Irrigated fields (within the world's
// WaterRecoveryRadius of any water) rest at WaterRecoveryAmount — double
// rainfall by default — so the crop-rotation cycle shortens and the same
// land sustains more mouths: canals are how a kingdom outgrows its
// riverbank. Sits below Fortify in both ladders (safety, then soil), and
// is what quiet, prosperous thinks buy.
//
// Doctrine, in firing order:
//
//   1. SURPLUS ONLY — the Fortify lesson verbatim: no canal spend in
//      famine, under a thin runway, below Grow's food floor, or without
//      labor slack. A canal is the longest-horizon investment the brain
//      makes; bread always outranks it.
//   2. ONE CANAL AT A TIME — an in-flight dig gets its builders (three,
//      per the catalog; ThinkContext.BuilderDemand raises the training
//      floor to match) and nothing else is planned until it floods.
//   3. WORTH IT — the rung counts DRY farm claims: tiles of own live
//      farms with no KNOWN water inside the recovery radius. Below
//      IrrigateMinDryClaimTiles the belt is already watered (or too
//      small to matter) and the rung retires.
//   4. PRIMED TREASURY — canal cost is PER TILE (stone-heavy); the dig
//      starts only above IrrigateStoneFloor. That means the quarry
//      income exists — the ResourceStockTarget haul cap makes banking
//      the full price impossible by design; the site drains the
//      warehouse for weeks while logistics keeps refilling it.
//   5. THE PLAN — a deterministic multi-source BFS over KNOWN-diggable
//      tiles, seeded from the water frontier (canonical (y,x) order), to
//      the shortest path whose END tile puts at least the threshold of
//      dry claims inside the recovery radius. Capped at
//      IrrigateMaxCanalTiles; unknown land is undiggable (the plan grows
//      with scouting, like the Fortify ring).
//
// Rejection feedback is observational (BrainCore.PendingCanal): a dig
// the server refused — a fog-hidden claim or reservation mid-path —
// blacklists the anchor tile, and the next plan reroutes from a
// different stretch of the frontier. A canal path cannot bisect (it must
// stay rooted at water), so the anchor is the one retry lever.
public sealed class IrrigateRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        var cfg = ctx.Cfg;
        if (cfg.IrrigateMaxCanalTiles <= 0) return null;
        if (ctx.View.Population < cfg.IrrigatePopulationFloor) return null;

        // 1. Surplus only.
        if (ctx.View.InFamine) return null;
        if (ctx.View.FoodRunwayTicks >= 0
            && ctx.View.FoodRunwayTicks < 2 * cfg.FoodRunwayFloorTicks) return null;
        if (ctx.View.CastleFood < cfg.GrowthFoodFloor) return null;
        var (pool, _, handsDemanded) = ctx.LaborLedger();
        if (pool * 100 < handsDemanded * cfg.GrowthLaborSlackPercent) return null;

        // 2. One canal at a time: crew the dig, then wait for the flood.
        if (ctx.OwnSite(StructureKind.Canal) is { } dig)
        {
            if (ctx.EnsureBuilders(dig) is { Count: > 0 } b)
                return new Decision("irrigate", "digging the canal", b);
            return null;
        }

        // 3. Worth it? Count the dry farm claims.
        var dry = DryFarmClaims(ctx);
        if (dry.Count < cfg.IrrigateMinDryClaimTiles) return null;

        // 4. Primed treasury.
        if (ThinkContext.AmountOf(ctx.Castle!.Holdings, Resource.Stone)
                < cfg.IrrigateStoneFloor) return null;

        // 5. The plan.
        if (PlanCanal(ctx, dry) is { Count: > 0 } path)
            return new Decision("irrigate",
                $"digging a {path.Count}-tile canal into the farm belt ({dry.Count} dry tiles)",
                new List<Intent> { new PlaceCanalIntent(path) { PlayerId = ctx.PlayerId } });
        return null;
    }

    // Claim tiles of own LIVE farms with no known water inside the
    // recovery radius — the tiles a canal would move onto the fast rest
    // cycle. Dead farms are skipped: their husks lock the claims anyway.
    private static List<(int X, int Y)> DryFarmClaims(ThinkContext ctx)
    {
        var dry = new List<(int X, int Y)>();
        foreach (var farm in ctx.Own)
        {
            if ((StructureKind)farm.Kind != StructureKind.Farm) continue;
            if (ctx.Mem.DeadExtractors.Contains((farm.X, farm.Y))) continue;
            for (var i = 0; i < farm.ClaimX.Length; i++)
            {
                var t = (farm.ClaimX[i], farm.ClaimY[i]);
                if (!ctx.KnownWaterNear(t.Item1, t.Item2, ctx.Cfg.IrrigateWaterRadius))
                    dry.Add(t);
            }
        }
        return dry;
    }

    // Deterministic multi-source BFS from the known water frontier to the
    // nearest tile whose radius covers enough dry claims. Returns the
    // ordered dig path (water-rooted, 4-connected — PlaceCanalIntent's
    // contract by construction) or null.
    private static List<TileCoord>? PlanCanal(ThinkContext ctx, List<(int X, int Y)> dry)
    {
        var cfg = ctx.Cfg;
        var drySet = new HashSet<(int, int)>(dry);
        int Covered((int X, int Y) t)
        {
            var n = 0;
            for (var dy = -cfg.IrrigateWaterRadius; dy <= cfg.IrrigateWaterRadius; dy++)
            for (var dx = -cfg.IrrigateWaterRadius; dx <= cfg.IrrigateWaterRadius; dx++)
                if (drySet.Contains((t.X + dx, t.Y + dy))) n++;
            return n;
        }
        bool InRange((int X, int Y) t) =>
            Math.Max(Math.Abs(t.X - ctx.CastleTile.X), Math.Abs(t.Y - ctx.CastleTile.Y))
                <= cfg.SiteSearchRange;

        var visited = new HashSet<(int, int)>();
        var parent = new Dictionary<(int, int), (int, int)>();
        var queue = new Queue<((int X, int Y) T, int Depth)>();
        foreach (var w in ctx.KnownWaterTiles())
            foreach (var n in Neighbors4(w))
            {
                if (visited.Contains(n) || !InRange(n)) continue;
                if (!ctx.KnownDiggable(n.X, n.Y)) continue;
                visited.Add(n);
                queue.Enqueue((n, 1));
            }
        while (queue.Count > 0)
        {
            var (t, depth) = queue.Dequeue();
            if (Covered(t) >= cfg.IrrigateMinDryClaimTiles)
            {
                // Reconstruct water → goal (parent chain runs goal → seed).
                var path = new List<TileCoord>();
                var cur = t;
                while (true)
                {
                    path.Add(new TileCoord(cur.X, cur.Y));
                    if (!parent.TryGetValue(cur, out cur)) break;
                }
                path.Reverse();
                return path;
            }
            if (depth >= cfg.IrrigateMaxCanalTiles) continue;
            foreach (var n in Neighbors4(t))
            {
                if (visited.Contains(n) || !InRange(n)) continue;
                if (!ctx.KnownDiggable(n.X, n.Y)) continue;
                visited.Add(n);
                parent[n] = t;
                queue.Enqueue((n, depth + 1));
            }
        }
        return null;
    }

    private static IEnumerable<(int X, int Y)> Neighbors4((int X, int Y) t)
    {
        yield return (t.X, t.Y - 1);
        yield return (t.X + 1, t.Y);
        yield return (t.X, t.Y + 1);
        yield return (t.X - 1, t.Y);
    }
}
