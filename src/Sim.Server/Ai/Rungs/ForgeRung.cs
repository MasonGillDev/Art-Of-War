using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// Rung — Forge (2026-09-19, docs/refining-structures.md's deferred
// "SmeltRung"): raise the ARMOURY INDUSTRY. Three structures, in the
// order their payoff arrives:
//
//   1. SMITHY — shields are wood + stone, both already flowing, so the
//      first thing the smithy makes needs no new economy at all. The
//      garrison gets +10 health per shield within days of the roof going
//      on.
//   2. MINE — M44: only on an ore VEIN the brain knows
//      (docs/stone-and-ore-land.md). No known free vein → send a Miner to
//      survey the nearest unsurveyed mountain (mountains are common
//      knowledge, so there is always a slope to try); no Miner → raise
//      OreStarved and TrainRung schools one. Veins are about a third of
//      all mountain tiles, so a sweep or two finds one.
//   3. SMELTER — Ore + Wood -> Iron, the only extractor with inputs. Fed
//      by LogisticsLayer's feed lines, not by this rung: hauls are
//      background (arbitration lesson #1).
//
// Then STAFF what stands (Miner preferred — the 2:1 bonus; the brain
// trains exactly the one Miner it needs to survey, generalists fill the
// rest at the base rate). ArmRung, one slot below, spends the output.
//
// Doctrine, in firing order:
//   * SURPLUS ONLY for new ground — the Fortify gates verbatim (famine,
//     runway, food floor, labor slack) plus the population floor: a camp
//     does not run a smithy. Below the floor the rung is inert — the
//     pre-arming curve. Staffing what stands sits behind the same gates
//     (ungated staffing cost the lab 13-16% of its population).
//   * AN ARMY TO ARM — no Barracks, no soldiers, no forge. Muster owns
//     the decision to raise an army; this rung only equips one.
//   * DON'T BREAK GROUND YOU CAN'T COVER (lesson #10) — every site waits
//     until the castle HOLDS its full build cost, like the Barracks.
//   * ONE SITE AT A TIME, crewed via EnsureBuilders only once it is
//     provisioned (the builder ping-pong the lab caught); rejection
//     feedback is BrainCore's PendingSite observation, like every other
//     placement.
public sealed class ForgeRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        var cfg = ctx.Cfg;
        if (!cfg.Arm) return null;
        if (ctx.View.Population < cfg.ArmPopulationFloor) return null;

        // Bread first: no armoury work at all in famine or on a thin runway.
        if (ctx.View.InFamine) return null;
        if (ctx.View.FoodRunwayTicks >= 0
            && ctx.View.FoodRunwayTicks < 2 * cfg.FoodRunwayFloorTicks) return null;

        // An army to arm.
        if (ctx.OwnStructure(StructureKind.Barracks) is null) return null;
        if (!ctx.OwnUnits.Any(u => (UnitRole)u.Role is UnitRole.Soldier or UnitRole.Archer))
            return null;

        // SURPLUS ONLY — the Fortify gates verbatim. Staffing sits behind
        // them too: the lab tried ungated staffing (idlers only, so it
        // "can't bleed the farms") and lost 13-16% of the population by
        // day 200 — an idler in a labor-tight colony is the next farmhand,
        // not spare change.
        if (ctx.View.CastleFood < cfg.GrowthFoodFloor) return null;
        var (pool, _, handsDemanded) = ctx.LaborLedger();
        if (pool * 100 < handsDemanded * cfg.GrowthLaborSlackPercent) return null;

        // STAFF WHAT STANDS before any new ground: fewest hands first, so
        // one pick and one furnace run before two of either (the lab's
        // faction 0 staffed the mine to two, then the surplus gate shut
        // for ninety days with the smelter empty and ore piling up).
        var posts = new List<(StructDto Post, int Target)>();
        if (ctx.OwnStructure(StructureKind.Mine) is { } mine) posts.Add((mine, cfg.ArmMineWorkers));
        if (ctx.OwnStructure(StructureKind.Smelter) is { } furnace) posts.Add((furnace, cfg.ArmSmelterWorkers));
        foreach (var (post, target) in posts.OrderBy(p => p.Post.Workers).ThenBy(p => p.Post.Kind))
            if (ctx.StaffExtractor(post, UnitRole.Miner, target) is { Count: > 0 } st)
                return new Decision("forge", $"staffing the {Name((StructureKind)post.Kind)}", st);

        // ONE SITE AT A TIME, crewed only once provisioned (the lab's
        // first run: two open Forge sites bounced one builder between
        // them for 36 days — see ThinkContext.EnsureBuilders marchEarly).
        // An open site of any Forge kind owns the rung until it stands.
        foreach (var kind in Chain)
            if (ctx.OwnSite(kind) is { } open)
            {
                if (ctx.EnsureBuilders(open, marchEarly: false) is { Count: > 0 } b)
                    return new Decision("forge", $"building the {Name(kind)}", b);
                return null;   // provisioning, or crewed and hammering — fall through
            }

        // 1. The Smithy.
        if (ctx.OwnStructure(StructureKind.Smithy) is null)
            return Place(ctx, StructureKind.Smithy, requiredBiome: null,
                "no smithy — placing one");

        // 2. The Mine — only on a vein the brain KNOWS (M44). Veins never
        //    run dry, so no DetectExhausted — its buffer cap is the brake.
        if (ctx.OwnStructure(StructureKind.Mine) is null)
            return MineOrSurvey(ctx);
        ctx.Mem.OreStarved = false;

        // 3. The Smelter, once ore is on its way.
        if (ctx.OwnStructure(StructureKind.Smelter) is null)
            return Place(ctx, StructureKind.Smelter, requiredBiome: null,
                "no smelter — placing one");
        return null;
    }

    // M44 — a known free vein takes a Mine; otherwise a Miner goes looking.
    private static Decision? MineOrSurvey(ThinkContext ctx)
    {
        var range = ctx.Cfg.SiteSearchRange;
        if (ctx.NearestFreeVein(range) is { } vein)
        {
            ctx.Mem.OreStarved = false;
            if (!AffordsSite(ctx, StructureCatalog.Spec(StructureKind.Mine))) return null;
            return new Decision("forge", "no mine — placing one on a known vein",
                new List<Intent> { new PlaceSiteIntent(vein, StructureKind.Mine) { PlayerId = ctx.PlayerId } });
        }
        var miners = ctx.OwnUnits.Where(u => (UnitRole)u.Role == UnitRole.Miner).ToList();
        if (miners.Any(u => u.SurveyX >= 0)) return null;   // a sweep is under way — wait for it
        ctx.Mem.OreStarved = miners.Count == 0;
        if (ctx.NearestUnsurveyedMountain(range) is not { } slope) return null;   // every known slope proven barren
        var miner = miners.Where(u => ctx.IsFree(u) && ctx.IsIdleStill(u)).OrderBy(u => u.Id).FirstOrDefault();
        if (miner is null) return null;
        return new Decision("forge", $"no known vein — sending a miner to survey {slope.X},{slope.Y}",
            new List<Intent> { new Sim.Core.Mining.SurveyIntent(ctx.Reserve(miner).Id, slope)
                { PlayerId = ctx.PlayerId } });
    }

    private static readonly StructureKind[] Chain =
        { StructureKind.Smithy, StructureKind.Mine, StructureKind.Smelter };

    private static string Name(StructureKind k) => k.ToString().ToLowerInvariant();

    // Break ground for one link of the chain: only when the castle holds
    // the full cost (lesson #10) and the brain knows a fitting free tile.
    private static Decision? Place(ThinkContext ctx, StructureKind kind, Biome? requiredBiome, string why)
    {
        if (!AffordsSite(ctx, StructureCatalog.Spec(kind))) return null;
        if (ctx.NearestFreeTile(requiredBiome, ctx.Cfg.SiteSearchRange) is not { } t) return null;
        return new Decision("forge", why,
            new List<Intent> { new PlaceSiteIntent(t, kind) { PlayerId = ctx.PlayerId } });
    }

    private static bool AffordsSite(ThinkContext ctx, StructureSpec spec)
    {
        foreach (var (res, amt) in spec.BuildCost)
            if (ThinkContext.AmountOf(ctx.Castle!.Holdings, res) < amt) return false;
        return true;
    }
}
