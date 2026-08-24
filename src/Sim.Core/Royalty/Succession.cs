using Sim.Core.World;

namespace Sim.Core.Royalty;

// M31 — the crown passes (docs/king-and-dynasty.md §"Succession, minority,
// interregnum").
//
// THE SINGLE MUTATION POINT for Player.KingUnitId. Everything else in
// Sim.Core.Royalty is a pure read.
//
// It needs no death plumbing of its own: DeathByAgeEvent, StarvationDeathEvent
// and combat death all already converge on Population.OnUnitRemoved, so one
// hook there covers every way a monarch can die. That convergence is why the
// design doc's "sim cost: thin" survives contact with the code.
//
// WHAT DOES NOT HAPPEN HERE. No sweep over royal children (royalty is derived,
// so the whole line re-resolves the instant KingUnitId moves). No regency, no
// caretaker, no interim buff: between a king's death and a functioning
// successor the King's Buff is simply GONE. Clean and legible beats a
// half-state nobody can read off the screen.
public static class Succession
{
    // Called from Population.OnUnitRemoved for EVERY removed unit, royal or
    // not. Cheap for the 99.9% case: one dictionary lookup and an int compare.
    //
    // ORDER-INDEPENDENT BY CONSTRUCTION. The heir is derived from the DYING
    // KING'S ID rather than from the stored crown, so it does not matter
    // whether the caller has already pulled the body out of world.Units — and
    // callers genuinely differ: CombatRules.OnUnitDeath removes first and
    // notifies second, while the age and starvation paths notify first. Any
    // version that started from "look up the current king" crowned nobody on
    // the combat path, i.e. a king killed in battle ended his own dynasty.
    public static void OnRoyalRemoved(Simulation sim, Unit unit)
    {
        var world = sim.World;
        if (!world.Players.TryGetValue(unit.OwnerId, out var player)) return;
        if (player.KingUnitId != unit.Id) return;   // not the king: nothing to do

        // A dead royal CHILD needs no handling at all — the heir is derived,
        // so the next eldest simply becomes the answer to the same question.
        // Only the crown itself is stored, and only the crown moves here.
        var heir = Royalty.HeirApparentOf(world, unit.Id, unit.OwnerId);
        player.KingUnitId = heir?.Id;   // null = the line is extinct

        // Announce it. A succession the player only discovers by noticing
        // their army got weaker is the silent-failure problem the goal
        // milestone spent its whole visibility contract on; the chronicler
        // will read exactly this event when it exists.
        sim.Schedule(sim.Now, new SuccessionEvent(
            unit.OwnerId, unit.Id, heir?.Id,
            heir is not null && Royalty.IsMinor(world, heir, sim.Now)));
    }
}
