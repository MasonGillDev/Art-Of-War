using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M25 — the brain seam (docs/m25-rival-spec.md). AiPlayerDriver drives any
// IBrain; Homesteader and Rival are COMPOSITIONS of the same rung
// vocabulary — different ladders plus Rival-only rungs — not forks
// (docs/m17-defender-spec.md pre-locked this: "Rival becomes composition
// ... instead of a 900-line fork"). The fairness contract is unchanged:
// Think sees the projected ViewDto, the clock, and its own droppable
// memory — never GameWorld or Simulation (AiPlayerTests.Brain_TouchesOnlyTheView
// sweeps every type in this namespace).
public interface IBrain
{
    Decision Think(ViewDto view, long now, AiMemory mem);
}

// Which brain a faction runs. Chosen by the host at construction:
// --rivals K gives the HIGHEST K AI faction ids the Rival ladder, the
// rest stay Homesteaders (GameHost's assignment loop).
public enum BrainKind : byte { Homesteader = 0, Rival = 1 }

// The shared arbiter loop — extracted move-only from HomesteaderBrain
// (M25 Phase 1) so every brain runs the identical two-layer think:
//
//   * THE STRATEGIC LADDER — strict priority, first rung that emits
//     claims the think. Rungs with nothing to DO fall through, so an
//     in-progress goal never starves the rungs below.
//   * LOGISTICS — hauls are BACKGROUND, not decisions; runs AFTER the
//     ladder so strategic decisions reserve their units first
//     (arbitration lesson #5).
//
// `perceive` runs right after the digest is built and BEFORE the ladder —
// the Rival's enemy-intel pre-pass hangs there, so perception updates
// every think no matter which rung claims it (the same reasoning that put
// threat-memory updates at the top of DefendRung.TryClaim).
public static class BrainCore
{
    public static Decision Think(IRung[] ladder, AiConfig cfg, ViewDto view, long now, AiMemory mem,
        Action<ThinkContext>? perceive = null)
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

        // M26 — wall-line feedback, same observation discipline: the line
        // was ordered last think; if not a single tile of it grew a
        // structure, the server rejected the whole intent (fail-clean).
        // Halve the next attempt — a bisect hunt for the bad tile; a
        // length-1 rejection pins it exactly, so blacklist that tile. An
        // accepted line resets the cap to full.
        if (mem.PendingWall is { } pw && now > pw.OrderedAt)
        {
            var any = view.Structures.Any(s => pw.Tiles.Contains((s.X, s.Y)));
            if (!any)
            {
                if (pw.Tiles.Count == 1) mem.BlacklistedTiles.Add(pw.Tiles[0]);
                mem.WallSegmentCap = Math.Max(1, pw.Tiles.Count / 2);
            }
            else mem.WallSegmentCap = null;
            mem.PendingWall = null;
        }

        var ctx = ThinkContext.Build(view, cfg, mem, now);
        if (ctx.Castle is null) return new Decision("dead", "no castle", new List<Intent>());

        perceive?.Invoke(ctx);

        // STRATEGIC FIRST: decisions (place/staff/breed/scout) reserve
        // their units before logistics swarms the rest. The other order
        // let the haul swarm take every idle unit every think — the camp
        // sat unstaffed for 29 days while food piled up.
        Decision? strategic = null;
        foreach (var rung in ladder)
            if ((strategic = rung.TryClaim(ctx)) is not null) break;

        var intents = new List<Intent>();
        if (strategic is not null) intents.AddRange(strategic.Intents);
        var hauls = LogisticsLayer.Emit(ctx);
        intents.AddRange(hauls);

        var rung_ = strategic?.Rung ?? (hauls.Count > 0 ? "logistics" : "idle");
        var why = strategic?.Why ?? (hauls.Count > 0 ? $"{hauls.Count} haul(s)" : "all needs met");
        if (strategic is not null && hauls.Count > 0) why += $" (+{hauls.Count} haul)";

        foreach (var intent in intents)
            if (intent is PlaceSiteIntent p)
                mem.PendingSite = (p.Tile, now);
            else if (intent is Sim.Core.Fortifications.PlaceWallIntent w)
                mem.PendingWall = (w.Path.Select(t => (t.X, t.Y)).ToList(), now);
        return new Decision(rung_, why, intents);
    }
}
