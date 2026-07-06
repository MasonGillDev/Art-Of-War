using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// M25 — Probe: the Rival's scouting pressure (docs/m25-rival-spec.md).
// Rival-only ladder slot: fires while the WAR LEDGER IS BLIND — some
// living foreign faction has no castle on the intel map, or the intel
// it has is older than IntelStaleTicks — and the probe budget remains
// (ledger #9: every standing job has a bound; EnemyIntel refreshes the
// budget when the castle map changes).
//
// Same shape as ScoutRung's spiral (16 headings, deterministic), but a
// LONGER stride: enemy castles start >= 24 tiles out (the genesis
// separation a player learns by walking, not by reading world-gen), so
// probe legs start at ~2x ScoutRange and grow per revolution. When a
// castle-less foe has KNOWN structures, head for the newest of them
// instead — the castle is near the smoke.
public sealed class ProbeRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        if (ctx.Mem.ProbeLeg >= ctx.Cfg.ProbeLegBudget) return null;

        // Blind toward whom? Living foreign factions with no fresh castle intel.
        var blindFoes = ctx.View.Factions
            .Where(f => f.Id != ctx.PlayerId && !f.Defeated
                && (!ctx.Mem.KnownEnemyCastles.TryGetValue(f.Id, out var c)
                    || ctx.Now - c.Tick > ctx.Cfg.IntelStaleTicks))
            .Select(f => f.Id)
            .ToList();
        if (blindFoes.Count == 0) return null;

        var scout = ctx.OwnUnits.FirstOrDefault(u =>
            ctx.IsIdleStill(u) && ctx.IsFree(u) && (UnitRole)u.Role == UnitRole.Scout);
        if (scout is null) return null;

        // A castle-less foe with known structures: walk their newest
        // structure (deterministic tiebreak by tile) — sight en route
        // and around it does the rest.
        var lead = ctx.Mem.KnownEnemyStructures
            .Where(kv => blindFoes.Contains(kv.Value.OwnerId))
            .OrderByDescending(kv => kv.Value.Tick)
            .ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .Select(kv => (Tile: kv.Key, Has: true))
            .FirstOrDefault();

        TileCoord dest;
        if (lead.Has)
        {
            dest = new TileCoord(lead.Tile.X, lead.Tile.Y);
        }
        else
        {
            // Blind spiral, ScoutRung's 16 headings at probe stride.
            var dirs = new (int X, int Y)[]
            {
                (2, 0), (2, 1), (1, 1), (1, 2),
                (0, 2), (-1, 2), (-1, 1), (-2, 1),
                (-2, 0), (-2, -1), (-1, -1), (-1, -2),
                (0, -2), (1, -2), (1, -1), (2, -1),
            };
            var dir = dirs[ctx.Mem.ProbeLeg % dirs.Length];
            var reach = Math.Max(1, ctx.Cfg.ScoutRange * (2 + ctx.Mem.ProbeLeg / dirs.Length));
            dest = new TileCoord(
                Math.Clamp(ctx.CastleTile.X + dir.X * reach, 0, ctx.MapWidth - 1),
                Math.Clamp(ctx.CastleTile.Y + dir.Y * reach, 0, ctx.MapHeight - 1));
        }

        if (dest == new TileCoord(scout.X, scout.Y)) return null;
        ctx.Reserve(scout);
        ctx.Mem.ProbeLeg++;
        return new Decision("probe",
            lead.Has ? $"tracking smoke at {dest.X},{dest.Y}" : $"probing leg {ctx.Mem.ProbeLeg - 1}",
            new List<Intent> { new MoveIntent(scout.Id, dest) { PlayerId = ctx.PlayerId } });
    }
}
