using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Automation;

// Install one automation order (docs/automation-substrate.md, Layer 1).
// The ONE player-facing entry point for every automation in the game — the
// spatial view, the panel, and the script surface all compile to this
// intent, which is what makes them equal in power and different only in
// authoring speed (surface parity, docs/automation-as-core-game.md).
//
// Validation is resolution-time against the live world, per
// docs/intent-validation.md. Everything here is a STRUCTURAL check (does
// the subject exist, is the crew ownable, is the recipe wired coherently) —
// never a viability check. "Will this order ever fire?" is the player's
// problem: an order pointed at an empty farm is legal and simply waits,
// exactly like a supply line whose source has run dry.
public sealed class SetOrderIntent : Intent
{
    public Order Definition { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetOrderIntent(Order definition) { Definition = definition; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        var d = Definition;

        // ---- cap ----
        var owned = 0;
        foreach (var (_, existing) in world.Orders)
            if (existing.OwnerId == PlayerId) owned++;
        if (owned >= AutomationConstants.MaxOrdersPerPlayer)
            return IntentOutcome.Reject(
                $"player {PlayerId} already has {owned} orders " +
                $"(cap {AutomationConstants.MaxOrdersPerPlayer})");

        // ---- subject ----
        Structure? subjectStructure = null;
        switch (d.SubjectKind)
        {
            case SubjectKind.Structure:
                if (!world.Structures.TryGetValue(d.SubjectTile, out subjectStructure))
                    return IntentOutcome.Reject(
                        $"no structure at subject {d.SubjectTile.X},{d.SubjectTile.Y}");
                if (subjectStructure.OwnerId != PlayerId)
                    return IntentOutcome.Reject(
                        $"subject structure at {d.SubjectTile.X},{d.SubjectTile.Y} not owned by player {PlayerId}");
                break;

            case SubjectKind.Group:
                if (!world.Groups.TryGetValue(d.SubjectGroupId, out var group))
                    return IntentOutcome.Reject($"group {d.SubjectGroupId} does not exist");
                if (group.OwnerId != PlayerId)
                    return IntentOutcome.Reject($"group {d.SubjectGroupId} not owned by player {PlayerId}");
                break;

            case SubjectKind.RoleCount:
                if (d.SubjectRole == UnitRole.None)
                    return IntentOutcome.Reject("RoleCount subject needs a role");
                break;

            default:
                return IntentOutcome.Reject($"unknown subject kind {(byte)d.SubjectKind}");
        }

        // ---- program + recipe ----
        if (d.Program == ProgramKind.Routine)
        {
            if (d.Steps.Count == 0)
                return IntentOutcome.Reject("a Routine needs at least one stop");
            if (d.Steps.Count > AutomationConstants.MaxStepsPerOrder)
                return IntentOutcome.Reject(
                    $"circuit of {d.Steps.Count} stops exceeds cap {AutomationConstants.MaxStepsPerOrder}");
            foreach (var step in d.Steps)
            {
                if (!world.Grid.InBounds(step.Tile))
                    return IntentOutcome.Reject($"stop {step.Tile.X},{step.Tile.Y} out of bounds");
                if (step.Action == RoutineAction.Load && step.Resource == Resource.None)
                    return IntentOutcome.Reject("a Load stop needs a resource");
                foreach (var p in step.DepartWhen)
                    if (p.Kind is < PredicateKind.Always or > PredicateKind.WorkersAtLeast)
                        return IntentOutcome.Reject($"unknown predicate kind {(byte)p.Kind}");
            }
            // A circuit is walked by a crew, and the ROUTE is the identity —
            // rotating a different body through it every leg would make the
            // caravan (and its escort) meaningless.
            if (d.CrewMode != CrewMode.Named)
                return IntentOutcome.Reject("a Routine requires a Named crew");
            if (d.EngageRadius < 0)
                return IntentOutcome.Reject("engage radius must be >= 0");
            if (d.LeashRadius < 0)
                return IntentOutcome.Reject("leash radius must be >= 0");
            return ValidateCrewAndInstall(sim, d);
        }

        if (d.Program != ProgramKind.Maintain)
            return IntentOutcome.Reject($"unknown program kind {(byte)d.Program}");

        // Posture belongs to a circuit. A Maintain recipe has no route to
        // patrol and no party to measure from, so an engage radius on one
        // would be silently inert — reject it rather than accept an order
        // that cannot do what it says (docs/patrols.md).
        if (d.EngageRadius != 0 || d.LeashRadius != 0)
            return IntentOutcome.Reject("engage/leash radii apply to a Routine patrol, not a Maintain order");

        switch (d.Recipe)
        {
            case RecipeKind.Haul:
                if (d.Resource == Resource.None)
                    return IntentOutcome.Reject("Haul recipe needs a resource");
                if (!world.Grid.InBounds(d.SourceTile))
                    return IntentOutcome.Reject(
                        $"source {d.SourceTile.X},{d.SourceTile.Y} out of bounds");
                if (d.SubjectKind != SubjectKind.Structure)
                    return IntentOutcome.Reject("Haul recipe needs a Structure subject");
                if (d.SourceTile == d.SubjectTile)
                    return IntentOutcome.Reject("Haul source and destination are the same tile");
                break;

            case RecipeKind.Train:
                if (d.SubjectKind != SubjectKind.RoleCount)
                    return IntentOutcome.Reject("Train recipe needs a RoleCount subject");
                if (d.SubjectRole is UnitRole.None or UnitRole.Boat)
                    return IntentOutcome.Reject($"role {d.SubjectRole} is not trainable from a citizen");
                if (!world.Structures.TryGetValue(d.SourceTile, out var trainer))
                    return IntentOutcome.Reject(
                        $"no trainer structure at {d.SourceTile.X},{d.SourceTile.Y}");
                if (trainer.OwnerId != PlayerId)
                    return IntentOutcome.Reject(
                        $"trainer at {d.SourceTile.X},{d.SourceTile.Y} not owned by player {PlayerId}");
                // The catalog decides which building trains which role
                // (School for civilians, Barracks for military) — the
                // automation must not invent a second routing table.
                var wanted = Sim.Core.Population.RoleTrainerCatalog.TrainerFor(d.SubjectRole);
                if (trainer.Kind != wanted)
                    return IntentOutcome.Reject(
                        $"{d.SubjectRole} trains at a {wanted}, not a {trainer.Kind}");
                if (d.CrewMode != CrewMode.Pull)
                    return IntentOutcome.Reject("Train recipe requires a Pull crew");
                break;

            case RecipeKind.Staff:
                if (d.SubjectKind != SubjectKind.Structure)
                    return IntentOutcome.Reject("Staff recipe needs a Structure subject");
                if (subjectStructure is not Extractor)
                    return IntentOutcome.Reject(
                        $"Staff recipe needs an Extractor subject (got {subjectStructure?.Kind})");
                if (d.CrewMode != CrewMode.Pull)
                    return IntentOutcome.Reject("Staff recipe requires a Pull crew");
                break;

            case RecipeKind.Breed:
                if (d.SubjectKind != SubjectKind.Structure)
                    return IntentOutcome.Reject("Breed recipe needs a Structure subject");
                if (subjectStructure is not House)
                    return IntentOutcome.Reject(
                        $"Breed recipe needs a House subject (got {subjectStructure?.Kind})");
                // Breeding always draws from the pool: a standing "breeding
                // pair" would lock two adults out of the workforce for life.
                if (d.CrewMode != CrewMode.Pull)
                    return IntentOutcome.Reject("Breed recipe requires a Pull crew");
                break;

            default:
                return IntentOutcome.Reject($"unknown recipe kind {(byte)d.Recipe}");
        }

        if (d.Target < 0)
            return IntentOutcome.Reject("target must be >= 0");

        // ---- trigger ----
        foreach (var clause in d.Trigger.Any)
        {
            if (clause.All.Count == 0)
                return IntentOutcome.Reject("trigger clause has no predicates");
            foreach (var p in clause.All)
            {
                // Keep this bound at the LAST enum value — a new predicate
                // added without widening it here is rejected at Set time,
                // which reads as "my order silently does nothing".
                if (p.Kind is < PredicateKind.Always or > PredicateKind.WorkersAtLeast)
                    return IntentOutcome.Reject($"unknown predicate kind {(byte)p.Kind}");
                if (p.Kind is PredicateKind.StockBelow or PredicateKind.StockAtLeast
                    && !world.Grid.InBounds(p.Tile))
                    return IntentOutcome.Reject(
                        $"predicate tile {p.Tile.X},{p.Tile.Y} out of bounds");
            }
        }

        return ValidateCrewAndInstall(sim, d);
    }

    // Crew validation + install — shared by both programs so a Routine and a
    // Maintain get identical claim semantics and neither can drift.
    private IntentOutcome ValidateCrewAndInstall(Simulation sim, Order d)
    {
        var world = sim.World;

        // ---- crew ----
        if (d.CrewMode == CrewMode.Named)
        {
            if (d.NamedCrew.Count == 0)
                return IntentOutcome.Reject("Named crew mode needs at least one unit");
            if (d.NamedCrew.Count > AutomationConstants.MaxClaimedUnitsPerOrder)
                return IntentOutcome.Reject(
                    $"crew of {d.NamedCrew.Count} exceeds cap {AutomationConstants.MaxClaimedUnitsPerOrder}");
            var seen = new HashSet<int>();
            foreach (var unitId in d.NamedCrew)
            {
                if (!seen.Add(unitId))
                    return IntentOutcome.Reject($"unit {unitId} listed twice in the crew");
                if (!world.Units.TryGetValue(unitId, out var u))
                    return IntentOutcome.Reject($"crew unit {unitId} does not exist");
                if (u.OwnerId != PlayerId)
                    return IntentOutcome.Reject($"crew unit {unitId} not owned by player {PlayerId}");
                // CLAIM EXCLUSIVITY at install time: a unit may serve one
                // order. The ledger enforces this per-claim too, but failing
                // here gives the player the error at authoring time rather
                // than as a silent no-op later.
                if (ClaimLedger.ClaimOf(world, unitId) is { } held)
                    return IntentOutcome.Reject(
                        $"crew unit {unitId} is already claimed by order {held.OrderId}");
            }
        }
        else if (d.CrewMode == CrewMode.Pull)
        {
            if (d.Selector.Radius < 0)
                return IntentOutcome.Reject("selector radius must be >= 0");
            if (!world.Grid.InBounds(d.Selector.Anchor))
                return IntentOutcome.Reject(
                    $"selector anchor {d.Selector.Anchor.X},{d.Selector.Anchor.Y} out of bounds");
        }
        else
        {
            return IntentOutcome.Reject($"unknown crew mode {(byte)d.CrewMode}");
        }

        // ---- install ----
        var orderId = world.NextOrderId++;
        // The SUBMITTER owns the order — a spoofed OwnerId in the payload is inert.
        var order = d.CloneDefinition(orderId, PlayerId);
        foreach (var unitId in d.NamedCrew) order.NamedCrew.Add(unitId);
        order.NamedCrew.Sort();     // canonical iteration
        order.LastFiredTick = long.MinValue;
        world.Orders.Add(orderId, order);

        // Named crews are claimed AT INSTALL (they are standing membership,
        // not a per-firing pull) so the units leave the dormant pool
        // immediately and no other order can grab them.
        foreach (var unitId in order.NamedCrew)
            world.Claims[unitId] = new Claim(unitId, orderId, ClaimPurpose.Crew);

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"SetOrder({Definition.Program}/{Definition.Recipe} on {Definition.SubjectKind}" +
        $" @ {Definition.SubjectTile.X},{Definition.SubjectTile.Y})";
}
