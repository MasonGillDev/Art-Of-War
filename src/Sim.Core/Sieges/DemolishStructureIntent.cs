using System.Text.Json.Serialization;
using Sim.Core.Logistics;

namespace Sim.Core.Sieges;

// Pull down one of your own standing structures, right now, for nothing
// (docs/demolish.md). No unit walks over, no labour, no refund: the tile is
// EMPTY afterwards — buildable again in the same tick — and whatever the
// structure held lands on the ground as a pile, the raze economy without
// the wreckage.
//
// Rejection cases (per docs/intent-validation.md, every check re-runs at
// resolution time, mutates nothing on failure):
//   * Tile out of bounds, or no structure there.
//   * Rubble / Cache — nobody's, so nobody's to demolish (rubble is cleared
//     via ClearRubbleIntent; a cache is looted).
//   * Not the issuing player's structure.
//   * A ConstructionSite — cancelling a job in progress is its own decision
//     with its own path (deferred; see the doc).
//   * The Castle — its loss is defeat (PlayerDefeatedEvent), never a tidy-up.
//
// Order of operations, because each step reads the structure while it still
// stands: workers and builders posted here are released (WorkAssignment.
// Release, which needs the structure to fix its counts), a house's residents
// move back to the castle (Population.SetHome, which needs the house to
// decrement its bed count and close its food window), and only then does
// SiegeDamage.Teardown run the shared removal — fertility catch-up, vault
// spill, the swap (to nothing), and the release of anyone still walking
// here. A fortification under siege closes its combat state the way a
// breach does; a unit fight on an ordinary tile is left to its own round,
// which ends itself when it finds no hostile pair.
public sealed class DemolishStructureIntent : Intent
{
    public TileCoord Tile { get; }

    [JsonConstructor]
    public DemolishStructureIntent(TileCoord tile) { Tile = tile; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Grid.InBounds(Tile))
            return IntentOutcome.Reject($"tile {Tile.X},{Tile.Y} out of bounds");
        if (!world.Structures.TryGetValue(Tile, out var s))
            return IntentOutcome.Reject($"no structure at {Tile.X},{Tile.Y}");
        if (s.Kind is StructureKind.Rubble or StructureKind.Cache or StructureKind.Idol or StructureKind.BanditCamp)
            return IntentOutcome.Reject($"{s.Kind} at {Tile.X},{Tile.Y} is nobody's to demolish");
        if (s.OwnerId != PlayerId)
            return IntentOutcome.Reject($"{s.Kind} at {Tile.X},{Tile.Y} is not yours");
        if (s is ConstructionSite)
            return IntentOutcome.Reject($"site at {Tile.X},{Tile.Y} is still under way; cancel it, don't demolish it");
        if (s.Kind == StructureKind.Castle)
            return IntentOutcome.Reject("a castle is not demolished; losing it is defeat");

        // 1. Release everyone posted here, id order (deterministic mutation
        //    sequence), while the structure still stands so Release can fix
        //    the worker / builder bookkeeping on it.
        var posted = world.Units.Values
            .Where(u => u.Assignment == Tile && u.Activity is Activity.Working or Activity.Building)
            .OrderBy(u => u.Id)
            .ToList();
        foreach (var u in posted) WorkAssignment.Release(sim, u);

        // 2. A house's residents go home to the castle: SetHome closes both
        //    food windows and frees the beds, and needs the house present.
        if (s is House)
        {
            var residents = world.Units.Values
                .Where(u => u.Home == Tile)
                .OrderBy(u => u.Id)
                .ToList();
            foreach (var u in residents) Sim.Core.Population.Population.SetHome(sim, u, null);
        }

        // 3. The shared removal — no rubble.
        var wasFort = Sim.Core.Fortifications.Fortification.IsStandingFortification(s);
        SiegeDamage.Teardown(sim, s, leaveRubble: false, reason: "demolished");

        // 4. A besieged fort's siege ends with the fort (FortSiege's breach
        //    shape); the pending round event stales out on its anchor.
        if (wasFort) world.CombatStates.Remove(Tile);

        return IntentOutcome.Applied;
    }

    public override string Describe() => $"Demolish(@ {Tile.X},{Tile.Y})";
}
