using System;
using System.Collections.Generic;
using System.Linq;
using Sim.Core.Scouting;
using Sim.Core.World;

namespace Sim.Server.Scouting;

// M20 Phase 2 — THE claims compiler: the truth oracle that turns a scout's
// raw observation log into ordered, honest, natural-language CLAIMS. Truth
// lives here; the LLM (Phase 4) only restyles the sentences this produces.
//
// PURE READ. It reads the mission's log (already fog-filtered at capture, so
// fog-honesty is inherited) plus the owner's OWN structures (always known to
// the player — used only for relative anchoring, never to see foreign
// things). It mutates nothing and is server-side, so it never touches the
// determinism hash. Deterministic by construction (canonical iteration, no
// RNG, no clock), which is what makes the golden-file tests meaningful.
//
// Two passes, as designed in docs/m20-scouting-reports-spec.md: the SEMANTIC
// pass here (what claims exist — compression, dedup, clustering, closure +
// impression rules, negative claims) and the LEXICAL pass in Lexicon (how to
// say them plainly).
public static class ClaimsCompiler
{
    public static ScoutReport Compile(
        GameWorld world, ScoutMission mission, ScoutReportConstants? constants = null)
    {
        var cfg = constants ?? new ScoutReportConstants();
        var ownerId = mission.OwnerId;
        var legs = mission.Legs;
        var dispatchTick = mission.DispatchTick;
        var returnTick = legs.Count > 0 ? legs[^1].Tick : dispatchTick;

        var (home, homeName) = ResolveHome(world, ownerId, legs);

        // Own structures (always known to the player) → relative anchors, in
        // canonical (y, x) order so nearest-anchor ties break deterministically.
        var ownStructures = world.Structures.Values
            .Where(s => s.OwnerId == ownerId)
            .Select(s => (s.At, s.Kind))
            .OrderBy(s => s.At.Y).ThenBy(s => s.At.X)
            .ToList();

        // --- dedup: best (highest-quality) observation per foreign unit /
        //     per foreign structure tile. Best fidelity wins, exactly as the
        //     spec's "dedupe repeated sightings, best fidelity" rule asks. ---
        var bestUnit = new Dictionary<int, UnitObs>();
        var bestStruct = new Dictionary<TileCoord, StructObs>();
        foreach (var leg in legs)
        {
            foreach (var s in leg.Sightings)
            {
                var range = Lexicon.RangeTiles(s.Tile, leg.Center);
                var quality = Lexicon.QualityPct(range, leg.Radius);
                foreach (var su in s.Units)
                {
                    if (su.OwnerId == ownerId) continue; // own men are not news
                    if (!bestUnit.TryGetValue(su.UnitId, out var prev) || quality > prev.Quality)
                        bestUnit[su.UnitId] = new UnitObs(su, s.Tile, leg.Tick, range, quality);
                }
                if (s.Structure is { } st && st.OwnerId != ownerId)
                {
                    if (!bestStruct.TryGetValue(s.Tile, out var prevS) || quality > prevS.Quality)
                        bestStruct[s.Tile] = new StructObs(st, s.Tile, leg.Tick, range, quality);
                }
            }
        }

        var journey = new List<(long tick, Claim claim)>();
        var limits = new List<Claim>();

        // --- (a) empty-ground compression: maximal runs of legs with no
        //     foreign content collapse into one claim each. ---
        EmitEmptyGround(legs, ownerId, home, homeName, ownStructures, cfg, journey);

        // --- (b) forces: cluster foreign units into bodies of men. ---
        EmitForces(bestUnit, bestStruct, ownerId, home, homeName, ownStructures, dispatchTick, cfg, journey, limits);

        // --- (c) structures: foreign works, with closure on incomplete ones. ---
        EmitStructures(bestStruct, home, homeName, ownStructures, dispatchTick, cfg, journey, limits);

        // --- (d) the negative claim: the country beyond the turnaround. ---
        EmitBeyond(legs, home, homeName, ownStructures, cfg, limits);

        // Order: journey claims by observation tick (stable → a force's
        // trailing impression stays with it), then the limits/unknowns block.
        var ordered = journey
            .OrderBy(j => j.tick)
            .Select(j => j.claim)
            .Concat(limits)
            .ToList();

        var sequenced = new List<Claim>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            sequenced.Add(new Claim
            {
                Sequence = i,
                Kind = c.Kind,
                Certainty = c.Certainty,
                Text = c.Text,
                Anchor = c.Anchor,
                ObservedTick = c.ObservedTick,
                RangeTiles = c.RangeTiles,
                VisionQualityPct = c.VisionQualityPct,
                Novel = c.Novel,
            });
        }

        return new ScoutReport
        {
            ScoutUnitId = mission.ScoutUnitId,
            OwnerId = ownerId,
            DispatchTick = dispatchTick,
            ReturnTick = returnTick,
            Claims = sequenced,
        };
    }

    // ---- (a) empty ground ------------------------------------------------

    private static void EmitEmptyGround(
        IReadOnlyList<ObservationLeg> legs, int ownerId, TileCoord home, string homeName,
        List<(TileCoord At, StructureKind Kind)> ownStructures, ScoutReportConstants cfg,
        List<(long, Claim)> journey)
    {
        var i = 0;
        while (i < legs.Count)
        {
            if (IsNewsworthy(legs[i], ownerId)) { i++; continue; }
            var start = i;
            while (i < legs.Count && !IsNewsworthy(legs[i], ownerId)) i++;
            // Run [start, i) is empty of news. Describe it by its deepest point.
            var run = legs.Skip(start).Take(i - start).ToList();
            var deepest = run.OrderByDescending(l => Lexicon.RangeTiles(l.Center, home))
                             .ThenBy(l => l.Center.Y).ThenBy(l => l.Center.X).First();
            var loc = Lexicon.LocationPhrase(deepest.Center, NearestAnchor(deepest.Center, ownStructures, home, homeName).tile,
                                             NearestAnchor(deepest.Center, ownStructures, home, homeName).name, cfg);
            journey.Add((run[0].Tick, new Claim
            {
                Kind = ClaimKind.EmptyGround,
                Certainty = ClaimCertainty.Observed,
                Text = $"The country {loc} lay empty — no men, no works, no fresh tracks.",
                Anchor = deepest.Center,
                ObservedTick = run[0].Tick,
                Novel = true,
            }));
        }
    }

    // ---- (b) forces ------------------------------------------------------

    private static void EmitForces(
        Dictionary<int, UnitObs> bestUnit, Dictionary<TileCoord, StructObs> bestStruct,
        int ownerId, TileCoord home, string homeName,
        List<(TileCoord At, StructureKind Kind)> ownStructures, long dispatchTick,
        ScoutReportConstants cfg, List<(long, Claim)> journey, List<Claim> limits)
    {
        // Group by faction, then cluster spatially within each faction.
        foreach (var group in bestUnit.Values
                     .GroupBy(o => o.Unit.OwnerId)
                     .OrderBy(g => g.Key))
        {
            var members = group.OrderBy(o => o.Unit.UnitId).ToList();
            foreach (var cluster in Cluster(members, cfg.ForceClusterRadius))
            {
                var n = cluster.Count;
                var rep = cluster
                    .OrderByDescending(o => o.Quality).ThenBy(o => o.Range)
                    .ThenBy(o => o.Tile.Y).ThenBy(o => o.Tile.X).ThenBy(o => o.Unit.UnitId)
                    .First();
                var earliestTick = cluster.Min(o => o.Tick);
                var onMarch = cluster.Any(o => o.Unit.Activity == Activity.Moving);
                var faction = Lexicon.FactionName(group.Key);
                var (anchorTile, anchorName) = NearestAnchor(rep.Tile, ownStructures, home, homeName);
                var loc = Lexicon.LocationPhrase(rep.Tile, anchorTile, anchorName, cfg);
                var count = Lexicon.CountPhrase(n, rep.Quality, cfg);
                var banded = Lexicon.IsBanded(rep.Quality, cfg);
                var posture = onMarch ? "on the march" : "at a halt";
                var day = Lexicon.DayStamp(earliestTick, dispatchTick);

                var text = $"Armed men, {faction}: {count}, {posture}, {loc}, {day}";
                text += banded
                    ? $" — I came no closer than {Lexicon.RideTime(rep.Range, cfg)}, and the ground hid their number."
                    : ".";

                journey.Add((earliestTick, new Claim
                {
                    Kind = ClaimKind.Force,
                    Certainty = banded ? ClaimCertainty.Estimated : ClaimCertainty.Observed,
                    Text = text,
                    Anchor = rep.Tile,
                    ObservedTick = earliestTick,
                    RangeTiles = rep.Range,
                    VisionQualityPct = rep.Quality,
                    Novel = true,
                }));

                // Impression (tagged): a same-faction works rising within reach
                // reads as entrenching — a scout's guess, never a fact line.
                var nearbyWorks = bestStruct.Values.FirstOrDefault(st =>
                    st.Structure.OwnerId == group.Key &&
                    Chebyshev(st.Tile, rep.Tile) <= cfg.ForceClusterRadius &&
                    st.Structure.Kind == StructureKind.ConstructionSite);
                if (nearbyWorks is not null)
                {
                    journey.Add((earliestTick, new Claim
                    {
                        Kind = ClaimKind.Impression,
                        Certainty = ClaimCertainty.Impression,
                        Text = $"By the {Lexicon.StructureNoun(nearbyWorks.Structure.TargetKind)} rising beside them, " +
                               "they looked to be digging in, not passing through — though that is my read, not a thing I can swear to.",
                        Anchor = rep.Tile,
                        ObservedTick = earliestTick,
                        Novel = true,
                    }));
                }

                // Closure rules — the canonical unknowns this sighting implies.
                limits.Add(new Claim
                {
                    Kind = ClaimKind.Unknown,
                    Certainty = ClaimCertainty.NotObserved,
                    Text = $"What {faction} intends, I cannot say — I saw no march on us, only men at their ground.",
                    Anchor = rep.Tile,
                    Novel = true,
                });
                if (banded)
                    limits.Add(new Claim
                    {
                        Kind = ClaimKind.Unknown,
                        Certainty = ClaimCertainty.NotObserved,
                        Text = $"Their full strength I could not count; take my number for a scout's guess, not a tally.",
                        Anchor = rep.Tile,
                        Novel = true,
                    });
            }
        }
    }

    // ---- (c) structures --------------------------------------------------

    private static void EmitStructures(
        Dictionary<TileCoord, StructObs> bestStruct, TileCoord home, string homeName,
        List<(TileCoord At, StructureKind Kind)> ownStructures, long dispatchTick,
        ScoutReportConstants cfg, List<(long, Claim)> journey, List<Claim> limits)
    {
        foreach (var obs in bestStruct.Values
                     .OrderBy(o => o.Tile.Y).ThenBy(o => o.Tile.X))
        {
            var st = obs.Structure;
            var faction = Lexicon.FactionName(st.OwnerId);
            var (anchorTile, anchorName) = NearestAnchor(obs.Tile, ownStructures, home, homeName);
            var loc = Lexicon.LocationPhrase(obs.Tile, anchorTile, anchorName, cfg);
            var day = Lexicon.DayStamp(obs.Tick, dispatchTick);

            if (st.Kind == StructureKind.ConstructionSite)
            {
                var pct = Lexicon.PercentDone(st.ProgressTicks, st.BuildDurationTicks);
                var work = Lexicon.WorkDone(st.ProgressTicks);
                journey.Add((obs.Tick, new Claim
                {
                    Kind = ClaimKind.Structure,
                    Certainty = ClaimCertainty.Observed,
                    Text = $"A {Lexicon.StructureNoun(st.TargetKind)} being raised by {faction}, {loc} — about {pct} in the hundred done, {work}, {day}.",
                    Anchor = obs.Tile,
                    ObservedTick = obs.Tick,
                    RangeTiles = obs.Range,
                    VisionQualityPct = obs.Quality,
                    Novel = true,
                }));
                limits.Add(new Claim
                {
                    Kind = ClaimKind.Unknown,
                    Certainty = ClaimCertainty.NotObserved,
                    Text = $"Whether that {Lexicon.StructureNoun(st.TargetKind)} is meant as a border fort or a staging camp, I cannot tell.",
                    Anchor = obs.Tile,
                    Novel = true,
                });
            }
            else
            {
                journey.Add((obs.Tick, new Claim
                {
                    Kind = ClaimKind.Structure,
                    Certainty = ClaimCertainty.Observed,
                    Text = $"A {faction} {Lexicon.StructureNoun(st.Kind)} stands {loc}, {day}.",
                    Anchor = obs.Tile,
                    ObservedTick = obs.Tick,
                    RangeTiles = obs.Range,
                    VisionQualityPct = obs.Quality,
                    Novel = true,
                }));
            }
        }
    }

    // ---- (d) the country beyond the turnaround ---------------------------

    private static void EmitBeyond(
        IReadOnlyList<ObservationLeg> legs, TileCoord home, string homeName,
        List<(TileCoord At, StructureKind Kind)> ownStructures, ScoutReportConstants cfg,
        List<Claim> limits)
    {
        if (legs.Count == 0) return;
        var farthest = legs
            .OrderByDescending(l => Lexicon.RangeTiles(l.Center, home))
            .ThenBy(l => l.Center.Y).ThenBy(l => l.Center.X).First();
        if (Lexicon.RangeTiles(farthest.Center, home) == 0) return;
        var bearing = Lexicon.Bearing(farthest.Center.X - home.X, farthest.Center.Y - home.Y, cfg);
        var (anchorTile, anchorName) = NearestAnchor(farthest.Center, ownStructures, home, homeName);
        var loc = Lexicon.LocationPhrase(farthest.Center, anchorTile, anchorName, cfg);
        var beyond = bearing == "" ? "Beyond there" : $"Beyond there, further to the {bearing},";
        limits.Add(new Claim
        {
            Kind = ClaimKind.NotObserved,
            Certainty = ClaimCertainty.NotObserved,
            Text = $"I rode as far as {loc}. {beyond} my road did not carry me; that country is dark to me.",
            Anchor = farthest.Center,
            Novel = true,
        });
    }

    // ---- helpers ---------------------------------------------------------

    private static bool IsNewsworthy(ObservationLeg leg, int ownerId) =>
        leg.Sightings.Any(s =>
            s.Units.Any(u => u.OwnerId != ownerId) ||
            (s.Structure is { } st && st.OwnerId != ownerId));

    private static (TileCoord tile, string homeName) ResolveHome(
        GameWorld world, int ownerId, IReadOnlyList<ObservationLeg> legs)
    {
        var castle = world.Structures.Values
            .Where(s => s.OwnerId == ownerId && s.Kind == StructureKind.Castle)
            .OrderBy(s => s.At.Y).ThenBy(s => s.At.X)
            .FirstOrDefault();
        if (castle is not null) return (castle.At, "your keep");
        return (legs.Count > 0 ? legs[0].Center : new TileCoord(0, 0), "your camp");
    }

    private static (TileCoord tile, string name) NearestAnchor(
        TileCoord from, List<(TileCoord At, StructureKind Kind)> ownStructures,
        TileCoord home, string homeName)
    {
        if (ownStructures.Count == 0) return (home, homeName);
        var best = ownStructures[0];
        var bestD = Lexicon.RangeTiles(from, best.At);
        foreach (var s in ownStructures.Skip(1))
        {
            var d = Lexicon.RangeTiles(from, s.At);
            if (d < bestD) { best = s; bestD = d; }
        }
        var noun = best.Kind == StructureKind.Castle ? "keep" : Lexicon.StructureNoun(best.Kind);
        return (best.At, $"your {noun}");
    }

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // Spatial single-link clustering (DSU) over best-observation tiles.
    private static List<List<UnitObs>> Cluster(List<UnitObs> members, int radius)
    {
        var n = members.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); }

        for (var a = 0; a < n; a++)
            for (var b = a + 1; b < n; b++)
                if (Chebyshev(members[a].Tile, members[b].Tile) <= radius)
                    Union(a, b);

        var byRoot = new SortedDictionary<int, List<UnitObs>>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!byRoot.TryGetValue(r, out var list)) byRoot[r] = list = new List<UnitObs>();
            list.Add(members[i]);
        }
        return byRoot.Values.ToList();
    }

    private sealed record UnitObs(SightedUnit Unit, TileCoord Tile, long Tick, int Range, int Quality);
    private sealed record StructObs(SightedStructure Structure, TileCoord Tile, long Tick, int Range, int Quality);
}
