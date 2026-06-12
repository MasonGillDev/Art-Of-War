using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// Rung 4: Grow — breed when the larder allows.
public sealed class GrowRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        if (ctx.View.CastleFood <= ctx.Cfg.GrowthFoodFloor) return null;

        bool Fertile(UnitDto u) =>
            u.Age >= ctx.Cfg.MinFertileAgeYears && u.Age <= ctx.Cfg.MaxFertileAgeYears;

        // HOUSES SCALE WITH THE FERTILE POPULATION — a house is a
        // pregnancy SLOT, and one slot caps the colony at one birth per
        // gestation cycle no matter how the demographic knobs are tuned
        // (the lab's TicksPerYear runs were measuring this ceiling, not
        // the clock). Pregnancies overlap across houses; pair setup
        // stays serial through the single designation.
        var houses = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.House)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var houseSites = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
            && (StructureKind)s.TargetKind == StructureKind.House).ToList();
        var fertileAdults = ctx.OwnUnits.Count(u => Fertile(u)
            && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                or UnitRole.Soldier or UnitRole.Archer));
        var housesNeeded = Math.Max(1, fertileAdults / Math.Max(1, ctx.Cfg.FertileAdultsPerHouse));

        if (houses.Count + houseSites.Count < housesNeeded
            && ctx.NearestFreeTile(requiredBiome: null, ctx.Cfg.SiteSearchRange) is { } t)
            return new Decision("grow",
                $"houses {houses.Count}+{houseSites.Count} of {housesNeeded} — placing one",
                new List<Intent> { new PlaceSiteIntent(t, StructureKind.House) { PlayerId = ctx.PlayerId } });
        foreach (var hs in houseSites)
            if (ctx.EnsureBuilders(hs) is { Count: > 0 } b)
                return new Decision("grow", "building a house", b);
        if (houses.Count == 0) return null;

        // Target: the first house that is VACANT (no pregnancy — the
        // occupation signature is two own units Working on the tile) and
        // STOCKED for a birth. None ready → logistics is stocking, or
        // every slot is mid-pregnancy.
        bool Vacant(StructDto h) => ctx.OwnUnits.Count(u =>
            u.X == h.X && u.Y == h.Y && u.Activity == (int)Activity.Working) < 2;
        var target = houses.FirstOrDefault(h => Vacant(h)
            && ThinkContext.AmountOf(h.Holdings, Resource.Food) >= ctx.Cfg.BirthFoodCost);
        if (target is null) return null;
        var houseTile = ThinkContext.TileOf(target);

        // CARRYING CAPACITY — don't breed mouths the workforce can't
        // feed. A child eats for 13 years before it can work; the lab
        // watched unthrottled breeding outrun the adult labor pool into
        // extinction twice. Growth resumes when natives come of age and
        // the pool widens — population rises in generational waves.
        var (pool, _, handsDemanded) = ctx.LaborLedger();
        if ((long)pool * 100 < (long)handsDemanded * ctx.Cfg.GrowthLaborSlackPercent) return null;

        // DESIGNATED PARENTS — ownership across thinks, not just within
        // one. A per-think reservation can't stop Eat from re-staffing a
        // recruit the think AFTER Grow freed them (the thrash loop the lab
        // caught: recruit → re-assign → recruit, one birth in 130 days).
        // Designation persists in memory until breeding starts; every
        // other selector skips designated units.
        ctx.Mem.DesignatedParents.RemoveWhere(id =>
        {
            var u = ctx.OwnUnits.FirstOrDefault(x => x.Id == id);
            return u is null || !Fertile(u)
                || (u.Activity == (int)Activity.Working
                    && houses.Any(h => h.X == u.X && h.Y == u.Y));   // breeding started — job done
        });

        // The garrison doesn't breed (M17 Phase 2): a soldier pulled
        // into a house is a soldier off the wall, and the quota would
        // just retrain a replacement — a silent two-mouth tax per birth.
        bool Eligible(UnitDto u) => Fertile(u) && ctx.IsFree(u)
            && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                or UnitRole.Soldier or UnitRole.Archer)
            && !ctx.Mem.DesignatedParents.Contains(u.Id);
        if (ctx.Mem.DesignatedParents.Count < 2)
        {
            // Free idlers first, then conscript off the fields.
            foreach (var u in ctx.OwnUnits.Where(u => Eligible(u) && ctx.IsIdleStill(u)).OrderBy(u => u.Id))
            {
                if (ctx.Mem.DesignatedParents.Count >= 2) break;
                ctx.Mem.DesignatedParents.Add(u.Id);
            }
            foreach (var u in ctx.OwnUnits.Where(u => Eligible(u)
                         && u.Activity == (int)Activity.Working).OrderBy(u => u.Id))
            {
                if (ctx.Mem.DesignatedParents.Count >= 2) break;
                ctx.Mem.DesignatedParents.Add(u.Id);
            }
        }
        if (ctx.Mem.DesignatedParents.Count < 2) return null;   // nobody fertile left — the cliff

        // March the designated pair through: unassign if working, walk if
        // away, breed when both stand idle on the house tile.
        var parents = ctx.OwnUnits.Where(u => ctx.Mem.DesignatedParents.Contains(u.Id))
            .OrderBy(u => u.Id).ToList();
        var intents = new List<Intent>();
        foreach (var p in parents)
        {
            if (p.Activity == (int)Activity.Working)
            {
                var post = ctx.Own.FirstOrDefault(s => s.X == p.X && s.Y == p.Y);
                if (post is not null)
                    intents.Add(new UnassignWorkersIntent(new TileCoord(post.X, post.Y),
                        new[] { ctx.Reserve(p).Id }) { PlayerId = ctx.PlayerId });
            }
            else if (ctx.IsIdleStill(p) && (p.X != houseTile.X || p.Y != houseTile.Y))
                intents.Add(new MoveIntent(ctx.Reserve(p).Id, houseTile) { PlayerId = ctx.PlayerId });
        }
        if (intents.Count > 0)
            return new Decision("grow", "escorting designated parents", intents);

        if (parents.Count >= 2 && parents.All(p =>
                ctx.IsIdleStill(p) && p.X == houseTile.X && p.Y == houseTile.Y))
        {
            ctx.Mem.DesignatedParents.Clear();   // re-derive after the birth
            return new Decision("grow", "parents in place — breeding",
                new List<Intent> { new BeginBreedingIntent(houseTile,
                    ctx.Reserve(parents[0]).Id, ctx.Reserve(parents[1]).Id) { PlayerId = ctx.PlayerId } });
        }
        return null;   // pair mid-march
    }
}
