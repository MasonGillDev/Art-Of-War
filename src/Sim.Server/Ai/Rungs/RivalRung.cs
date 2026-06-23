using Sim.Core.Diplomacy;
using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// M25 — the RIVAL: the offensive war brain (docs/m25-rival-spec.md). Two
// touchpoints, one cohesive component:
//
//   * Perceive(ctx) — the brain calls this EVERY think, before the ladder, the
//     way the Defend rung runs its threat memory first. It keeps the CAMPAIGN
//     book (are we at war? did we win?), evaluates the four casus belli against
//     the colony's personality, and emits the (cheap, telegraphed)
//     DeclareWarIntent. Declaring consumes no units, so the war DECISION ships
//     regardless of which rung claims the think — a busy economy never starves
//     it.
//
//   * TryClaim(ctx) — the ladder rung (below Muster) that DIRECTS the army the
//     campaign called up: form it, march it by strike doctrine, siege (Phase 4).
//     Economy-first by its low placement; it never marches a hungry colony.
//
// A Homesteader is peaceful: Perceive does only defensive bookkeeping and never
// opens a campaign, so the whole rung is inert for the default posture and the
// M17 balance lab is untouched. Retaliation, for a Homesteader, reduces to the
// Defend rung repelling the invader — it does not counter-invade.
public sealed class RivalRung : IRung
{
    // The ladder slot (below Muster) — DIRECT the field army the campaign called
    // up: march it onto the objective; siege is automatic on contact (M24). It
    // sits below the economy and Muster, so a colony feeds and arms before it
    // marches; and it never marches a starving colony. Returns null (yields the
    // think to the economy) whenever there's no order to give — the army is
    // still mustering, already marching, or holding the line.
    public Decision? TryClaim(ThinkContext ctx)
    {
        if (ctx.Mem.CampaignTarget is not { } target) return null;   // no war on
        if (ctx.View.InFamine) return null;                          // survival first
        if (ctx.Mem.CampaignObjective is not { } objTile) return null; // nothing located to hit

        var objective = new TileCoord(objTile.X, objTile.Y);

        // The field army: my free soldiers/archers (Defend, a higher rung, has
        // already reserved any it pulled for home defense — those aren't free).
        var army = ctx.OwnUnits
            .Where(u => (UnitRole)u.Role is UnitRole.Soldier or UnitRole.Archer && ctx.IsFree(u))
            .ToList();
        if (army.Count == 0) return null;

        // COMMIT GATE — don't trickle a token force into a siege. Hold until the
        // army reaches campaign strength (Muster builds toward it). Once any
        // blade is already committed (marching or on the objective), keep feeding
        // reinforcements regardless of the count — we don't recall a war.
        var committed = army.Any(u => !ctx.IsIdleStill(u)
            || (u.X == objective.X && u.Y == objective.Y));
        var strength = Math.Min(ctx.Cfg.CampaignArmySize,
            Math.Max(1, ctx.View.Population / Math.Max(1, ctx.Cfg.WarPopulationPerSoldier)));
        if (!committed && army.Count < strength) return null;   // still mustering

        // March every soldier that's idle-still and not already on the objective
        // onto it. Those already sieging stay put; those mid-march finish their
        // leg and re-target on arrival (the M16 don't-spam-anchors lesson).
        var movers = army
            .Where(u => ctx.IsIdleStill(u) && (u.X != objective.X || u.Y != objective.Y))
            .ToList();
        if (movers.Count == 0) return null;   // all marching or already engaged

        var intents = movers
            .Select(u => (Intent)new MoveIntent(ctx.Reserve(u).Id, objective) { PlayerId = ctx.PlayerId })
            .ToList();
        return new Decision("rival",
            $"campaign vs {target} ({ctx.Mem.CampaignReason}): {movers.Count} marching on {objective.X},{objective.Y}",
            intents);
    }

    // The perception pass — runs every think. Returns diplomatic intents
    // (war declarations now; peace offers in Phase 5).
    public List<Intent> Perceive(ThinkContext ctx)
    {
        var intents = new List<Intent>();
        Bookkeep(ctx);

        // PEACE first, for EVERY posture: a Homesteader dragged into a war must
        // still be able to end it. Accepting an olive branch flips the pair to
        // Neutral, which Bookkeep reads next think to stand the campaign down.
        AcceptPeace(ctx, intents);

        // Peaceful posture: defend only, never open an offensive war.
        if (!RivalDoctrine.IsWarCapable(ctx.Cfg.Personality)) return intents;

        // Already prosecuting a campaign → keep the objective fresh for the army
        // machine, make sure the war is on the books (covers a re-derived
        // campaign whose declaration never landed), and — for a LIMITED posture —
        // sue for peace once the grievance is settled or the army is spent.
        if (ctx.Mem.CampaignTarget is { } active)
        {
            UpdateObjective(ctx, active);
            MaybeDeclare(ctx, active, intents);
            MaybeSueForPeace(ctx, active, intents);
            return intents;
        }

        // RETALIATION — someone is bringing war to us (an incoming declaration or
        // an enemy state we didn't open) and we hold no campaign of our own:
        // answer it with a counter-offensive against the aggressor. No
        // declaration needed — the war is already coming.
        foreach (var f in ctx.OtherFactions())
            if ((ctx.IsHostileFaction(f) || ctx.HasPendingWarWith(f)) && !ctx.IsFactionDefeated(f))
            {
                Adopt(ctx, f, "retaliation");
                UpdateObjective(ctx, f);
                return intents;
            }

        // Proactive war needs a stable colony — never break the peace while the
        // larder is short (survival outranks conquest, the Eat-preempts-all rule).
        if (ctx.View.InFamine || ctx.View.CastleFood <= ctx.Cfg.GrowthFoodFloor) return intents;

        // ENCROACHMENT / OPPORTUNISM / LAND HUNGER (+ the Warlord's manufactured
        // war): pick a target, adopt it, declare.
        if (SelectTarget(ctx) is { } pick)
        {
            Adopt(ctx, pick.Faction, pick.Reason);
            UpdateObjective(ctx, pick.Faction);
            MaybeDeclare(ctx, pick.Faction, intents);
        }
        return intents;
    }

    // ---- siege objective (strike doctrine) -------------------------------

    // Choose the tile the army marches on next: the highest-priority visible
    // structure of the target — MILITARY (decapitate force projection) →
    // ECONOMY (collapse the war's fuel) → CASTLE (the decisive, win-condition
    // blow), nearest within a class. Held through re-fog so a column doesn't
    // lose its target mid-march; dropped only once the army has reached a
    // now-empty objective (razed, or it was never really there).
    private static void UpdateObjective(ThinkContext ctx, int target)
    {
        var best = ctx.VisibleStructuresOf(target)
            .OrderBy(s => StrikePriority((StructureKind)s.Kind))
            .ThenBy(s => Cheb(s.X, s.Y, ctx.CastleTile.X, ctx.CastleTile.Y))
            .ThenBy(s => s.Y).ThenBy(s => s.X)
            .FirstOrDefault();
        if (best is not null)
        {
            ctx.Mem.CampaignObjective = new TileCoord(best.X, best.Y);
            return;
        }
        // Nothing of the target's in sight. Keep marching toward the last-known
        // objective UNLESS the army is already standing on it (it's been razed,
        // or was a stale ghost) — then clear it so the column doesn't idle there.
        if (ctx.Mem.CampaignObjective is { } obj
            && ctx.OwnUnits.Any(u => (UnitRole)u.Role is UnitRole.Soldier or UnitRole.Archer
                && u.X == obj.X && u.Y == obj.Y))
            ctx.Mem.CampaignObjective = null;
    }

    private static int StrikePriority(StructureKind k) => k switch
    {
        StructureKind.Barracks or StructureKind.Tower => 0,   // military — break it first
        StructureKind.Castle => 2,                            // the keep — the killing blow, last
        _ => 1,                                               // economy — collapse it in between
    };

    private static int Cheb(int ax, int ay, int bx, int by) =>
        Math.Max(Math.Abs(ax - bx), Math.Abs(ay - by));

    // ---- campaign lifecycle ----------------------------------------------

    private static void Adopt(ThinkContext ctx, int faction, string reason)
    {
        ctx.Mem.CampaignTarget = faction;
        ctx.Mem.CampaignReason = reason;
    }

    // End a campaign when the war is OVER: the target was eliminated (Castle
    // razed → Defeated/gone), OR peace was made (the pair is no longer at war
    // and has no pending war — an accepted proposal flipped it back to Neutral).
    // The peace case can't misfire on the adoption think: the campaign is adopted
    // and declared in the SAME think, so by the next think the pair is already
    // pending/Enemy — "no longer at war" only reads true after a real peace.
    private static void Bookkeep(ThinkContext ctx)
    {
        if (ctx.Mem.CampaignTarget is not { } t) return;
        var eliminated = !ctx.OtherFactions().Any(f => f == t) || ctx.IsFactionDefeated(t);
        var atWar = ctx.IsHostileFaction(t) || ctx.HasPendingWarWith(t);
        if (eliminated || !atWar)
        {
            ctx.Mem.CampaignTarget = null;
            ctx.Mem.CampaignReason = "";
            ctx.Mem.CampaignObjective = null;
            ctx.Mem.PeaceProposedTo = null;
        }
    }

    // ---- war termination -------------------------------------------------

    // Take an olive branch when the posture wants peace: a Homesteader always
    // (it never wanted this war), an Opportunist always (it fights for gain, not
    // blood). A Warlord lets every offer lapse — it plays for the kill.
    private static void AcceptPeace(ThinkContext ctx, List<Intent> intents)
    {
        if (ctx.Cfg.Personality == AiPersonality.Warlord) return;
        foreach (var p in ctx.IncomingPeaceProposals())
            intents.Add(new RespondToProposalIntent(ctx.PlayerId, p.Id, accept: true)
                { PlayerId = ctx.PlayerId });
    }

    // The LIMITED stand-down (Opportunist only): once the grievance is settled
    // (nothing of the enemy's left to hit that we can see AND no trespasser in
    // our border) or the army is spent, sue for peace — one offer per campaign
    // (proposals don't dedup, so the flag throttles it). The war ends when the
    // enemy accepts; until then we hold (and still defend). A Warlord never
    // sues; a Homesteader never has a campaign to end this way.
    private static void MaybeSueForPeace(ThinkContext ctx, int target, List<Intent> intents)
    {
        if (ctx.Cfg.Personality != AiPersonality.Opportunist) return;
        if (ctx.Mem.PeaceProposedTo == target) return;                 // already on the table
        if (!ctx.IsHostileFaction(target) && !ctx.HasPendingWarWith(target)) return;   // not at war yet

        var trespass = ctx.NearestStructureDistOf(target) is { } d && d <= ctx.Cfg.EncroachmentRadius;
        var grievanceSettled = ctx.Mem.CampaignObjective is null && !trespass;
        var armySpent = ctx.OwnUnits.Count(u =>
            (UnitRole)u.Role is UnitRole.Soldier or UnitRole.Archer) <= 1;
        if (!grievanceSettled && !armySpent) return;

        intents.Add(new ProposeRelationshipIntent(ctx.PlayerId, target, RelationshipState.Neutral)
            { PlayerId = ctx.PlayerId });
        ctx.Mem.PeaceProposedTo = target;
    }

    // Emit the telegraphed declaration — but only when the war isn't already on
    // (Enemy) or pending, and the pair is Neutral (never declare on an Ally).
    // Idempotent across thinks: once the war is pending, this stops firing.
    private static void MaybeDeclare(ThinkContext ctx, int target, List<Intent> intents)
    {
        if (ctx.IsHostileFaction(target) || ctx.HasPendingWarWith(target)) return;
        if (ctx.RelationshipStateWith(target) != (int)RelationshipState.Neutral) return;
        intents.Add(new DeclareWarIntent(ctx.PlayerId, target) { PlayerId = ctx.PlayerId });
    }

    // ---- casus belli -----------------------------------------------------

    private readonly record struct Target(int Faction, string Reason);

    // Evaluate the proactive triggers against every DECLARABLE rival (a Neutral,
    // undefeated faction I can currently see a structure of), nearest first.
    // The personality decides which triggers are live and whether a Warlord
    // manufactures one when nothing fires.
    private static Target? SelectTarget(ThinkContext ctx)
    {
        var cfg = ctx.Cfg;
        var warlord = cfg.Personality == AiPersonality.Warlord;
        var myArmy = ctx.OwnArmyPower();
        var mySoldiers = ctx.OwnUnits.Count(u =>
            (Sim.Core.World.UnitRole)u.Role is Sim.Core.World.UnitRole.Soldier
                or Sim.Core.World.UnitRole.Archer);

        var rivals = ctx.OtherFactions()
            .Where(f => ctx.RelationshipStateWith(f) == (int)RelationshipState.Neutral)
            .Where(f => !ctx.IsFactionDefeated(f))
            .Select(f => (Faction: f, Dist: ctx.NearestStructureDistOf(f)))
            .Where(r => r.Dist is not null)
            .OrderBy(r => r.Dist!.Value)
            .ToList();

        foreach (var (faction, dist) in rivals)
        {
            // ENCROACHMENT — a rival structure inside my border. The trespass
            // the user named; the most common opening to a war.
            if (dist!.Value <= cfg.EncroachmentRadius)
                return new Target(faction, "encroachment");

            if (dist.Value > cfg.CampaignReachTiles) continue;   // too far to project force

            // OPPORTUNISM — I keep a standing army AND can overwhelm the force
            // guarding them (a predator picks fights it wins).
            if (mySoldiers >= cfg.SoldierQuotaFloor && myArmy > 0)
            {
                var guard = ctx.VisibleCombatPowerOf(faction);
                if (myArmy >= guard + 1
                    && (long)myArmy * 100 >= (long)Math.Max(1, guard) * cfg.OpportunismPowerPercent)
                    return new Target(faction, "opportunism");
            }

            // LAND HUNGER — boxed in (the land bank ran dry) and they hold
            // reachable ground.
            if (ctx.Mem.LandStarved)
                return new Target(faction, "land hunger");
        }

        // WARLORD — absent any provocation, march on the nearest reachable rival
        // anyway. Conquest is the goal; a casus belli is a formality.
        if (warlord)
        {
            var reachable = rivals.FirstOrDefault(r => r.Dist!.Value <= cfg.CampaignReachTiles);
            if (reachable.Dist is not null)
                return new Target(reachable.Faction, "conquest");
        }

        return null;
    }
}
