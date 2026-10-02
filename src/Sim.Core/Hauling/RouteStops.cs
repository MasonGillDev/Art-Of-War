namespace Sim.Core.Hauling;

// The stop-list rules every route intent shares (M45): Set and Update must
// accept exactly the same stops, so the checks live once, here.
public static class RouteStops
{
    // Why `stops` can't be a route's stop list, or null when it can.
    // Structural checks only: a stop with no structure of the player's is
    // legal (a waypoint, or a building not up yet); its rules move nothing.
    public static string? Check(GameWorld world, IReadOnlyList<RouteStop> stops)
    {
        if (stops.Count == 0)
            return "a route needs at least one stop";
        if (stops.Count > HaulingConstants.MaxStopsPerRoute)
            return $"route of {stops.Count} stops exceeds cap {HaulingConstants.MaxStopsPerRoute}";

        foreach (var stop in stops)
        {
            if (stop is null)
                return "null stop";
            if (!world.Grid.InBounds(stop.Tile))
                return $"stop {stop.Tile.X},{stop.Tile.Y} out of bounds";
            if (stop.Rules.Count > HaulingConstants.MaxRulesPerStop)
                return $"stop {stop.Tile.X},{stop.Tile.Y} has {stop.Rules.Count} rules (cap {HaulingConstants.MaxRulesPerStop})";
            foreach (var rule in stop.Rules)
            {
                if (rule.Resource == Resource.None)
                    return "a stop rule needs a resource";
                if (rule.Op != StopRuleOp.Pickup && rule.Op != StopRuleOp.Drop)
                    return $"unknown stop rule op {(byte)rule.Op}";
                if (rule.Percent < 1 || rule.Percent > 100)
                    return $"percent {rule.Percent} must be 1..100";
            }
        }
        return null;
    }

    // Deep copy: an intent's lists belong to the durable log, a route's to the world.
    public static List<RouteStop> Copy(IEnumerable<RouteStop> stops) =>
        stops.Select(s => new RouteStop { Tile = s.Tile, Rules = new List<StopRule>(s.Rules) }).ToList();

    // Where a crew heading to old stop `current` should head on the new list.
    //
    // The crew keeps its place in the loop, read by TILE, not by index: if the
    // stop it was walking to survives the edit, it still walks there. If that
    // stop was removed, it goes on to the next old stop (in loop order) that
    // survives. A tile that appears several times keeps its occurrence: the
    // second visit to the granary stays the second visit. Nothing survives
    // (every tile replaced): the new first stop.
    public static int Remap(IReadOnlyList<RouteStop> old, IReadOnlyList<RouteStop> fresh, int current)
    {
        if (old.Count == 0 || fresh.Count == 0) return 0;
        current = ((current % old.Count) + old.Count) % old.Count;
        for (var k = 0; k < old.Count; k++)
        {
            var idx = (current + k) % old.Count;
            var tile = old[idx].Tile;
            var rank = 0;
            for (var i = 0; i < idx; i++)
                if (old[i].Tile == tile) rank++;

            var firstMatch = -1;
            var seen = 0;
            for (var j = 0; j < fresh.Count; j++)
            {
                if (fresh[j].Tile != tile) continue;
                if (firstMatch < 0) firstMatch = j;
                if (seen++ == rank) return j;
            }
            if (firstMatch >= 0) return firstMatch;   // fewer visits now: the first one
        }
        return 0;
    }
}

// A route's name (M45): trimmed, at most MaxRouteNameLength characters, no
// control characters. "" means unnamed and the client shows the number.
public static class RouteNames
{
    public static string? Clean(string? raw, out string clean)
    {
        clean = (raw ?? "").Trim();
        if (clean.Length > HaulingConstants.MaxRouteNameLength)
            return $"route name is {clean.Length} characters (cap {HaulingConstants.MaxRouteNameLength})";
        foreach (var c in clean)
            if (char.IsControl(c))
                return "route name has a control character";
        return null;
    }
}
