using System;
using Sim.Core;        // Time vocabulary (Minute/Hour/Day/...)
using Sim.Core.World;  // TileCoord

namespace Sim.Server.Scouting;

// M20 Phase 2 — the LEXICAL pass: turning sim values into a traveler's
// terms. Pure, deterministic, no sim state touched. This is where the
// audited LLM failures are made structurally impossible: the model is never
// handed a tile count or a raw tick, only finished traveler's language
// ("half a day's ride to the east", "on my second day out"). Geometry and
// distance are pure functions of coordinates; count bands are pure functions
// of (count, quality). Nothing here is a flavor choice the model could get
// wrong.
//
// Grid convention: +X = east, +Y = south (screen coordinates, y-down), so
// north is -Y.
public static class Lexicon
{
    // Integer distance in tiles between two points (Euclidean, floored). The
    // Math.Sqrt seed is corrected to an exact integer below, so the result is
    // platform-independent despite the float — and this is presentation code,
    // never hashed.
    public static int RangeTiles(TileCoord a, TileCoord b)
    {
        long dx = a.X - b.X;
        long dy = a.Y - b.Y;
        return Isqrt(dx * dx + dy * dy);
    }

    public static int Isqrt(long n)
    {
        if (n <= 0) return 0;
        var x = (long)Math.Sqrt(n);
        while (x > 0 && x * x > n) x--;
        while ((x + 1) * (x + 1) <= n) x++;
        return (int)x;
    }

    // Vision quality as an integer percent: 100% on the scout's own tile,
    // falling to 0% at the edge of its sight. Drives whether a count is exact
    // or banded.
    public static int QualityPct(int rangeTiles, int radius)
    {
        if (radius <= 0) return 0;
        var q = 100 * (radius - rangeTiles) / radius;
        return q < 0 ? 0 : q > 100 ? 100 : q;
    }

    // The count as the lord should read it. Returns the exact number when the
    // sighting was good; otherwise a band — and the TRUE count does not
    // appear in the returned phrase.
    public static string CountPhrase(int trueCount, int qualityPct, ScoutReportConstants cfg)
    {
        if (qualityPct >= cfg.ExactCountQualityPct)
            return trueCount == 1 ? "a lone man" : $"{trueCount} of them";

        var half = Math.Max(1, trueCount * (100 - qualityPct) / cfg.BandWidthDivisor);
        var lo = Math.Max(1, trueCount - half);
        var hi = trueCount + half;
        return $"between {lo} and {hi}";
    }

    // True when CountPhrase would band rather than state an exact number.
    public static bool IsBanded(int qualityPct, ScoutReportConstants cfg) =>
        qualityPct < cfg.ExactCountQualityPct;

    // 8-wind compass from a delta, pure integer. "" for a zero delta.
    public static string Bearing(int dx, int dy, ScoutReportConstants cfg)
    {
        if (dx == 0 && dy == 0) return "";
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        var diagonal = Math.Min(ax, ay) * cfg.DiagonalDenominator
                       >= Math.Max(ax, ay) * cfg.DiagonalNumerator;

        string ns = dy < 0 ? "north" : dy > 0 ? "south" : "";
        string ew = dx > 0 ? "east" : dx < 0 ? "west" : "";

        if (diagonal && ns != "" && ew != "") return $"{ns}-{ew}";
        // Cardinal: the dominant axis wins.
        return ax >= ay ? ew : ns;
    }

    // Crow-flies tile distance rendered as a traveler's ride-time. Bucketed so
    // an empty ride stays short and a long haul reads in days — never tiles.
    public static string RideTime(int tiles, ScoutReportConstants cfg)
    {
        long ticks = (long)tiles * cfg.RideTicksPerTile;
        if (ticks <= 0) return "no distance at all";
        if (ticks < 2 * Time.Hour) return "a short ride";
        if (ticks < 6 * Time.Hour) return "a few hours' ride";
        if (ticks < 3 * Time.Day / 4) return "half a day's ride";
        if (ticks < 3 * Time.Day / 2) return "a day's ride";
        var days = (int)((ticks + Time.Day / 2) / Time.Day); // round
        return $"{days} days' ride";
    }

    // "near {anchorName}", or with distance+bearing when the tile is well off
    // the anchor: "half a day's ride to the north-east of your keep".
    public static string LocationPhrase(
        TileCoord tile, TileCoord anchor, string anchorName, ScoutReportConstants cfg)
    {
        var tiles = RangeTiles(tile, anchor);
        if (tiles == 0) return $"hard by {anchorName}";
        var bearing = Bearing(tile.X - anchor.X, tile.Y - anchor.Y, cfg);
        var ride = RideTime(tiles, cfg);
        if (bearing == "") return $"{ride} from {anchorName}";
        return $"{ride} to the {bearing} of {anchorName}";
    }

    // Journey-relative day stamp from the observation tick. Day 1 is the day
    // of dispatch. Gives the LLM real pacing so it invents none.
    public static string DayStamp(long observedTick, long dispatchTick)
    {
        var day = (observedTick - dispatchTick) / Time.Day + 1;
        if (day <= 0) day = 1;
        return day switch
        {
            1 => "on the first day out",
            2 => "on the second day",
            3 => "on the third day",
            _ => $"on the {Ordinal(day)} day",
        };
    }

    // "perhaps two days' work done" from a build-progress tick count.
    public static string WorkDone(long progressTicks)
    {
        if (progressTicks < Time.Hour) return "barely begun";
        if (progressTicks < Time.Day)
        {
            var hours = (int)((progressTicks + Time.Hour / 2) / Time.Hour);
            return hours <= 1 ? "perhaps an hour's work in it" : $"perhaps {hours} hours' work in it";
        }
        var days = (int)((progressTicks + Time.Day / 2) / Time.Day);
        return days <= 1 ? "perhaps a day's work in it" : $"perhaps {days} days' work in it";
    }

    // Integer build-progress percent for a (progress, duration) pair.
    public static int PercentDone(long progressTicks, long durationTicks)
    {
        if (durationTicks <= 0) return 100;
        var pct = (int)(100 * progressTicks / durationTicks);
        return pct < 0 ? 0 : pct > 100 ? 100 : pct;
    }

    // Deterministic display name for a faction. Owner 0 is the player; foreign
    // owners draw a stable house name; bandits (owner -1) ride under no banner.
    public static string FactionName(int ownerId)
    {
        if (ownerId == Sim.Core.Bandits.BanditConstants.OwnerId) return "brigands under no banner";
        if (ownerId == 0) return "your own";
        var houses = new[]
        {
            "Ashford", "Blackwood", "Hollin", "Marsh", "Vance",
            "Dunmore", "Reyne", "Storr", "Whitlock", "Garrow",
        };
        return $"House {houses[(ownerId - 1) % houses.Length]}";
    }

    // Plain-spoken name for what a structure is.
    public static string StructureNoun(StructureKind kind) => kind switch
    {
        StructureKind.Castle     => "keep",
        StructureKind.Tower      => "watchtower",
        StructureKind.House      => "dwelling",
        StructureKind.Farm       => "farmstead",
        StructureKind.LumberCamp => "logging camp",
        StructureKind.Quarry     => "quarry",
        StructureKind.Mine       => "mine",
        StructureKind.Dock       => "wharf",
        StructureKind.School     => "hall",
        StructureKind.Barracks   => "barracks",
        StructureKind.Stockpile  => "store",
        _                        => "works",
    };

    private static string Ordinal(long n) => n switch
    {
        1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth",
        6 => "sixth", 7 => "seventh", 8 => "eighth", 9 => "ninth", 10 => "tenth",
        _ => $"{n}th",
    };
}
