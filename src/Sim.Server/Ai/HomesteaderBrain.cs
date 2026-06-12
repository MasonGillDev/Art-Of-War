using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M17 — the Homesteader: a peaceful economy brain (docs/m17-ai-players-spec.md).
//
// FAIRNESS IS THE SIGNATURE: Think takes the projected ViewDto — the same
// fog-filtered payload a human client renders — plus the clock and its
// own memory. It can never reference GameWorld or Simulation (pinned by
// AiPlayerTests.Brain_TouchesOnlyTheView). It MAY read Sim.Core catalogs
// and enums: those are game RULES (a human knows build costs from the
// UI), not world state.
//
// TWO LAYERS PER THINK (the first arbitration lesson, learned the
// designed way — the decision trace showed the AI hauling food while its
// camp site sat at zero builders):
//
//   * LOGISTICS — hauls are BACKGROUND, not decisions. Every think, every
//     needed haul (full buffers home, site deliveries, house stocking)
//     gets its own idle carrier. Hauling competes for carriers, never for
//     the think. Builders are NOT carriers — conscripting them as food
//     mules was exactly how the camp site starved.
//   * THE STRATEGIC LADDER — strict priority, first rung that emits
//     claims the think: Eat (farm placed/built/staffed) → Build (camp) →
//     Grow (house, breeding) → Scout. Rungs with nothing to DO fall
//     through, so an in-progress goal never starves the rungs below.
//     The thresholds that decide when a rung fires live in AiConfig —
//     they ARE the arbitration.
//
// OBSERVATION-DRIVEN: progress is read from the next view (the site
// exists, the buffer fell, the unit arrived), never from remembered
// promises — a restarted server re-derives every goal. AiMemory holds
// droppable hints only (scout rotation, rejected-site blacklist).
public sealed class HomesteaderBrain
{
    public sealed record Decision(string Rung, string Why, List<Intent> Intents);

    private readonly AiConfig _cfg;
    public HomesteaderBrain(AiConfig cfg) { _cfg = cfg; }

    public Decision Think(ViewDto view, long now, AiMemory mem)
    {
        // Site-placement feedback by OBSERVATION (the brain can't see
        // rejection notices): we ordered a site last think and the view
        // shows nothing at that tile → the placement was rejected
        // (insufficient claimable land, contested tile, …). Blacklist the
        // tile so NearestFreeTile offers the next candidate. MUST run
        // BEFORE the digest is built — the digest snapshots the blacklist,
        // and updating it afterwards made every rejected tile get retried
        // exactly once (off-by-one-think, seen live as doubled PlaceSite
        // attempts).
        if (mem.PendingSite is { } pending && now > pending.OrderedAt)
        {
            var occupied = view.Structures.Any(s => s.X == pending.Tile.X && s.Y == pending.Tile.Y);
            if (!occupied) mem.BlacklistedTiles.Add((pending.Tile.X, pending.Tile.Y));
            mem.PendingSite = null;
        }

        var d = Digest.Build(view, _cfg, mem);
        if (d.Castle is null) return new Decision("dead", "no castle", new List<Intent>());

        // STRATEGIC FIRST: decisions (place/staff/breed/scout) reserve
        // their units before logistics swarms the rest. The other order
        // let the haul swarm take every idle unit every think — the camp
        // sat unstaffed for 29 days while food piled up. Arbitration
        // lesson #5: priority isn't just rung order, it's who gets to
        // reserve people first.
        var strategic = Eat(d, view)
            ?? Build(d)
            ?? Grow(d, view)
            ?? Scout(d, mem);

        var intents = new List<Intent>();
        if (strategic is not null) intents.AddRange(strategic.Intents);
        var hauls = Logistics(d, view);
        intents.AddRange(hauls);

        var rung = strategic?.Rung ?? (hauls.Count > 0 ? "logistics" : "idle");
        var why = strategic?.Why ?? (hauls.Count > 0 ? $"{hauls.Count} haul(s)" : "all needs met");
        if (strategic is not null && hauls.Count > 0) why += $" (+{hauls.Count} haul)";

        foreach (var intent in intents)
            if (intent is PlaceSiteIntent p)
                mem.PendingSite = (p.Tile, now);
        return new Decision(rung, why, intents);
    }

    // ---- background: logistics — every needed haul, distinct carriers ----

    private List<Intent> Logistics(Digest d, ViewDto view)
    {
        var intents = new List<Intent>();
        var urgent = view.InFamine
            || (view.FoodRunwayTicks >= 0 && view.FoodRunwayTicks < _cfg.FoodRunwayFloorTicks);

        // Recovery: a laden idle unit is a deadlock — every selector
        // requires empty cargo, so leftover cargo (a haul that over-picked
        // for a small site need) turns a citizen into a zombie carrying
        // the faction's wood. Walk them home and unload into the castle.
        foreach (var u in d.OwnUnits.Where(u =>
                     d.IsIdleStill(u) && d.IsFree(u) && u.CargoAmount > 0))
        {
            d.Reserve(u);
            intents.Add(u.X == d.CastleTile.X && u.Y == d.CastleTile.Y
                ? new UnloadCargoIntent(u.Id) { PlayerId = d.PlayerId }
                : new MoveIntent(u.Id, d.CastleTile) { PlayerId = d.PlayerId });
        }

        // Site deliveries — construction unblocks everything else. ONE
        // delivery in flight per site: a 25-capacity carrier over-fills a
        // 10-wood need, so racing three of them strands two with stuck
        // cargo (the zombie bug above). A delivery is "in flight" when any
        // own laden-or-hauling unit is heading for the site tile.
        foreach (var site in d.OwnSites())
        {
            var siteTile = Digest.TileOf(site);
            var inFlight = d.OwnUnits.Any(u =>
                u.DestX == siteTile.X && u.DestY == siteTile.Y
                && (u.Activity == (int)Activity.Hauling || u.CargoAmount > 0));
            if (inFlight) continue;
            foreach (var need in site.Needed)
            {
                var missing = need.Amount - Digest.AmountOf(site.Holdings, (Resource)need.Resource);
                if (missing <= 0) continue;
                if (Digest.AmountOf(d.Castle!.Holdings, (Resource)need.Resource) <= 0) continue;
                if (d.TakeIdleCarrier() is not { } carrier) return intents;
                intents.Add(new HaulIntent(carrier.Id, d.CastleTile, siteTile,
                    (Resource)need.Resource) { PlayerId = d.PlayerId });
                break;
            }
        }

        // Full (or famine-urgent) extractor buffers go home. SWARM the
        // buffer: keep allocating carriers until their combined capacity
        // covers it — most citizens carry only 5 (UnitCargoCatalog, a
        // rule, not state), and a single small carrier per think left the
        // farm dormant-at-cap for whole days while food flatlined at the
        // famine line.
        foreach (var ex in d.OwnExtractors())
        {
            var spec = StructureCatalog.Spec((StructureKind)ex.Kind);
            if (spec.OutputResource == Resource.None) continue;
            var remaining = Digest.AmountOf(ex.Holdings, spec.OutputResource);
            var urgentFood = spec.OutputResource == Resource.Food && urgent;
            for (var trips = 0; trips < 4; trips++)
            {
                var wanted = urgentFood ? remaining > 0 : remaining >= _cfg.HaulBufferThreshold;
                if (!wanted) break;
                if (d.TakeIdleCarrier() is not { } carrier) return intents;
                intents.Add(new HaulIntent(carrier.Id, Digest.TileOf(ex), d.CastleTile,
                    spec.OutputResource) { PlayerId = d.PlayerId });
                remaining -= UnitCargoCatalog.CapacityFor((UnitRole)carrier.Role);
            }
        }

        // Keep the house stocked for births while the larder is comfortable.
        if (view.CastleFood > _cfg.GrowthFoodFloor
            && d.OwnStructure(StructureKind.House) is { } house
            && Digest.AmountOf(house.Holdings, Resource.Food) < _cfg.BirthFoodCost
            && d.TakeIdleCarrier() is { } stocker)
            intents.Add(new HaulIntent(stocker.Id, d.CastleTile, Digest.TileOf(house),
                Resource.Food) { PlayerId = d.PlayerId });

        return intents;
    }

    // ---- rung 1: Eat — a working farm exists ------------------------------

    private Decision? Eat(Digest d, ViewDto view)
    {
        var farm = d.OwnExtractor(StructureKind.Farm);
        var site = d.OwnSite(StructureKind.Farm);
        if (farm is null && site is null)
        {
            if (d.NearestFreeTile(Biome.Grassland, _cfg.SiteSearchRange) is { } t)
                return new Decision("eat", "no farm — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.Farm) { PlayerId = d.PlayerId } });
            return null;   // no grassland in view — Scout will find some
        }
        if (site is not null && EnsureBuilders(d, site) is { Count: > 0 } b)
            return new Decision("eat", "building the farm", b);
        if (farm is not null && StaffExtractor(d, farm, UnitRole.Farmer) is { Count: > 0 } st)
            return new Decision("eat", "staffing the farm", st);
        return null;
    }

    // ---- rung 2: Build — a wood income exists ------------------------------

    private Decision? Build(Digest d)
    {
        var camp = d.OwnExtractor(StructureKind.LumberCamp);
        var site = d.OwnSite(StructureKind.LumberCamp);
        if (camp is null && site is null)
        {
            if (d.NearestFreeTile(Biome.Forest, _cfg.SiteSearchRange) is { } t)
                return new Decision("build", "no lumber camp — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.LumberCamp) { PlayerId = d.PlayerId } });
            return null;
        }
        if (site is not null && EnsureBuilders(d, site) is { Count: > 0 } b)
            return new Decision("build", "building the camp", b);
        if (camp is not null && StaffExtractor(d, camp, UnitRole.Lumberjack) is { Count: > 0 } st)
            return new Decision("build", "staffing the camp", st);
        return null;
    }

    // ---- rung 3: Grow — breed when the larder allows -----------------------

    private Decision? Grow(Digest d, ViewDto view)
    {
        if (view.CastleFood <= _cfg.GrowthFoodFloor) return null;

        var house = d.OwnStructure(StructureKind.House);
        var site = d.OwnSite(StructureKind.House);
        if (house is null && site is null)
        {
            if (d.NearestFreeTile(requiredBiome: null, _cfg.SiteSearchRange) is { } t)
                return new Decision("grow", "no house — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.House) { PlayerId = d.PlayerId } });
            return null;
        }
        if (site is not null && EnsureBuilders(d, site) is { Count: > 0 } b)
            return new Decision("grow", "building the house", b);
        if (house is null) return null;

        var houseTile = Digest.TileOf(house);
        if (Digest.AmountOf(house.Holdings, Resource.Food) < _cfg.BirthFoodCost)
            return null;   // logistics is stocking it

        var fertile = d.OwnUnits
            .Where(u => d.IsIdleStill(u) && d.IsFree(u)
                && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler)
                && u.Age >= _cfg.MinFertileAgeYears && u.Age <= _cfg.MaxFertileAgeYears)
            .OrderBy(u => u.Id).ToList();
        var onTile = fertile.Where(u => u.X == houseTile.X && u.Y == houseTile.Y).ToList();
        if (onTile.Count >= 2)
            return new Decision("grow", "parents in place — breeding",
                new List<Intent> { new BeginBreedingIntent(houseTile,
                    d.Reserve(onTile[0]).Id, d.Reserve(onTile[1]).Id) { PlayerId = d.PlayerId } });

        var movers = fertile.Where(u => u.X != houseTile.X || u.Y != houseTile.Y)
            .Take(2 - onTile.Count)
            .Select(Intent (u) => new MoveIntent(d.Reserve(u).Id, houseTile) { PlayerId = d.PlayerId })
            .ToList();
        return movers.Count > 0
            ? new Decision("grow", "sending parents to the house", movers)
            : null;
    }

    // ---- rung 4: Scout — reveal the frontier (it's fogged) ------------------

    private Decision? Scout(Digest d, AiMemory mem)
    {
        // SCOUT-ROLE UNITS ONLY. The first draft conscripted any idle
        // adult — and since Scout fires on every otherwise-quiet think,
        // the whole village ended up on 6-hour wander legs, the carrier
        // pool dried up, hauls starved, and the farm stalled at full
        // buffer. Scouting is a job, not a default.
        var scout = d.OwnUnits.FirstOrDefault(u =>
            d.IsIdleStill(u) && d.IsFree(u) && (UnitRole)u.Role == UnitRole.Scout);
        if (scout is null) return null;
        d.Reserve(scout);

        // Rotate through 8 compass legs — droppable memory; a restart just
        // restarts the sweep.
        var dirs = new (int X, int Y)[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
        var dir = dirs[mem.ScoutLeg++ % dirs.Length];
        var dest = new TileCoord(
            Math.Clamp(d.CastleTile.X + dir.X * _cfg.ScoutRange, 0, d.MapWidth - 1),
            Math.Clamp(d.CastleTile.Y + dir.Y * _cfg.ScoutRange, 0, d.MapHeight - 1));
        if (dest == new TileCoord(scout.X, scout.Y)) return null;
        return new Decision("scout", $"sweeping leg {mem.ScoutLeg - 1}",
            new List<Intent> { new MoveIntent(scout.Id, dest) { PlayerId = d.PlayerId } });
    }

    // ---- shared plays --------------------------------------------------------

    // Get the site its builders: assign the ones standing on it, march the
    // ones that aren't. (Materials are logistics' job.)
    private List<Intent> EnsureBuilders(Digest d, StructDto site)
    {
        var intents = new List<Intent>();
        if (site.BuildersPresent >= site.BuildersRequired) return intents;
        var siteTile = Digest.TileOf(site);
        var builders = d.OwnUnits.Where(u =>
                (UnitRole)u.Role == UnitRole.Builder && d.IsIdleStill(u) && d.IsFree(u)
                && u.CargoAmount == 0 && u.Age >= _cfg.MinAdultAgeYears)
            .OrderBy(u => u.Id).ToList();
        var onTile = builders.Where(u => u.X == siteTile.X && u.Y == siteTile.Y)
            .Select(u => d.Reserve(u).Id).ToList();
        if (onTile.Count > 0)
            intents.Add(new AssignBuildersIntent(siteTile, onTile) { PlayerId = d.PlayerId });
        else
            foreach (var b in builders.Take(site.BuildersRequired - site.BuildersPresent))
                intents.Add(new MoveIntent(d.Reserve(b).Id, siteTile) { PlayerId = d.PlayerId });
        return intents;
    }

    // Get an extractor staffed (prefer the matching role — the 2:1 rate
    // bonus — falling back to any non-builder adult). Farms staff to CAP:
    // food production must outrun a breeding population. Everything else
    // gets one worker — wood income doesn't race demographics.
    private List<Intent> StaffExtractor(Digest d, StructDto ex, UnitRole prefer)
    {
        var intents = new List<Intent>();
        var want = (StructureKind)ex.Kind == StructureKind.Farm ? ex.WorkerCap : 1;
        if (ex.Workers >= Math.Max(1, want)) return intents;

        var tile = Digest.TileOf(ex);
        // Builders belong on sites, Haulers on the road (their 25-capacity
        // is the logistics backbone — assigning one as a farmhand starves
        // the haul loop). Everyone else can work.
        var candidates = d.OwnUnits.Where(u =>
                d.IsIdleStill(u) && d.IsFree(u) && u.CargoAmount == 0
                && u.Age >= _cfg.MinAdultAgeYears
                && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler))
            .OrderBy(u => (UnitRole)u.Role == prefer ? 0 : 1).ThenBy(u => u.Id)
            .ToList();
        var onTile = candidates.FirstOrDefault(u => u.X == tile.X && u.Y == tile.Y);
        if (onTile is not null)
            intents.Add(new AssignWorkersIntent(tile, new[] { d.Reserve(onTile).Id }) { PlayerId = d.PlayerId });
        else if (candidates.FirstOrDefault() is { } walker)
            intents.Add(new MoveIntent(d.Reserve(walker).Id, tile) { PlayerId = d.PlayerId });
        return intents;
    }

    // ---- the view digest ------------------------------------------------------

    private sealed class Digest
    {
        public int PlayerId;
        public int MapWidth, MapHeight;
        public StructDto? Castle;
        public TileCoord CastleTile;
        public List<StructDto> Own = new();
        public List<UnitDto> OwnUnits = new();
        private readonly Dictionary<(int X, int Y), int> _biome = new();
        private readonly HashSet<(int X, int Y)> _blocked = new();   // structures + claims + blacklist

        // Per-think unit reservation, shared by EVERY selector (carriers,
        // staffing, builders, parents, scouts). Without it, two layers
        // task the same unit in one think and move-on-busy silently
        // cancels the first order — the bug that starved the bootstrap
        // (food hauls yanked off carriers by scout moves).
        private readonly HashSet<int> _reserved = new();
        public bool IsFree(UnitDto u) => !_reserved.Contains(u.Id);
        public UnitDto Reserve(UnitDto u) { _reserved.Add(u.Id); return u; }

        public static Digest Build(ViewDto view, AiConfig cfg, AiMemory mem)
        {
            var d = new Digest { PlayerId = view.PlayerId, MapWidth = view.Width, MapHeight = view.Height };
            foreach (var t in view.Visible) d._biome[(t.X, t.Y)] = t.Biome;
            foreach (var t in view.Remembered) d._biome.TryAdd((t.X, t.Y), t.Biome);
            foreach (var b in mem.BlacklistedTiles) d._blocked.Add(b);
            foreach (var s in view.Structures)
            {
                d._blocked.Add((s.X, s.Y));
                for (var i = 0; i < s.ClaimX.Length; i++) d._blocked.Add((s.ClaimX[i], s.ClaimY[i]));
                if (s.OwnerId != view.PlayerId) continue;
                d.Own.Add(s);
                if ((StructureKind)s.Kind == StructureKind.Castle)
                {
                    d.Castle = s;
                    d.CastleTile = new TileCoord(s.X, s.Y);
                }
            }
            d.OwnUnits = view.Units.Where(u => u.OwnerId == view.PlayerId).OrderBy(u => u.Id).ToList();
            return d;
        }

        public StructDto? OwnExtractor(StructureKind kind) =>
            Own.FirstOrDefault(s => (StructureKind)s.Kind == kind);
        public StructDto? OwnStructure(StructureKind kind) => OwnExtractor(kind);
        public StructDto? OwnSite(StructureKind target) =>
            Own.FirstOrDefault(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
                && (StructureKind)s.TargetKind == target);
        public IEnumerable<StructDto> OwnSites() =>
            Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite)
               .OrderBy(s => s.Y).ThenBy(s => s.X);
        public IEnumerable<StructDto> OwnExtractors() =>
            Own.Where(s => (StructureKind)s.Kind is StructureKind.Farm or StructureKind.LumberCamp
                or StructureKind.Quarry or StructureKind.Mine)
               .OrderBy(s => s.Y).ThenBy(s => s.X);

        // Idle AND standing still — a marching unit reads Activity.Idle
        // (movement lives on the arrival anchors), so "has no destination"
        // is the real stillness check. M16 lesson.
        public bool IsIdleStill(UnitDto u) => u.Activity == (int)Activity.Idle && u.DestX < 0;

        // Allocate a haul carrier for THIS think: idle, still, empty-handed,
        // never a Builder (conscripting builders as food mules is how the
        // camp site starved — they're needed on sites). Children may haul
        // (only role-tied assignments are age-gated); Haulers first for
        // the capacity.
        public UnitDto? TakeIdleCarrier()
        {
            var pick = OwnUnits.Where(u => IsIdleStill(u) && u.CargoAmount == 0
                    && (UnitRole)u.Role != UnitRole.Builder && IsFree(u))
                .OrderBy(u => (UnitRole)u.Role == UnitRole.Hauler ? 0 : 1)
                .ThenBy(u => u.Id)
                .FirstOrDefault();
            return pick is null ? null : Reserve(pick);
        }

        // Nearest known tile to the castle matching the biome requirement,
        // unoccupied and unclaimed — ring scan in (dist, y, x) order so the
        // choice is deterministic. requiredBiome null = any walkable land.
        public TileCoord? NearestFreeTile(Biome? requiredBiome, int range)
        {
            for (var r = 1; r <= range; r++)
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                var key = (CastleTile.X + dx, CastleTile.Y + dy);
                if (!_biome.TryGetValue(key, out var biome)) continue;
                if (requiredBiome is { } req ? biome != (int)req
                    : biome is (int)Biome.Water or (int)Biome.None) continue;
                if (_blocked.Contains(key)) continue;
                return new TileCoord(key.Item1, key.Item2);
            }
            return null;
        }

        public static int AmountOf(ResAmtDto[] holdings, Resource r) =>
            holdings.FirstOrDefault(h => h.Resource == (int)r)?.Amount ?? 0;

        public static TileCoord TileOf(StructDto s) => new(s.X, s.Y);
    }
}

// Droppable hints only — everything else is re-derived from the view.
public sealed class AiMemory
{
    public int ScoutLeg;
    // Site order awaiting confirmation + tiles where placement was
    // observed to fail (rejected server-side; skip them next search).
    public (Sim.Core.World.TileCoord Tile, long OrderedAt)? PendingSite;
    public HashSet<(int X, int Y)> BlacklistedTiles { get; } = new();
}
