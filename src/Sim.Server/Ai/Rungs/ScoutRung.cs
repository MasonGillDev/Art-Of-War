using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung 5: Scout — reveal the frontier (it's fogged).
public sealed class ScoutRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        // SCOUT-ROLE UNITS ONLY, and only within the exploration BUDGET —
        // unless the colony is LAND-STARVED, which re-opens scouting on
        // demand (a fixed lifetime budget left a 300-day colony starving
        // at pop 67 with the whole continent in the fog). Legs spiral
        // wider as the count grows, so renewed exploration pushes past
        // the already-known halo.
        if (ctx.Mem.ScoutLeg >= ctx.Cfg.ScoutLegBudget && !ctx.Mem.LandStarved) return null;
        var scout = ctx.OwnUnits.FirstOrDefault(u =>
            ctx.IsIdleStill(u) && ctx.IsFree(u) && (UnitRole)u.Role == UnitRole.Scout);
        if (scout is null) return null;
        ctx.Reserve(scout);

        // Rotate through 16 headings, each full sweep reaching farther —
        // droppable memory; a restart just restarts the sweep. (8 compass
        // rays were needles through a haystack on rugged maps: a meadow
        // off the compass lines was never seen.) Half-step headings use
        // 2:1 integer vectors, scaled to roughly matching reach.
        var dirs = new (int X, int Y, int Scale)[]
        {
            (2, 0, 1), (2, 1, 1), (1, 1, 1), (1, 2, 1),
            (0, 2, 1), (-1, 2, 1), (-1, 1, 1), (-2, 1, 1),
            (-2, 0, 1), (-2, -1, 1), (-1, -1, 1), (-1, -2, 1),
            (0, -2, 1), (1, -2, 1), (1, -1, 1), (2, -1, 1),
        };
        var dir = dirs[ctx.Mem.ScoutLeg % dirs.Length];
        var reach = Math.Max(1, ctx.Cfg.ScoutRange * (1 + ctx.Mem.ScoutLeg / dirs.Length) / 2);
        ctx.Mem.ScoutLeg++;
        var dest = new TileCoord(
            Math.Clamp(ctx.CastleTile.X + dir.X * reach, 0, ctx.MapWidth - 1),
            Math.Clamp(ctx.CastleTile.Y + dir.Y * reach, 0, ctx.MapHeight - 1));
        if (dest == new TileCoord(scout.X, scout.Y)) return null;
        return new Decision("scout", $"sweeping leg {ctx.Mem.ScoutLeg - 1}",
            new List<Intent> { new MoveIntent(scout.Id, dest) { PlayerId = ctx.PlayerId } });
    }
}
