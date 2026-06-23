using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung 0: Defend — the TOP of the ladder (M17 Phase 2): dead farmers
// don't farm, so an active raid preempts everything. When the threat
// memory is cold this rung emits nothing and the ladder behaves
// exactly as peacetime.
//
// PERCEPTION runs here unconditionally: because Defend is first, its
// TryClaim executes every think, so the threat memory updates whether
// or not anything claims the think. Bandits carry no Sight.Reveal —
// they appear in the fogged view exactly when a human would see them,
// and vanish back into the fog when they leave it (hence MEMORY: the
// brain chases last-known positions, not live tracking).
//
// Combat itself is automatic on hostile contact — "defend" reduces to
// moving the right units to the right tiles:
//   * SORTIE: every standing soldier converges on the freshest
//     actionable sighting (newest tick, then nearest the castle —
//     raiders run their loot home through your fields).
//   * PURSUIT WITH A LEASH (user-locked: pursue): sightings beyond
//     PursuitLeashTiles of the castle are ignored — an unleashed
//     chase is the scout-job-creep bug with swords (lesson #9).
//   * STAND-DOWN: threat cold → strays walk home to the garrison.
//   * CIVILIAN DOCTRINE (the lab's A/B knob): recall ON evacuates
//     workers inside the danger radius and pauses staffing there
//     (the guards live in ThinkContext's shared plays); OFF lets
//     them work through the raid.
//
// Loot recovery is DEFERRED: dropped cargo piles aren't on the wire
// at all yet (neither brain nor human client can see them) — pinned
// in docs/m17-defender-spec.md as a core/wire work item.
public sealed class DefendRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        UpdateThreatMemory(ctx);

        // Actionable = fresh AND inside the pursuit leash.
        var actionable = ctx.Mem.SightedHostiles
            .Where(kv => ctx.Now - kv.Value.Tick <= ctx.Cfg.ThreatMemoryTicks
                && Cheb(kv.Key, (ctx.CastleTile.X, ctx.CastleTile.Y)) <= ctx.Cfg.PursuitLeashTiles)
            .OrderByDescending(kv => kv.Value.Tick)
            .ThenBy(kv => Cheb(kv.Key, (ctx.CastleTile.X, ctx.CastleTile.Y)))
            .ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
            .ToList();

        var soldiers = ctx.OwnUnits
            .Where(u => (UnitRole)u.Role == UnitRole.Soldier).ToList();

        if (actionable.Count == 0)
        {
            // Stand down: the garrison walks home. (Soldiers mid-march
            // finish their leg and come home a think later.)
            var home = ctx.OwnStructure(StructureKind.Barracks) is { } b
                ? ThinkContext.TileOf(b) : ctx.CastleTile;
            var moves = soldiers
                .Where(u => ctx.IsIdleStill(u) && ctx.IsFree(u)
                    && (u.X != home.X || u.Y != home.Y))
                .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, home)
                    { PlayerId = ctx.PlayerId })
                .ToList();
            return moves.Count == 0 ? null
                : new Decision("defend", "threat cold — garrison stands down", moves);
        }

        var target = actionable[0].Key;
        var intents = new List<Intent>();

        // FORCE PARITY before the sortie (rules read: the combat
        // catalog prices both sides; own units' effective Power is in
        // the view). Trickling two soldiers into a four-bandit party
        // is a gift of swords — outmatched, the garrison HOLDS (and
        // the war-footing quota in Muster is meanwhile raising the
        // roster toward the counted headcount). Civilian recalls below
        // still run either way.
        // M25 — hostile power is the SUMMED per-tile estimate the
        // perception banked (role-priced; soldiers and archers, not
        // just bandits), so parity is true strength, not a head-count
        // times the bandit rate.
        var hostilePower = actionable.Sum(kv => kv.Value.Power);
        var ownPower = soldiers.Sum(u => u.Power >= 0 ? u.Power
            : Sim.Core.Combat.UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower);
        if (ownPower >= hostilePower)
        {
            // Sortie: converge. Only still soldiers take new orders — a
            // marching soldier finishes its leg and re-targets on arrival
            // (the Idle-while-moving lesson: don't spam orders at anchors).
            foreach (var s in soldiers.Where(u => ctx.IsIdleStill(u) && ctx.IsFree(u)
                         && (u.X != target.X || u.Y != target.Y)))
                intents.Add(new MoveIntent(ctx.Reserve(s).Id, new TileCoord(target.X, target.Y))
                    { PlayerId = ctx.PlayerId });
        }

        // Doctrine ON: evacuate working crews inside the danger radius
        // (the matching staffing pause lives in ThinkContext's shared
        // plays, so Eat doesn't march replacements in one think behind
        // the recall), and idle civilians inside it run for the castle.
        if (ctx.Cfg.RecallCiviliansUnderRaid)
        {
            foreach (var ex in ctx.OwnExtractors())
            {
                if (ex.Workers == 0 || !ctx.UnderThreat(ThinkContext.TileOf(ex))) continue;
                var crew = ctx.OwnUnits.Where(u =>
                        u.X == ex.X && u.Y == ex.Y && u.Activity == (int)Activity.Working)
                    .Select(u => u.Id).ToList();
                if (crew.Count > 0)
                    intents.Add(new UnassignWorkersIntent(ThinkContext.TileOf(ex), crew)
                        { PlayerId = ctx.PlayerId });
            }
            foreach (var u in ctx.OwnUnits.Where(u => ctx.IsIdleStill(u) && ctx.IsFree(u)
                         && (UnitRole)u.Role is not (UnitRole.Soldier or UnitRole.Archer)
                         && ctx.UnderThreat(new TileCoord(u.X, u.Y))
                         && (u.X != ctx.CastleTile.X || u.Y != ctx.CastleTile.Y)))
                intents.Add(new MoveIntent(ctx.Reserve(u).Id, ctx.CastleTile)
                    { PlayerId = ctx.PlayerId });
        }

        if (intents.Count == 0) return null;   // everyone already tasked/mid-march
        return new Decision("defend",
            ownPower >= hostilePower
                ? $"hostiles last seen at {target.X},{target.Y} — sortie ({soldiers.Count} soldiers)"
                : $"outmatched ({ownPower}pw vs {hostilePower}pw) — holding, civilians recalled",
            intents);
    }

    private static int Cheb((int X, int Y) a, (int X, int Y) b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // Sightings: refresh on sight, clear on re-observed-empty, expire
    // on staleness.
    // M25 — a hostile is a bandit (faction -1, a RULE constant) OR a unit
    // owned by a faction this colony is at WAR with (the view's public
    // diplomacy — IsHostileFaction). Each tile banks both a head-count and a
    // role-priced POWER estimate (enemy Power is hidden in the fair view, so we
    // read it from the visible role like a human would). This is the same
    // doctrine the M17 defender ran against bandits — now it guards the border
    // against rival armies too, for EVERY personality (even a peaceful
    // Homesteader defends its turf).
    private static void UpdateThreatMemory(ThinkContext ctx)
    {
        var seen = new Dictionary<(int X, int Y), (int Count, int Power)>();
        foreach (var u in ctx.View.Units)
        {
            if (!ctx.IsHostileFaction(u.OwnerId)) continue;
            var key = (u.X, u.Y);
            var prev = seen.GetValueOrDefault(key);
            seen[key] = (prev.Count + 1, prev.Power + ThinkContext.EstimatedPower(u));
        }
        foreach (var (tile, agg) in seen)
            ctx.Mem.SightedHostiles[tile] = (ctx.Now, agg.Count, agg.Power);
        foreach (var tile in ctx.Mem.SightedHostiles.Keys.ToList())
        {
            var expired = ctx.Now - ctx.Mem.SightedHostiles[tile].Tick > ctx.Cfg.ThreatMemoryTicks;
            var observedEmpty = ctx.VisibleTiles.Contains(tile) && !seen.ContainsKey(tile);
            if (expired || observedEmpty) ctx.Mem.SightedHostiles.Remove(tile);
        }
    }
}
