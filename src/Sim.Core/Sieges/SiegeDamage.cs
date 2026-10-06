using Sim.Core.Engine;
using Sim.Core.World;

namespace Sim.Core.Sieges;

// M24 — the destruction half of the siege seam. CombatRoundEvent applies
// raw HP damage in line; when a structure's Health hits zero this is what
// turns it into rubble (and, for a castle, fires the PlayerDefeatedEvent
// that lands in Phase D). Kept separate from CombatRoundEvent so the
// "what does razing do?" decisions live in one obvious place.
//
// MUTATION POLICY: called only from CombatRoundEvent.Apply after siege
// damage reduces a structure's Health to <= 0. Mutates Structures (swap
// to Rubble), schedules future events (PlayerDefeatedEvent for castles —
// wired in Phase D). See docs/sieges-and-conquest.md.
public static class SiegeDamage
{
    // Replace the destroyed structure with a Rubble pile on the same tile.
    // The pile is unowned (OwnerId = SiegeConstants.RubbleOwnerId), so
    // every "this player's structures" iteration naturally skips it. A
    // razed Castle schedules the player-defeat event.
    public static void RazeStructure(Simulation sim, Structure structure)
    {
        var razedKind = structure.Kind;
        var formerOwner = structure.OwnerId;
        var at = structure.At;

        Teardown(sim, structure, leaveRubble: true, reason: "razed");

        // M24 — castle destruction defeats the owner. Schedule (rather
        // than mutate inline) so the transition lands in ResolvedLog and
        // a defeated player's intents reject cleanly via the IntentEvent
        // gate from this tick onward. The event idempotency-fences so
        // duplicate firings — even from a future "two castles razed same
        // tick" edge — are safe.
        if (razedKind == StructureKind.Castle && formerOwner >= 0)
            sim.Schedule(sim.Now, new PlayerDefeatedEvent(formerOwner, at));
    }

    // THE ONE removal path for a standing structure, shared by razing
    // (leaveRubble: true) and the owner's own DemolishStructureIntent
    // (leaveRubble: false — docs/demolish.md). Everything that "the
    // container stops existing" means lives here exactly once: the
    // fertility catch-up, the vault spill, the swap, and the release of
    // anyone still walking here. Callers own what is specific to them
    // (castle defeat for a raze; worker / resident release for a demolish).
    internal static void Teardown(Simulation sim, Structure structure, bool leaveRubble, string reason)
    {
        var world = sim.World;
        var at = structure.At;

        // M26 — removing a PRODUCING extractor is a rate-changing event for
        // its claimed tiles (the M9/§2.5 anchor discipline): catch their
        // fertility up under the OLD rate — the extractor is still in the
        // world and armed here, so the derivation includes it — BEFORE the
        // removal changes the rate. Without this, a later read re-interprets
        // the whole producing window under the post-removal rate and the
        // soil damage evaporates retroactively. (Its claims themselves free
        // the moment the removal lands — Claims.ClaimantAt scans live
        // structures only; the land is immediately claimable.)
        if (structure is Extractor razedExtractor)
            Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
                world, razedExtractor, sim.Now, world.BiomeDegradationConfig);

        // The vault spills before the walls come down (2026-07-13):
        // whatever the structure held — a storage's holdings (a castle's
        // whole treasury), an extractor's buffer, a construction site's
        // delivered materials — lands on the tile as a ground pile, the
        // same loot economy as a dying unit's cargo drop. Removal destroys
        // the CONTAINER, not the goods: the victor can haul the vault
        // home (what makes dead kingdoms worth scavenging), bandits can
        // steal from the ruins, and nothing simply vanishes. Food homes
        // spill Holdings as-read — the lazy consumption clock is NOT
        // caught up first (the structure is about to stop existing; its
        // pending food events already fence on the structure lookup).
        switch (structure)
        {
            case StorageStructure ss:
                foreach (var (r, amt) in ss.Holdings) Spill(world, at, r, amt);
                break;
            case Extractor ex:
                foreach (var (r, amt) in ex.Output) Spill(world, at, r, amt);
                // Refining: the input store spills too — the ore and fuel a
                // razed smelter was fed are loot like everything else.
                foreach (var (r, amt) in ex.Inputs) Spill(world, at, r, amt);
                break;
            case ConstructionSite cs:
                foreach (var (r, amt) in cs.Delivered) Spill(world, at, r, amt);
                break;
        }

        // Direct dictionary mutation: we are REPLACING (or clearing) the
        // entry, not adding a fresh one.
        world.Structures.Remove(at);
        // A bridge (or one being built) stands on a canal: taking it away leaves
        // the canal, not rubble (docs/structure-footprints.md).
        if (Bridge.IsDeck(structure))
            world.AddStructure(new Canal(at) { OwnerId = structure.OwnerId });
        else if (leaveRubble)
            world.AddStructure(new Rubble(at) { OwnerId = SiegeConstants.RubbleOwnerId });

        // M30 — anyone walking toward this tile to work, build or breed there
        // is now walking toward nothing. A traveller would find out on arrival,
        // but a unit already WAITING here has nothing left to wake it, so the
        // release happens at the removal. docs/goal-shaped-intents.md.
        Sim.Core.Intents.GoalRules.OnStructureRemoved(sim, at, reason);
    }

    // Merge into the tile's ground pile (CombatRules.OnUnitDeath's
    // cargo-drop shape, shared by every loot source).
    private static void Spill(GameWorld world, TileCoord at, Resource r, int amount)
    {
        if (amount <= 0 || r == Resource.None) return;
        if (!world.GroundResources.TryGetValue(at, out var pile))
        {
            pile = new SortedDictionary<Resource, int>();
            world.GroundResources[at] = pile;
        }
        pile.TryGetValue(r, out var existing);
        pile[r] = existing + amount;
    }
}
