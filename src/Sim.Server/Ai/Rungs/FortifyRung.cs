using Sim.Core.Fortifications;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// Rung — Fortify (M26, docs/walls-and-gates.md): ring the castle in walls
// once the colony is a town with stone to spare. Sits BELOW the whole
// economy/army ladder in both brains — fortification is what quiet thinks
// buy, never what food or defense pays for.
//
// Doctrine, in firing order:
//
//   1. FINISH WHAT'S STARTED — builders for wall/gate/quarry sites
//      (materials are logistics' job, as everywhere).
//   2. STONE ECONOMY — walls are stone-heavy and NO other rung mines:
//      below FortifyStoneFloor the rung bootstraps a Quarry on the best
//      known HILL pocket (M44 — stone is quarried from hills, and a quarry
//      claims hill land like a farm claims grassland) and staffs it, then
//      waits for the pile.
//   3. GATE FIRST — a gate goes up before any wall segment, and no wall
//      is ever placed while zero gates stand or build: own walls block
//      OWN units too (spec has no owner exemption), so a gateless ring
//      would wall the colony's haulers in. One gate per ring side,
//      midpoint first, shifting along the side when the midpoint is
//      claimed or flooded; a side with no placeable tile has no door
//      (one gate anywhere keeps the colony open).
//   4. ONE WALL SEGMENT PER THINK, bounded two ways: FortifySegmentMax
//      caps the line length (short lines localize server-side
//      rejections — the whole intent fails-clean on one bad tile), and
//      FortifyMaxOpenSites caps outstanding fort sites (each open site
//      claims a background delivery carrier per think — an unbounded
//      ring would starve the food hauls that outrank nothing).
//
// BREACH MENDING (M26 reclaim loop): rubble sitting on the ring — a razed
// segment, or fallen wreckage in the wall's path — gets a materials-free
// ClearRubbleIntent before any new placement, and deliberately AHEAD of
// the stone floor (clearing costs labor only; a stone-poor colony still
// mends its breach). The cleared tile rejoins the missing runs and gets
// re-walled by the ordinary segment flow: breach → clear → rebuild.
//
// THREAT-FACING BUILD ORDER: with a live threat picture — fresh hostile
// sightings, or a war partner's known castle once war is declared or
// telegraphed — the ring builds its threat-nearest arc first, growing
// from the hot end. Without one it walks clockwise, so a peaceful Rival
// walls exactly like a Homesteader (the Sparta discipline).
//
// REJECTION FEEDBACK is observational, like PendingSite: BrainCore
// watches the ordered segment; if no tile grew a site the server
// rejected the line, and the next attempt halves (bisect toward the bad
// tile — a length-1 rejection blacklists that exact tile). The ring
// completes incrementally as knowledge grows: unknown or blocked tiles
// are holes today, and the rung re-plans from the view every think.
public sealed class FortifyRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        var cfg = ctx.Cfg;
        if (cfg.FortifyRadius <= 0) return null;
        if (ctx.View.Population < cfg.FortifyPopulationFloor) return null;

        // SURPLUS-ONLY ACTIVITY (the 160-day lab regression: wall spend +
        // carrier drain tipped mature colonies into famine). Masonry yields
        // to bread at least as readily as breeding does: pause the whole
        // rung — placements, quarry, even builder assignment (assigned
        // builders are locked and invisible to every other selector) — in
        // famine, under a thin food runway, below Grow's food floor, or
        // without labor slack. Open sites finish on their own; no NEW
        // spend starts until the granary is fat again.
        if (ctx.View.InFamine) return null;
        if (ctx.View.FoodRunwayTicks >= 0
            && ctx.View.FoodRunwayTicks < 2 * cfg.FoodRunwayFloorTicks) return null;
        if (ctx.View.CastleFood < cfg.GrowthFoodFloor) return null;
        var (pool, _, handsDemanded) = ctx.LaborLedger();
        if (pool * 100 < handsDemanded * cfg.GrowthLaborSlackPercent) return null;

        // 1. Finish what's started: builders for fort sites — wall and gate
        // segments AND ring-rubble clearing jobs — then the quarry.
        var fortSites = ctx.OwnSites().Where(s =>
            (StructureKind)s.TargetKind is StructureKind.Wall or StructureKind.Gate
                or StructureKind.Rubble).ToList();
        foreach (var site in fortSites)
            if (ctx.EnsureBuilders(site) is { Count: > 0 } b)
                return new Decision("fortify", "raising the wall", b);
        if (ctx.OwnSite(StructureKind.Quarry) is { } quarrySite
            && ctx.EnsureBuilders(quarrySite) is { Count: > 0 } qb)
            return new Decision("fortify", "building the quarry", qb);

        // 2. The plan — what does the ring still need? Nothing knowable and
        // nothing in flight → the rung retires until the world changes.
        var plan = PlanRing(ctx);
        if (plan.NextGate is null && plan.NextRun.Count == 0 && plan.NextRubble is null
            && fortSites.Count == 0)
            return null;

        // 3. Reclaim the ring: rubble on a ring tile — a breached segment,
        // or wreckage standing in the wall's path — is CLEARED before
        // anything new is placed. The job is materials-free, so it
        // deliberately does NOT wait behind the stone floor: a stone-poor
        // colony still mends its breach. Bounded by the same open-sites
        // budget (a clearing site holds a builder). ClearRubbleIntent is
        // self-confirming: success grows a site at the tile (no longer
        // rubble → no longer a candidate), so no pending-feedback machinery
        // is needed.
        var budget = cfg.FortifyMaxOpenSites - fortSites.Count;
        if (plan.NextRubble is { } pile && budget > 0)
            return new Decision("fortify", "clearing the breach",
                new List<Intent> { new Sim.Core.Sieges.ClearRubbleIntent(pile)
                    { PlayerId = ctx.PlayerId } });

        // 4. Stone economy. Placement waits behind the stone floor; the
        // floor waits behind a quarry.
        var stone = ThinkContext.AmountOf(ctx.Castle!.Holdings, Resource.Stone);
        if (stone < cfg.FortifyStoneFloor)
        {
            var quarry = ctx.Own.FirstOrDefault(s => (StructureKind)s.Kind == StructureKind.Quarry);
            if (quarry is null)
            {
                if (ctx.OwnSite(StructureKind.Quarry) is not null) return null;   // building — wait
                // M44 — quarries claim hill land like farms claim grassland:
                // search for a pocket that can host the full claim.
                var quarrySpec = StructureCatalog.Spec(StructureKind.Quarry);
                if (ctx.NearestPocketTile(Biome.Hills, cfg.SiteSearchRange,
                        quarrySpec.ClaimCount, quarrySpec.ClaimRange) is { } ht)
                    return new Decision("fortify", "no stone income — placing a quarry",
                        new List<Intent> { new PlaceSiteIntent(ht, StructureKind.Quarry)
                            { PlayerId = ctx.PlayerId } });
                return null;   // no known hill pocket in range — scouting's problem
            }
            if (ctx.StaffExtractor(quarry, UnitRole.Quarryman, cfg.FortifyQuarryWorkers)
                    is { Count: > 0 } st)
                return new Decision("fortify", "staffing the quarry for wall stone", st);
            return null;   // income exists — wait for the pile
        }

        // 5. Pace the build: bounded open sites bound the carrier drain.
        if (budget <= 0) return null;

        // 6. Gate first — never wall the colony in.
        if (plan.NextGate is { } gate)
            return new Decision("fortify", "gate first — never wall the colony in",
                new List<Intent> { new PlaceSiteIntent(gate, StructureKind.Gate)
                    { PlayerId = ctx.PlayerId } });
        if (!plan.AnyGate) return null;   // no gate stands or fits → no walls at all

        // 7. One wall segment per think, clipped by every cap that applies
        // (open-site budget, segment max, the bisect retry cap).
        if (plan.NextRun.Count > 0)
        {
            var cap = Math.Max(1, Math.Min(budget,
                Math.Min(cfg.FortifySegmentMax, ctx.Mem.WallSegmentCap ?? int.MaxValue)));
            var seg = plan.NextRun.Take(cap).ToList();
            return new Decision("fortify", $"walling the ring ({seg.Count} tile(s))",
                new List<Intent> { new PlaceWallIntent(seg) { PlayerId = ctx.PlayerId } });
        }
        return null;
    }

    // ---- the ring planner ------------------------------------------------

    // Reads the ring's state from the view digest: the next gate slot to
    // fill (side midpoints, N/E/S/W order), whether ANY own gate stands or
    // builds on the ring, the threat-preferred run of missing,
    // known-buildable wall tiles, and the next RUBBLE pile sitting on the
    // ring (a breached segment or fallen wreckage) awaiting a clearing job.
    private static (TileCoord? NextGate, bool AnyGate, List<TileCoord> NextRun,
        TileCoord? NextRubble) PlanRing(ThinkContext ctx)
    {
        var c = ctx.CastleTile;
        var r = ctx.Cfg.FortifyRadius;

        var structAt = new Dictionary<(int X, int Y), StructDto>();
        foreach (var s in ctx.View.Structures) structAt[(s.X, s.Y)] = s;

        bool OwnGate((int X, int Y) t) =>
            structAt.TryGetValue(t, out var s) && s.OwnerId == ctx.PlayerId
            && ((StructureKind)s.Kind == StructureKind.Gate
                || ((StructureKind)s.Kind == StructureKind.ConstructionSite
                    && (StructureKind)s.TargetKind == StructureKind.Gate));

        bool Placeable((int X, int Y) t) =>
            t.X >= 0 && t.Y >= 0 && t.X < ctx.MapWidth && t.Y < ctx.MapHeight
            && !structAt.ContainsKey(t)
            && ctx.KnownBuildableLand(t.X, t.Y);

        // Gate slots: one per ring side, canonical N/E/S/W order. Try the
        // side midpoint first, then walk outward along the side — a claimed
        // or flooded midpoint SHIFTS the door, it doesn't cancel it (the
        // coastal lab colony had every midpoint under water or farm claims
        // and would have deadlocked gateless forever). A side with no
        // placeable tile at all has no door — its obstacle, usually the
        // sea, is its own wall. Corners stay wall ground.
        var sides = new[]
        {
            (Mid: (c.X, c.Y - r), Along: (1, 0)),   // north — walk ±X
            (Mid: (c.X + r, c.Y), Along: (0, 1)),   // east  — walk ±Y
            (Mid: (c.X, c.Y + r), Along: (1, 0)),   // south
            (Mid: (c.X - r, c.Y), Along: (0, 1)),   // west
        };
        var anyGate = RingTiles(c, r).Any(OwnGate);
        TileCoord? nextGate = null;
        var reserved = new HashSet<(int X, int Y)>();
        foreach (var side in sides)
        {
            (int X, int Y)? slot = null;
            var sideHasGate = false;
            for (var o = 0; o < r && slot is null && !sideHasGate; o++)
                foreach (var sign in o == 0 ? new[] { 0 } : new[] { -o, o })
                {
                    var t = (side.Mid.Item1 + side.Along.Item1 * sign,
                             side.Mid.Item2 + side.Along.Item2 * sign);
                    if (OwnGate(t)) { sideHasGate = true; reserved.Add(t); break; }
                    if (Placeable(t)) { slot = t; break; }
                }
            if (sideHasGate || slot is not { } g) continue;
            reserved.Add(g);
            nextGate ??= new TileCoord(g.X, g.Y);
        }

        // Every contiguous missing run, walking the ring clockwise (runs
        // split at gate slots, holes, and the NW seam) — and every rubble
        // pile ON the ring (a razed segment, or wreckage standing in the
        // wall's path) that a clearing job would turn back into wall ground.
        var runs = new List<List<TileCoord>>();
        var run = new List<TileCoord>();
        var rubble = new List<TileCoord>();
        foreach (var t in RingTiles(c, r))
        {
            if (structAt.TryGetValue(t, out var occupant)
                && (StructureKind)occupant.Kind == StructureKind.Rubble)
                rubble.Add(new TileCoord(t.X, t.Y));
            if (!reserved.Contains(t) && Placeable(t))
            {
                run.Add(new TileCoord(t.X, t.Y));
                continue;
            }
            if (run.Count > 0) { runs.Add(run); run = new List<TileCoord>(); }
        }
        if (run.Count > 0) runs.Add(run);

        var threatSet = ThreatPoints(ctx);
        TileCoord? nextRubble = rubble.Count > 0 ? rubble[0] : null;
        if (rubble.Count > 1 && threatSet.Count > 0)
        {
            // The breach facing the enemy is the urgent one.
            var best = int.MaxValue;
            foreach (var pile in rubble)
            {
                var d = threatSet.Min(p =>
                    Math.Max(Math.Abs(pile.X - p.X), Math.Abs(pile.Y - p.Y)));
                if (d < best) { best = d; nextRubble = pile; }
            }
        }
        if (runs.Count == 0) return (nextGate, anyGate, new List<TileCoord>(), nextRubble);

        // THREAT-FACING: build the arc nearest a live threat first, and
        // within it start from the threat-nearest end. No threat (or one
        // run) → plain walk order, the deterministic default. Ties keep
        // walk order (strict '<'), so identical threat pictures build
        // identical walls.
        var next = runs[0];
        if (threatSet.Count > 0)
        {
            int Dist(TileCoord t) => threatSet.Min(p =>
                Math.Max(Math.Abs(t.X - p.X), Math.Abs(t.Y - p.Y)));
            if (runs.Count > 1)
            {
                var best = int.MaxValue;
                foreach (var candidate in runs)
                {
                    var d = candidate.Min(Dist);
                    if (d < best) { best = d; next = candidate; }
                }
            }
            if (next.Count > 1 && Dist(next[^1]) < Dist(next[0]))
                next.Reverse();   // segments grow OUT from the hot end
        }
        return (nextGate, anyGate, next, nextRubble);
    }

    // Threat points that steer the build order: FRESH hostile sightings
    // (raids teach both brains where danger comes from — the Defend rung's
    // own memory) and, ONLY at war or inside the telegraph window, the
    // enemy's known castle. Peacetime intel deliberately does not count: a
    // peaceful Rival walls exactly like a Homesteader (the Sparta
    // discipline), and the wall effort pivots toward the front the moment
    // a war is declared. Order-independent consumer (min over the set), so
    // dictionary iteration order can't leak into the choice.
    private static List<(int X, int Y)> ThreatPoints(ThinkContext ctx)
    {
        var threats = new List<(int X, int Y)>();
        foreach (var (tile, s) in ctx.Mem.SightedHostiles)
            if (ctx.Now - s.Tick <= ctx.Cfg.ThreatMemoryTicks) threats.Add(tile);
        foreach (var (owner, intel) in ctx.Mem.KnownEnemyCastles)
            if ((ctx.AtWarWith(owner) || ctx.PendingWarWith(owner)) && !ctx.IsDefeated(owner))
                threats.Add(intel.Tile);
        return threats;
    }

    // Perimeter of the Chebyshev-radius-r square, clockwise from the NW
    // corner. Consecutive tiles are 4-adjacent (PlaceWallIntent's chain
    // rule), corners included exactly once.
    private static IEnumerable<(int X, int Y)> RingTiles(TileCoord c, int r)
    {
        for (var x = c.X - r; x <= c.X + r; x++) yield return (x, c.Y - r);          // top L→R
        for (var y = c.Y - r + 1; y <= c.Y + r; y++) yield return (c.X + r, y);      // right T→B
        for (var x = c.X + r - 1; x >= c.X - r; x--) yield return (x, c.Y + r);      // bottom R→L
        for (var y = c.Y + r - 1; y >= c.Y - r + 1; y--) yield return (c.X - r, y);  // left B→T
    }
}
