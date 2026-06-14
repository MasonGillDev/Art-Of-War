namespace Sim.Core.Scouting;

// M20 — THE one write site for a scout's observation log. Called only from
// MoveArrivalEvent.Apply, immediately after Sight.Reveal (it records the very
// disc Reveal just revealed). This mirrors the single-call-site discipline
// the determinism audit pins for Sight.Reveal and Road.CreditTraffic.
//
// Fog-honest BY CONSTRUCTION: it iterates only the scout's own live vision
// disc (Euclidean, radius = Sight.RadiusFor(role)). Nothing outside the
// scout's eyes can enter the log — the M17 fairness signature, applied to
// intelligence. No Explored/RememberedBiome consulted: this is live sight,
// not memory.
public static class ScoutObservation
{
    public static void Capture(Simulation sim, Unit scout)
    {
        var world = sim.World;
        if (!world.ScoutMissions.TryGetValue(scout.Id, out var mission)) return;
        if (mission.State == ScoutMissionState.Returned) return; // log is final

        var center = scout.Position;
        var radius = Sight.RadiusFor(scout.Role);
        if (radius <= 0) return;
        var rSquared = radius * radius;

        var leg = new ObservationLeg { Tick = sim.Now, Center = center, Radius = radius };

        // One pass over Units (SortedDictionary → id order) buckets the
        // units inside the disc by tile, each bucket already id-ordered.
        // O(units) per arrival — the same bounded-per-call scan flagged for
        // BuildersPresent in docs/determinism-audit.md; a per-tile unit index
        // is the scale fix when it bites. Capture early-returns for units
        // with no mission, so non-scouts never pay this.
        Dictionary<TileCoord, List<SightedUnit>>? unitsByTile = null;
        foreach (var u in world.Units.Values)
        {
            if (u.Id == scout.Id) continue;   // never report yourself
            if (u.IsEmbarked) continue;       // off-tile, doesn't occupy
            var udx = u.Position.X - center.X;
            var udy = u.Position.Y - center.Y;
            if (udx * udx + udy * udy > rSquared) continue;
            unitsByTile ??= new Dictionary<TileCoord, List<SightedUnit>>();
            if (!unitsByTile.TryGetValue(u.Position, out var bucket))
                unitsByTile[u.Position] = bucket = new List<SightedUnit>();
            bucket.Add(new SightedUnit(u.Id, u.OwnerId, u.Role, u.Activity));
        }

        // Walk the disc in canonical (y, x) order; emit a Sighting for every
        // tile holding units and/or a structure. Empty tiles are skipped —
        // their emptiness is recorded implicitly by the leg's (Center, Radius)
        // coverage, which is what makes observed-empty distinct from never-seen.
        var grid = world.Grid;
        var xLo = Math.Max(0, center.X - radius);
        var xHi = Math.Min(grid.Width - 1, center.X + radius);
        var yLo = Math.Max(0, center.Y - radius);
        var yHi = Math.Min(grid.Height - 1, center.Y + radius);
        for (var y = yLo; y <= yHi; y++)
        {
            var dy = y - center.Y;
            var dy2 = dy * dy;
            for (var x = xLo; x <= xHi; x++)
            {
                var dx = x - center.X;
                if (dx * dx + dy2 > rSquared) continue;
                var tile = new TileCoord(x, y);
                List<SightedUnit>? units = null;
                unitsByTile?.TryGetValue(tile, out units);
                world.Structures.TryGetValue(tile, out var structure);
                if (units is null && structure is null) continue;
                leg.Sightings.Add(new Sighting
                {
                    Tile = tile,
                    Biome = BiomeDegradation.BiomeAt(world, tile, sim.Now, world.BiomeDegradationConfig),
                    Units = units ?? new List<SightedUnit>(),
                    Structure = structure is null ? null : Describe(structure, sim.Now),
                });
            }
        }

        mission.Legs.Add(leg);
    }

    private static SightedStructure Describe(Structure s, long now) => s is ConstructionSite cs
        ? new SightedStructure
        {
            Kind = s.Kind,                       // ConstructionSite
            TargetKind = cs.TargetKind,          // what is being raised
            OwnerId = s.OwnerId,
            ProgressTicks = EffectiveProgress(cs, now),
            BuildDurationTicks = cs.BuildDurationTicks,
        }
        : new SightedStructure
        {
            Kind = s.Kind,
            TargetKind = s.Kind,                 // finished: kind is its own target
            OwnerId = s.OwnerId,
            ProgressTicks = 0,
            BuildDurationTicks = 0,
        };

    // Build progress as the scout WOULD have seen it: banked ticks plus the
    // live delta if the site is actively building. Pure read — mirrors the
    // Pause() formula without writing to the site (capture never mutates the
    // world it observes).
    private static long EffectiveProgress(ConstructionSite cs, long now)
    {
        var p = cs.ProgressTicks;
        if (cs.IsActive) p += now - cs.LastActiveAtTick!.Value;
        if (p > cs.BuildDurationTicks) p = cs.BuildDurationTicks;
        return p < 0 ? 0 : p;
    }
}
