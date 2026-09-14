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

        SyncRoles(world, unit.OwnerId);
    }

    // M31 — KEEP THE ROLES HONEST.
    //
    // The crown used to be purely derived, and Royalty's own header argued that a
    // stored flag "would be a second source of truth about the same fact". Making
    // King and Heir real UnitRoles makes that flag real, so this is the one place
    // that keeps the stored roles equal to the derived truth — and
    // RoyalRolesMatchTheDerivedLine is the test that fails the moment they part.
    //
    // PROMOTION ONLY, and that is a property rather than a shortcut. The heir is
    // the ELDEST living child of the current king, so no younger sibling can ever
    // overtake a living heir; heirship moves only when its holder dies or is
    // crowned. A living unit therefore never has to be demoted out of a royal
    // role, which is also what makes "royalty is for life" true rather than
    // aspirational.
    //
    // Called after every crown movement and after every birth, because those are
    // the only two events that can change who the answer is.
    public static void SyncRoles(GameWorld world, int ownerId)
    {
        if (Royalty.King(world, ownerId) is { } king && king.Role != UnitRole.King)
            king.SetRoleForCrowning(UnitRole.King);

        if (Royalty.HeirApparent(world, ownerId) is { } heir && heir.Role != UnitRole.Heir)
            heir.SetRoleForCrowning(UnitRole.Heir);
    }
}
