using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Bandits;

// M16 — surviving bandits melt back into the fog, TAKING THEIR CARGO
// WITH THEM. Stolen goods you fail to intercept leave the world here;
// that's the punishment for ignoring a raid, and the reason chasing a
// fleeing party with a scout matters: while any player can see a
// bandit's tile, this intent rejects and the driver has to keep
// running deeper.
//
// SERVER-INTERNAL like the spawn (wire rejects it; defense in depth on
// PlayerId below).
//
// Preconditions (per unit):
//   * exists, owned by the bandit faction, not embarked;
//   * its tile is invisible to every non-bandit faction;
//   * no active combat on its tile (the engagement pin can't be
//     despawned out of — fight it out or die).
//
// Removal: cargo is cleared FIRST (so nothing drops), then the unit
// goes through CombatRules.OnUnitDeath — the M7 single death pipeline
// (group cleanup, in-flight event fencing, Population.OnUnitRemoved).
public sealed class DespawnBanditPartyIntent : Intent
{
    public int[] UnitIds { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DespawnBanditPartyIntent(int[] unitIds)
    {
        UnitIds = unitIds;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (PlayerId != BanditConstants.OwnerId)
            return IntentOutcome.Reject(
                $"only the bandit driver may despawn bandits (PlayerId={PlayerId})");
        if (UnitIds is null || UnitIds.Length == 0)
            return IntentOutcome.Reject("no units named");
        if (UnitIds.Distinct().Count() != UnitIds.Length)
            return IntentOutcome.Reject("duplicate unit ids");

        // Validate ALL before mutating ANY — the party despawns atomically
        // or not at all (a half-vanished party would leave the driver's
        // model and the world disagreeing).
        var units = new List<Unit>(UnitIds.Length);
        foreach (var id in UnitIds)
        {
            if (!world.Units.TryGetValue(id, out var u))
                return IntentOutcome.Reject($"unit {id} does not exist");
            if (u.OwnerId != BanditConstants.OwnerId)
                return IntentOutcome.Reject($"unit {id} is not a bandit");
            if (u.IsEmbarked)
                return IntentOutcome.Reject($"unit {id} is embarked");
            if (BanditRules.IsSeenByAnyPlayer(world, u.Position))
                return IntentOutcome.Reject(
                    $"unit {id} at {u.Position} is visible to a player — keep running");
            if (world.CombatStates.ContainsKey(u.Position))
                return IntentOutcome.Reject(
                    $"unit {id} at {u.Position} is on a contested tile");
            units.Add(u);
        }

        foreach (var u in units)
        {
            // The loot vanishes with them — clear cargo so OnUnitDeath's
            // drop-to-ground (the capture economy) has nothing to drop.
            u.CargoAmount = 0;
            u.CargoResource = Resource.None;
            Sim.Core.Combat.CombatRules.OnUnitDeath(sim, u);
        }
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"DespawnBanditParty(units=[{string.Join(",", UnitIds)}])";
}
