namespace Sim.Core.Progression;

// M37 — what a milestone does when it fires (docs/progression.md). A closed set
// of typed records that grows one type at a time.
//
// Apply runs inside the sim at the moment the milestone fires, and must be
// deterministic: no RNG draws of its own, no wall clock.
public abstract record Effect
{
    internal abstract void Apply(Simulation sim, Player player, Milestone source);
}

// A one-off gift into the player's castle. Capacity-limited like any deposit:
// what does not fit is lost, not queued.
public sealed record Grant(Resource Resource, int Amount) : Effect
{
    internal override void Apply(Simulation sim, Player player, Milestone source)
    {
        if (Sim.Core.Food.FoodConsumption.FindCastleFor(sim.World, player.Id) is { } castle)
            CargoTransfer.DepositInto(sim, castle, Resource, Amount);
    }
}

// A building the player's people learn to raise. The lock is DERIVED, not
// stored: a kind is locked for an enrolled player while some unfired row
// carries an Unlock for it (Progression.IsLocked), so firing the row is the
// whole of unlocking and Apply has nothing to write. Players who are not
// enrolled (AI seats) are never locked.
public sealed record Unlock(StructureKind Kind) : Effect
{
    internal override void Apply(Simulation sim, Player player, Milestone source) { }
}

// A telegraphed attack: an omen with a countdown, then the force arrives out
// of the fog and marches on the owner's seat. The outcome (Repelled / Lost)
// is counted against the milestone that raised it. Kill every raider and
// their war chest drops where the last one fell.
public sealed record Threat(OmenKind Kind, int Size, long WarningTicks,
                            Resource ChestResource = Resource.None, int ChestAmount = 0) : Effect
{
    internal override void Apply(Simulation sim, Player player, Milestone source) =>
        Omens.RaiseRaid(sim, player, source.Id, Size, WarningTicks, ChestResource, ChestAmount);
}

// Newcomers: an omen with a countdown, then a band of the owner's own people
// appears on the settled side and walks to the seat.
public sealed record Arrival(int Size, long WarningTicks) : Effect
{
    internal override void Apply(Simulation sim, Player player, Milestone source) =>
        Omens.RaiseArrival(sim, player, source.Id, Size, WarningTicks);
}

// M39 — a bandit camp in the owner's wildest direction, rumoured with a search
// circle; its raiders ride after CampConfig.FirstRaidDelay.
public sealed record CampRumour : Effect
{
    internal override void Apply(Simulation sim, Player player, Milestone source) =>
        Omens.RaiseCamp(sim, player, source.Id);
}

// A ruin in the fog, out in the wildest direction, stocked with Loot. The
// owner is told which way to look, never where.
public sealed record Rumour(IReadOnlyList<(Resource Resource, int Amount)> Loot) : Effect
{
    internal override void Apply(Simulation sim, Player player, Milestone source) =>
        Omens.RaiseRumour(sim, player, source.Id, Loot);
}
