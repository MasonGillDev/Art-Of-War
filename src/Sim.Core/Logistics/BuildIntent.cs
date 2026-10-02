using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Logistics;

// M30 — THE COMPOSITE (docs/goal-shaped-intents.md §3.3): "a farm exists here,
// built by him, worked by her" — one gesture, then walk away.
//
// Everything after firing is sim-driven: the builder travels; construction
// starts when materials and builder are both present (whichever lands last);
// on completion the manning sub-goal fires and the designated worker walks
// over and takes the post.
//
// WHY THIS IS STILL ONE DECISION, not a plan. Every parameter is bound up
// front, and nothing new is ever chosen after firing. Staged execution is not
// chaining — "build the farm, then a house, then start breeding" would be, and
// that is the automation tier's business.
//
// THE SIM NEVER PICKS PARTICIPANTS. There is no "use any available builder"
// here; that convenience lives CLIENT-side, where the UI picks (nearest idle)
// and binds a concrete id before the intent reaches the wire. Sugar in the
// client, decisions with the player, execution in the sim — and the replay log
// stays legible, which it would not if the sim had opinions about who works.
//
// MATERIALS ARE NOT PART OF THE COMPOSITE, and this is a deliberate departure
// from the design doc's §3.3, which assumed a demand-driven haulage system
// that does not exist (HaulIntent is one named hauler, one source, one
// destination — see docs/m30-goal-shaped-intents-spec.md, correction 2).
// Building one here would ALSO cross the boundary this milestone exists to
// protect: choosing which hauler and which stockpile feeds a site is judgment,
// and judgment is earned, not free. So materials arrive the way they always
// did, and a site without them stalls visibly rather than silently.
//
// REJECTS only what is malformed: an illegal placement (delegated whole to
// PlaceSiteIntent, so the two can never disagree), or a bound unit that isn't
// the player's. Everything else — a busy builder, a worker who dies before the
// build finishes — is a precondition or a dissolution, announced and visible.
public sealed class BuildIntent : Intent
{
    public TileCoord Tile { get; }
    public StructureKind Kind { get; }

    // The builder who will raise it. Optional: a site with no builder bound is
    // exactly a PlaceSiteIntent, and stalls as "waiting: builder".
    public int? BuilderId { get; }

    // The worker who will MAN the finished structure. Optional, and never
    // touched until completion — they keep doing whatever they are doing
    // rather than idling at the site waiting for a building to exist.
    public int? WorkerToManId { get; }

    public TileCoord? DockSlip { get; }
    public List<TileCoord>? ClaimTiles { get; }

    // Which way the building faces: as PlaceSiteIntent.Facing (-1 = work it out).
    public int Facing { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public BuildIntent(TileCoord tile, StructureKind kind, int? builderId = null,
        int? workerToManId = null, TileCoord? dockSlip = null, List<TileCoord>? claimTiles = null,
        int facing = -1)
    {
        Facing = facing;
        Tile = tile;
        Kind = kind;
        BuilderId = builderId;
        WorkerToManId = workerToManId;
        DockSlip = dockSlip;
        ClaimTiles = claimTiles;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        // Placement validation is DELEGATED, not copied: one implementation of
        // "may this go here", so the composite can never drift from the plain
        // placement it is built on.
        var placement = new PlaceSiteIntent(Tile, Kind, DockSlip, ClaimTiles, Facing) { PlayerId = PlayerId };
        var outcome = placement.Resolve(sim);
        if (outcome.IsRejected) return outcome;

        if (!world.Structures.TryGetValue(Tile, out var s) || s is not ConstructionSite site)
            return IntentOutcome.Reject($"no construction site at {Tile.X},{Tile.Y} after placement");

        // From here the site EXISTS, so nothing below may reject: the goal
        // ("a structure should exist here") has already been accepted, and a
        // late rejection would leave the player's world changed and their
        // intent reported as refused.
        if (WorkerToManId is { } workerId && world.Units.TryGetValue(workerId, out var worker)
            && worker.OwnerId == PlayerId)
            site.WorkerToManId = workerId;

        if (BuilderId is { } builderId)
        {
            if (world.Units.TryGetValue(builderId, out var builder)
                && builder.OwnerId == PlayerId
                && !Sim.Core.Groups.GroupRules.UnderCommand(world, builder)
                && !builder.IsEmbarked
                && builder.Role == UnitRole.Builder
                && builder.Activity == Activity.Idle)
            {
                if (builder.Position == Tile)
                {
                    if (WorkAssignment.TryAssignBuilder(sim, site, builder)
                        && !site.IsActive && site.ConditionsMet(world))
                        site.StartOrResume(sim);
                }
                else
                {
                    GoalRules.Begin(sim, builder, new GoalPlan(GoalKind.AssignBuilder, Tile));
                }
            }
            else
            {
                // The named builder can't take the job. The site stands and
                // says so rather than the whole gesture failing — availability
                // is a precondition, and the player can bind someone else.
                sim.Schedule(sim.Now, new GoalDissolvedEvent(
                    builderId, GoalKind.AssignBuilder, Tile, "builder unavailable"));
            }
        }

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"Build({Kind} @ {Tile.X},{Tile.Y}, builder={BuilderId?.ToString() ?? "-"}, " +
        $"worker={WorkerToManId?.ToString() ?? "-"})";
}
