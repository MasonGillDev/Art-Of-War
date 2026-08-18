using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Battlefield salvage (2026-07-13) — the dead drop what they carried:
// cargo and equipment become a ground pile under a grave marker
// (GraveTracker), and this rung sends civilians to bring it home. The
// loop is RaidRung's loot leg pointed at graves instead of enemy
// extractors, with the whole dry-strike apparatus DELETED: the host
// retires a grave the moment its pile empties, so the view IS the
// dry-target list — a grave someone else looted first simply vanishes
// and the crew retargets on its own.
//
// WHO GOES: idle-still civilian adults under the StaffExtractor
// exclusions — never Builders (they belong on sites), never Haulers
// (the logistics backbone), never the garrison. The crew is a
// cross-think designation (ledger #6) bounded by SalvageCrewSize
// (ledger #9), capped at one hand per known grave so a lone grave
// doesn't draw a column.
//
// WHERE: only graves inside SalvageLeashTiles of the castle (a
// civilian's walk, not an expedition — distant fields belong to whoever
// died there) and outside every live sighting's danger radius (Defend's
// threat memory): nobody loots MID-battle; the field is read after it
// cools.
//
// NAMING THE LOOT: LoadCargoIntent needs a resource, and a grave's
// contents depend on what the victim carried — no catalog rule names
// them (RaidRung's extractor trick doesn't transfer). So the pile's
// contents ride the wire for tiles in CURRENT sight (ViewDto.Piles, the
// M23 cache stance), and the crew member names the richest row while
// standing on the grave — its own sight is what makes the row visible.
//
// LOOT GOES HOME: the castle is the only store the economy spends from —
// a forward stockpile by the battlefield would park the loot
// un-spendably and cost 20 wood to do it. Deferred until storage
// structures participate in the economy's ledger.
public sealed class SalvageRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        if (ctx.Cfg.SalvageCrewSize <= 0) return null;

        // Roster hygiene (the raid party's discipline): the dead and the
        // retrained leave the crew.
        ctx.Mem.SalvageCrew.RemoveWhere(id =>
            ctx.OwnUnits.FirstOrDefault(u => u.Id == id) is not { } u
            || (UnitRole)u.Role is UnitRole.Builder or UnitRole.Hauler
                or UnitRole.Soldier or UnitRole.Archer);

        // Lootable graves: witnessed (view.Graves carries every marker
        // this faction has SEEN that still holds loot), inside the leash,
        // not inside a live sighting's danger radius.
        var lootable = ctx.View.Graves
            .Where(g => Cheb(g.X, g.Y, ctx.CastleTile.X, ctx.CastleTile.Y)
                    <= ctx.Cfg.SalvageLeashTiles
                && !ctx.UnderThreat(new TileCoord(g.X, g.Y)))
            .OrderBy(g => Cheb(g.X, g.Y, ctx.CastleTile.X, ctx.CastleTile.Y))
            .ThenBy(g => g.Y).ThenBy(g => g.X)
            .ToList();
        if (lootable.Count == 0) return StandDown(ctx);

        // Top up the crew: one hand per grave, capped by the budget.
        var crewTarget = Math.Min(ctx.Cfg.SalvageCrewSize, lootable.Count);
        foreach (var u in ctx.OwnUnits
                     .Where(u => ctx.IsIdleStill(u) && ctx.IsFree(u) && u.CargoAmount == 0
                         && u.Age >= ctx.Cfg.MinAdultAgeYears
                         && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                             or UnitRole.Soldier or UnitRole.Archer))
                     .OrderBy(u => u.Id))
        {
            if (ctx.Mem.SalvageCrew.Count >= crewTarget) break;
            ctx.Mem.SalvageCrew.Add(u.Id);
        }
        if (ctx.Mem.SalvageCrew.Count == 0) return null;

        var intents = new List<Intent>();
        foreach (var u in ctx.OwnUnits.Where(u => ctx.Mem.SalvageCrew.Contains(u.Id))
                     .OrderBy(u => u.Id))
        {
            if (!ctx.IsIdleStill(u)) continue;   // mid-march — orders on arrival

            if (u.CargoAmount > 0)
            {
                // Loot home, then hand it over (RaidRung's leg, verbatim).
                if (u.X == ctx.CastleTile.X && u.Y == ctx.CastleTile.Y)
                    intents.Add(new UnloadCargoIntent(ctx.Reserve(u).Id) { PlayerId = ctx.PlayerId });
                else
                    intents.Add(new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile) { PlayerId = ctx.PlayerId });
                continue;
            }

            if (lootable.Any(g => g.X == u.X && g.Y == u.Y))
            {
                // Standing on a grave: the unit's own sight puts the pile
                // on the wire — name its richest row and load. No row
                // means the pile drained this very hour; the marker
                // retires from the view by the next think and the crew
                // retargets — no dry-strike counters needed.
                var pick = ctx.PileAt(u.X, u.Y)?.Holdings
                    .Where(h => h.Amount > 0)
                    .OrderByDescending(h => h.Amount).ThenBy(h => h.Resource)
                    .FirstOrDefault();
                if (pick is not null)
                    intents.Add(new LoadCargoIntent(ctx.Reserve(u).Id, (Resource)pick.Resource)
                        { PlayerId = ctx.PlayerId });
                continue;
            }

            // Walk to the nearest lootable grave FROM THE UNIT, not the
            // castle — crew members spread across the field instead of
            // stacking on the closest marker.
            var target = lootable.OrderBy(g => Cheb(g.X, g.Y, u.X, u.Y))
                .ThenBy(g => g.Y).ThenBy(g => g.X).First();
            intents.Add(new MoveIntent(ctx.Reserve(u).Id, new TileCoord(target.X, target.Y))
                { PlayerId = ctx.PlayerId });
        }

        return intents.Count == 0 ? null
            : new Decision("salvage",
                $"{ctx.Mem.SalvageCrew.Count} salvaging {lootable.Count} grave(s)", intents);
    }

    // No lootable graves: carriers still walk their loot home and unload;
    // the empty-handed are released where they stand (staffing re-absorbs
    // them next think).
    private static Decision? StandDown(ThinkContext ctx)
    {
        if (ctx.Mem.SalvageCrew.Count == 0) return null;
        var intents = new List<Intent>();
        foreach (var u in ctx.OwnUnits.Where(u => ctx.Mem.SalvageCrew.Contains(u.Id))
                     .OrderBy(u => u.Id).ToList())
        {
            if (u.CargoAmount == 0) { ctx.Mem.SalvageCrew.Remove(u.Id); continue; }
            if (!ctx.IsIdleStill(u)) continue;
            if (u.X == ctx.CastleTile.X && u.Y == ctx.CastleTile.Y)
                intents.Add(new UnloadCargoIntent(ctx.Reserve(u).Id) { PlayerId = ctx.PlayerId });
            else
                intents.Add(new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile) { PlayerId = ctx.PlayerId });
        }
        return intents.Count == 0 ? null
            : new Decision("salvage", "field clear — crew hauls home", intents);
    }

    private static int Cheb(int ax, int ay, int bx, int by) =>
        Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));
}
