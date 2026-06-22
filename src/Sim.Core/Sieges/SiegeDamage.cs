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
    // every "this player's structures" iteration naturally skips it. Phase
    // D will detect Castle here and schedule the player-defeat event.
    public static void RazeStructure(Simulation sim, Structure structure)
    {
        var world = sim.World;
        var at = structure.At;
        var razedKind = structure.Kind;
        var formerOwner = structure.OwnerId;

        // Direct dictionary mutation: we are REPLACING the entry, not
        // adding a fresh one. The structure being razed is already keyed
        // here; Remove + AddStructure would also work, but the explicit
        // swap is clearer at the call site.
        world.Structures.Remove(at);
        world.AddStructure(new Rubble(at) { OwnerId = SiegeConstants.RubbleOwnerId });

        // M24 — castle destruction defeats the owner. Schedule (rather
        // than mutate inline) so the transition lands in ResolvedLog and
        // a defeated player's intents reject cleanly via the IntentEvent
        // gate from this tick onward. The event idempotency-fences so
        // duplicate firings — even from a future "two castles razed same
        // tick" edge — are safe.
        if (razedKind == StructureKind.Castle && formerOwner >= 0)
            sim.Schedule(sim.Now, new PlayerDefeatedEvent(formerOwner, at));
    }
}
