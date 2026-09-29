namespace Sim.Server.Wire;

// The client <-> server JSON contract. These are flat, JsonUtility-friendly shapes
// (arrays, not sets/dicts) so the Unity client can (de)serialize them with no extra
// deps. Serialized camelCase via ServerJson.Options; the client's Wire.cs mirrors
// these field names. Enums cross as ints matching Sim.Core's append-only byte enums.

// POST /intent body: a durable type-name + the intent's own JSON payload.
public sealed record IntentEnvelopeDto
{
    public string? TypeName { get; init; }
    public string? Payload { get; init; }
}

// POST /intent response. Validation is at resolution time, so "accepted" only means
// the intent was queued — it can still be rejected when its event fires.
public sealed class AckDto
{
    public bool Accepted { get; set; }
    public string Reason { get; set; } = "";
}

// GET /map/elevation response: the FULL per-tile elevation grid (the whole map, NOT
// fog-filtered) for client-side terrain synthesis (hydraulic erosion). Static,
// generation-time data — terrain never changes — so it is fetched once and is safe
// to read without the sim lock. Elevation is a flat row-major array (index =
// y * Width + x) of the raw quantized heights, [0, 1000], matching the per-tile
// Elevation already carried on TileDto. Note: this deliberately exposes terrain
// topology to the client (a design choice — terrain is treated as non-secret; fog
// still hides it visually). Units/structures/economy remain fog-gated as before.
public sealed class ElevationDto
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int WaterLevel { get; set; }    // sea level on the 0..1000 scale
    public int[] Elevation { get; set; } = [];
}

// GET /view/{playerId} response: the fog-filtered slice of the world.
// NOT sealed: ViewV2Dto (WireV2.cs) extends it so the v2 projection reuses every
// dynamic field and every Fill* helper instead of forking a parallel projector.
public class ViewDto
{
    public int PlayerId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WaterLevel { get; set; }
    // The world's current tick (Sim.Now). 1 tick = 1 game-minute (Sim.Core/Time.cs,
    // the canonical clock), so this doubles as "minutes since the world began" — the
    // client derives the running calendar/clock from it. Distinct from the wall-clock
    // pace dial (--tps) and the demographic aging clock, both of which leave it alone.
    public long Tick { get; set; }
    // Two-act pacing (docs/two-act-pacing.md): the tick the landing comes, the end of
    // the prelude and its truce. 0 = a one-act world. On the base view so the AI
    // brains, which read only this, see the same countdown a player does.
    public long LandingTick { get; set; }
    public TileDto[] Visible { get; set; } = [];
    public TileDto[] Remembered { get; set; } = [];
    public UnitDto[] Units { get; set; } = [];
    public StructDto[] Structures { get; set; } = [];
    public RoadDto[] Roads { get; set; } = [];

    // M13 food consumption (the viewing player's realm). Ticks-until values are
    // pre-computed against the sim's current tick; -1 = N/A.
    public int Population { get; set; }
    public int CastleFood { get; set; }        // live (consumption-adjusted) food in the castle
    public int FoodPerPeriod { get; set; }     // food drained per period (= population)
    public int FoodPeriodTicks { get; set; }   // ticks per consumption period
    public long FoodRunwayTicks { get; set; }  // ticks until the castle runs dry (-1 = N/A)
    public bool InFamine { get; set; }
    public long StarvationInTicks { get; set; }// ticks until the next starvation death (-1 = none)
    // Recent resolution-time rejections for this player (a rolling window). Validation
    // happens when an intent's event fires, AFTER the submit ack, so this is how the
    // player learns WHY a fire-and-forget intent did nothing. Each carries a monotonic
    // Id so the client can toast only the ones it hasn't seen.
    public NoticeDto[] Notices { get; set; } = [];

    // M18 — the viewing player's OWN standing orders (other players' orders
    // are never wire-visible; automation is private strategy). Definition +
    // live cursor so the client can render "supply line: step 1/2, waiting".
    public OrderDto[] Orders { get; set; } = [];
    // M36 — the viewer's own haul queue and named routes
    // (docs/hauling-queue-and-routes.md). Owner-only, like Orders.
    public HaulQueueDto HaulQueue { get; set; } = new();
    public HaulRouteDto[] HaulRoutes { get; set; } = [];
    // M37 — the viewer's own omens (docs/progression.md): threats and arrivals
    // counting down or under way, and the outcome of any that ended in the
    // last OmenDto.RecentTicks. Owner-only. The milestones behind them and the
    // progress counters never go on the wire: they are surprises.
    public OmenDto[] Omens { get; set; } = [];
    // M37 — structure kinds (StructureKind bytes) the viewer's people do not yet
    // know how to build; a milestone will teach them. Owner-only. Says WHAT is
    // locked, never what unlocks it (milestones are surprises). Empty for a
    // player not enrolled in progression.
    public int[] LockedKinds { get; set; } = [];
    // M38 — the viewer's own chart: what their returned scouts reported
    // (docs/scouting-secrets.md). Owner-only. A HINT, never the secret's kind
    // or contents. The secret itself is only a structure while in live sight.
    public ChartEntryDto[] Chart { get; set; } = [];
    // M38 — the viewer's own live idol circles of sight. Owner-only.
    public VisionGrantDto[] VisionGrants { get; set; } = [];

    // M20 — scout reports that have come in for this player (own-only; a
    // rolling window). Each carries the narrated prose (or raw-claims fallback)
    // plus the structured claims for sketch-map pins. Id is monotonic so the
    // client toasts each once; Prose updates in place when the async narration
    // lands a moment after the raw report appears.
    public ScoutReportDto[] ScoutReports { get; set; } = [];

    // M25 — diplomatic state (docs/diplomacy-model.md). Factions /
    // Relationships / PendingWars are PUBLIC knowledge, identical for every
    // viewer; IncomingProposals is the one per-viewer slice (offers stay
    // private to the proposer/target pair). This is the war telegraph on
    // the wire: a pending war is visible to its target for
    // DiplomacyConfig.Delay ticks before it bites — the human client and
    // the AI brains read the same channel (the fairness contract).
    public FactionDto[] Factions { get; set; } = [];
    public RelationshipDto[] Relationships { get; set; } = [];
    public PendingWarDto[] PendingWars { get; set; } = [];
    public ProposalDto[] IncomingProposals { get; set; } = [];

    // Grave markers — combat/starvation deaths the viewer has WITNESSED (a
    // grave enters the view the first poll its tile is visible and stays while
    // the grave lives). Host-level presentation state, same tier as Notices —
    // see GameHost.HarvestDeaths.
    public GraveDto[] Graves { get; set; } = [];

    // Ground piles — loose resources on tiles in CURRENT sight (a death's
    // drop under a grave marker, a spilled haul). Contents are public while
    // visible — the M23 cache stance: naming a resource to LoadCargoIntent
    // requires seeing what's there, and the AI brains read the same rows
    // (that's how battlefield salvage knows what to pick up). No remembered
    // reveal: look away and the pile leaves the view — it may be looted at
    // any time, so a stale amount would be a lie.
    public PileDto[] Piles { get; set; } = [];
}

// One grave: where a unit fell to combat or starvation (never age or a bandit
// despawn) AND left loot — the marker exists exactly as long as its tile's
// ground pile does (empty-handed victims mint none; looting the pile retires
// it). Id is monotonic; the client reconciles by Id rather than appending.
public sealed class GraveDto
{
    public long Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public long Tick { get; set; }
}

// One visible ground pile (world.GroundResources projected for tiles in
// current sight). Piles ordered (Y, X); Holdings ordered by resource id;
// zero rows are dropped — an empty pile never reaches the wire.
public sealed class PileDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public ResAmtDto[] Holdings { get; set; } = [];
}

// M25 — one known faction. Negative sentinel owners (bandits -1, caches -2,
// rubble -3) are not diplomatic actors and never appear — same discipline as
// PlayerDefeatedEvent's live-player count. Defeated is public: a fallen
// castle is world news.
public sealed class FactionDto
{
    public int Id { get; set; }
    public bool Defeated { get; set; }
}

// M25 — one relationship row (sparse: an absent pair reads Neutral, the
// default). State crosses as the Sim.Core RelationshipState byte.
// PendingEffectiveTick: -1 = none; otherwise the tick a declared war takes
// effect (the telegraph window's far edge).
public sealed class RelationshipDto
{
    public int LoId { get; set; }
    public int HiId { get; set; }
    public int State { get; set; }
    public long PendingEffectiveTick { get; set; } = -1;
}

// M25 — a declared-but-not-yet-effective war. Redundant with the
// Relationships row's PendingEffectiveTick by design (same as PlayerView):
// clients alert off this list without scanning every relationship.
public sealed class PendingWarDto
{
    public int LoId { get; set; }
    public int HiId { get; set; }
    public long EffectiveTick { get; set; }
}

// M25 — a diplomatic offer addressed to the viewing player. DesiredState
// crosses as the RelationshipState byte; respond via RespondToProposalIntent
// before ExpiryTick (lazy expiry — a stale response simply rejects).
public sealed class ProposalDto
{
    public int Id { get; set; }
    public int ProposerId { get; set; }
    public int TargetId { get; set; }
    public int DesiredState { get; set; }
    public long ExpiryTick { get; set; }
}

// M20 — one returned scout's report: the narrated prose to read plus the
// canonical claims the prose is a view of (the sketch-map / fallback source).
public sealed class ScoutReportDto
{
    public long Id { get; set; }
    public int ScoutUnitId { get; set; }
    public string ScoutName { get; set; } = "";
    public long DispatchTick { get; set; }
    public long ReturnTick { get; set; }
    public string Prose { get; set; } = "";
    public int Status { get; set; }   // Sim.Server.Scouting.ReportStatus byte (0=Narrated, 1=RawFallback)
    public ScoutClaimDto[] Claims { get; set; } = [];
}

// One claim: a canonical sentence plus its map pin and epistemic tags, so the
// client can render the sketch map and let the player weigh saw-vs-guess.
public sealed class ScoutClaimDto
{
    public int Sequence { get; set; }
    public int Kind { get; set; }       // Sim.Server.Scouting.ClaimKind byte
    public int Certainty { get; set; }  // Sim.Server.Scouting.ClaimCertainty byte
    public string Text { get; set; } = "";
    public bool HasAnchor { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public bool Novel { get; set; }
}

// One automation order (docs/automation-substrate.md), definition + live
// status. Flat per the file rule; enums cross as ints.
public sealed class OrderDto
{
    public int Id { get; set; }
    public int Priority { get; set; }
    public int Program { get; set; }         // ProgramKind byte
    public int Recipe { get; set; }          // RecipeKind byte
    public int SubjectKind { get; set; }
    public int SubjectX { get; set; }
    public int SubjectY { get; set; }
    public int SubjectRole { get; set; }
    public int SubjectGroupId { get; set; }
    public long Target { get; set; }
    public int SourceX { get; set; }
    public int SourceY { get; set; }
    public int Resource { get; set; }
    public int CrewMode { get; set; }
    public int[] NamedCrew { get; set; } = [];
    // Units the order currently holds from the labour pool — what the UI
    // needs to draw "who is working on this right now".
    public int[] HeldUnits { get; set; } = [];
    public SelectorDto Selector { get; set; } = new();
    public TriggerClauseDto[] Trigger { get; set; } = [];
    public RoutineStepDto[] Steps { get; set; } = [];
    // M29 patrol posture (Routine only). 0/0 = a pacifist circuit.
    public int EngageRadius { get; set; }
    public int LeashRadius { get; set; }
    // ---- live status ----
    public bool Enabled { get; set; }
    public int RetryCount { get; set; }
    public long LastFiredTick { get; set; }
    public int CurrentStep { get; set; }     // Routine cursor
    // What the driver did with this order on its last think — the ONLY way
    // the client can tell a happily-resting order (trigger not met) from one
    // starving for hands (trigger met, pool dry). Both look identical from
    // HeldUnits + LastFiredTick alone, and starving is the state that needs
    // a player decision. Sourced from the server-side OrderJournal, which is
    // presentation-only and never hashed: dropping it changes no world byte.
    // 0 = never thought about yet (installed this tick, or driver disabled).
    public int LastOutcome { get; set; }     // JournalOutcome byte
    public long LastOutcomeTick { get; set; }
    public string LastDetail { get; set; } = "";
}

// M38 — one chart marker. Hint: 1 Glint (a cache or ruin), 2 StoneFigure (an
// idol). State: 1 Known (last seen SeenTick), 2 Gone (struck at GoneTick).
public sealed class ChartEntryDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Hint { get; set; }
    public int State { get; set; }
    public long SeenTick { get; set; }
    public long GoneTick { get; set; }
}

// M38 — an activated idol's circle of sight, while it lasts.
public sealed class VisionGrantDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Radius { get; set; }
    public long TicksLeft { get; set; }
}

// M37 — one omen, as its owner sees it. Enum values are the sim's bytes:
//   Kind   1 Raid, 2 Refugees (newcomers), 3 Rumour (a ruin in the fog),
//          4 Camp (a bandit camp; TicksLeft counts to its first raid)
//   State  1 Pending, 2 Arrived (raid under way / ruin standing),
//          3 Repelled, 4 Lost, 5 Fulfilled (arrived / ruin emptied), 6 Fizzled
//   From   0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW (north = +y)
public sealed class OmenDto
{
    // How long an ended omen stays on the wire, so the client can say how it went.
    public const long RecentTicks = 2 * Sim.Core.Time.Day;

    public int Id { get; set; }
    public int Kind { get; set; }
    public int State { get; set; }
    public int From { get; set; }
    // Raiders or newcomers; 0 for a rumour.
    public int Size { get; set; }
    // Pending: when it comes, and how long until then.
    public long DueTick { get; set; }
    public long TicksLeft { get; set; }
    // Where it is headed (the seat). -1 for a rumour: the owner is told which
    // way to look, never where.
    public int TargetX { get; set; } = -1;
    public int TargetY { get; set; } = -1;
    // Rumour: the search area, a circle that holds the ruin somewhere inside
    // (never at its centre). Radius 0 for other kinds.
    public int AreaX { get; set; }
    public int AreaY { get; set; }
    public int AreaRadius { get; set; }
    // Raid under way: raiders still standing.
    public int Remaining { get; set; }
    // When it ended; 0 while live.
    public long ResolvedTick { get; set; }
}

// M36 — the haul queue. Jobs are in QUEUE order (front first). The live
// fields come from the driver's last think (presentation only, never hashed);
// State 0 = added since that think.
public sealed class HaulQueueDto
{
    public int FreeHaulers { get; set; }
    // Jobs that want a trip and have stock, but no hauler is free.
    public int Waiting { get; set; }
    // How long the longest-waiting of those has been in line, in ticks.
    public long LongestWaitTicks { get; set; }
    public HaulJobDto[] Jobs { get; set; } = [];
}

public sealed class HaulJobDto
{
    public int Id { get; set; }
    public int SourceX { get; set; }
    public int SourceY { get; set; }
    public int DestX { get; set; }
    public int DestY { get; set; }
    public int Resource { get; set; }
    public int Kind { get; set; }            // HaulJobKind byte: 1 Standing, 2 Once
    public int Target { get; set; }
    public int Delivered { get; set; }       // Once only
    public long WaitTicks { get; set; }      // since it last joined the back of the line
    // ---- live, from the driver ----
    public int State { get; set; }           // HaulJobState
    public int Need { get; set; }
    public int OnTheWay { get; set; }
    public int Haulers { get; set; }
}

public sealed class HaulRouteDto
{
    public int Id { get; set; }
    public HaulStopDto[] Stops { get; set; } = [];
    public HaulCrewDto[] Crews { get; set; } = [];
}

public sealed class HaulStopDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public HaulStopRuleDto[] Rules { get; set; } = [];
}

public sealed class HaulStopRuleDto
{
    public int Resource { get; set; }
    public int Op { get; set; }              // StopRuleOp byte: 1 Pickup, 2 Drop
    public int Percent { get; set; }
}

public sealed class HaulCrewDto
{
    public int Id { get; set; }
    public int[] Members { get; set; } = [];
    public int CurrentStop { get; set; }
    public int Living { get; set; }
    public int State { get; set; }           // RouteCrewState, live from the driver
}

public sealed class SelectorDto
{
    public int Role { get; set; }
    public bool AnyRole { get; set; }
    public int MinAgeYears { get; set; }
    public int MaxAgeYears { get; set; }
    public bool RequireDormant { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int Radius { get; set; }
}

// The trigger is disjunctive normal form: ANY clause whose predicates ALL
// hold fires the order. An empty Trigger array means always.
public sealed class TriggerClauseDto
{
    public PredicateDto[] All { get; set; } = [];
}

public sealed class PredicateDto
{
    public int Kind { get; set; }            // PredicateKind byte
    public int X { get; set; }
    public int Y { get; set; }
    public int Resource { get; set; }
    public int Role { get; set; }
    public long Threshold { get; set; }
    public bool CountInFlight { get; set; }
}

public sealed class RoutineStepDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Action { get; set; }          // RoutineAction byte
    public int Resource { get; set; }
    public PredicateDto[] DepartWhen { get; set; } = [];
}

public sealed class NoticeDto
{
    public long Id { get; set; }
    public long Tick { get; set; }
    public string Text { get; set; } = "";
}

public sealed class TileDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Biome { get; set; }
    public int Elevation { get; set; }
    // M35 — the tile's environmental fertility baseline (docs/environmental-
    // fertility.md): band baseline + water/forest-depth offset. What the AI
    // brains site farms and camps by. 0 on the graves-only visibility list.
    public int Baseline { get; set; }
}

// M36 — one row of a unit's mixed cargo (docs/hauling-queue-and-routes.md).
public sealed class CargoItemDto
{
    public int Resource { get; set; }
    public int Amount { get; set; }
}

public sealed class UnitDto
{
    public int Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Role { get; set; }
    public int OwnerId { get; set; }
    public int Age { get; set; }        // derived age in years
    // Sim.Core has projected UnitView.Health since M7; v1 never carried it. Additive
    // (v1 readers ignore the extra field), and the v2 client needs it for wounded-unit
    // presentation. Own units only — enemy health is private, same rule as Power.
    public int Health { get; set; } = -1;
    public int Activity { get; set; }   // Sim.Core Activity enum; -1 = hidden (not the viewer's unit)
    public int PassengerCap { get; set; } // boats: max passengers (0 for non-boats / not own)
    public int Passengers { get; set; }   // boats: current embarked passenger count
    // M36 — cargo can be MIXED. CargoAmount is the total aboard and
    // CargoResource the resource with the most aboard (exact for every
    // single-resource carrier); Cargo lists every row. Own units only.
    public int CargoResource { get; set; } // dominant carried resource (own units; 0/None if empty/not own)
    public int CargoAmount { get; set; }   // total carried, all resources (own units)
    public CargoItemDto[] Cargo { get; set; } = [];
    public int Power { get; set; } = -1;   // effective combat power (own units; -1 = hidden)
    // Housing (docs/housing-buffs.md): homed at a fed own House, so the
    // settled work/power bonus applies. Own units only, like Power.
    public bool Settled { get; set; }
    public string[] Buffs { get; set; } = []; // active buff kinds (own units only — loadout is private)
    // Movement destination (own units in transit; -1/-1 = none/hidden).
    // Solo moves read Unit.PathFinalDest; grouped units fall back to their
    // Group's PathFinalDest. Other players' plans are private — same rule
    // as Activity.
    public int DestX { get; set; } = -1;
    public int DestY { get; set; } = -1;
    // C2 — group membership. The last of the C1 surfacing debt: without this the
    // client cannot select an army AS an army, only as N loose units. Own units
    // only (-1 = ungrouped, or not yours) — an enemy stack's command structure is
    // private, same rule as Activity and Power. The member list is not sent: it is
    // exactly the set of units carrying this id, so the client derives it.
    public int GroupId { get; set; } = -1;

    // The group's STATE, because "which group" is not enough to command one.
    //
    // A freshly formed group is Forming — its members are still walking to the
    // rendezvous — and MoveGroupIntent refuses it outright ("group 1 is still
    // forming"). Without this on the wire the player forms an army, orders it to
    // march, and is refused for a reason they had no way to see and no way to
    // predict the end of. Same defect the M30 goal fields fixed for errands.
    //
    // Own units only, like GroupId: an enemy's order of battle stays private.
    // 0 = not in a group; otherwise Sim.Core GroupState (1 forming, 2 idle, 3 moving).
    public int GroupState { get; set; }

    // M30 — THE VISIBILITY CONTRACT (docs/goal-shaped-intents.md). Goal-shaped
    // intents move work off the player's memory and into the sim; if the sim
    // then says nothing about that work, appointment-anxiety is simply traded
    // for silent-failure anxiety. So a unit under a goal always reports it.
    //
    // GoalKind: 0 = none, otherwise Sim.Core.World.GoalKind.
    // GoalState: "" when idle, else "en route" or "waiting: <what for>", which
    // is exactly the distinction the client needs to draw a STALLED goal
    // differently from one that is progressing.
    // Own units only, like Activity — an enemy's plans are not public.
    // M31 — the dynasty, OWN UNITS ONLY (docs/king-and-dynasty.md).
    //
    // Royal = 1 king, 2 heir-apparent, 0 otherwise. Both are DERIVED
    // server-side and sent as a tag rather than as ids, because the client
    // needs to draw a crown, not to recompute a line.
    //
    // Own-only for now, which is the conservative end of the design's open
    // question #6 (does the heir marker leak through fog, or is it
    // scout-discoverable intel?). Sending it for enemy units would silently
    // answer that question in the most generous direction — an enemy dynasty
    // readable off the map with no scouting at all — so the wire stays quiet
    // until the question is settled.
    public int Royal { get; set; }

    public int GoalKind { get; set; }
    public string GoalState { get; set; } = "";
    public int GoalX { get; set; } = -1;
    public int GoalY { get; set; } = -1;

    // P3 — the PURSUIT anchor (World/Pursuit.cs, docs/patrols.md): who this unit
    // is chasing and the leash it will be called off at. OWN UNITS ONLY — a chase
    // is an order and orders are private; an enemy's chase is inferred from its
    // movement, never disclosed. -1 everywhere when there is no pursuit or the
    // unit is not yours. PursuitLeashRadius 0 is a real value: "no leash".
    public int PursuitTargetId { get; set; } = -1;
    public int PursuitLeashX { get; set; } = -1;
    public int PursuitLeashY { get; set; } = -1;
    public int PursuitLeashRadius { get; set; } = -1;

    // C2 — THE CURRENT HOP, so the client can draw motion instead of teleportation.
    //
    // Units move tile to tile on scheduled arrivals, so a client that draws them at
    // their tile centre relocates them 100 world units four times a second and
    // nothing ever reads as marching. With the destination tile, the tick the hop
    // lands on, and how long the hop takes, the client can place a unit exactly where
    // it is between tiles — derived from sim facts, not smoothed into existence.
    //
    // PUBLIC FOR EVERY VISIBLE UNIT, unlike DestX/DestY above. The distinction is
    // between a plan and a physical fact: where an army is ultimately HEADED is
    // private intelligence, but which way it is stepping right now is something you
    // can see by looking at it. Emitting the hop reveals no plan — one tile of a
    // march is not a destination.
    //
    // -1 on all four when the unit is standing still.
    public int HopToX { get; set; } = -1;
    public int HopToY { get; set; } = -1;
    public long HopArriveTick { get; set; } = -1;
    public int HopTotalTicks { get; set; } = -1;
}

// A road ARC (docs/roads-on-edges.md): the lane between the owner tile
// (X, Y) and its east (Axis 0) or south (Axis 1) neighbour.
public sealed class RoadDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Axis { get; set; }
    public int Condition { get; set; }
}

public sealed class ResAmtDto
{
    public int Resource { get; set; }
    public int Amount { get; set; }
}

// Structure projection. Pos/kind/owner are always present (fog already gated which
// structures the player sees). The richer fields are filled ONLY for the viewer's OWN
// structures — holdings and build status are private activity (the design hides
// activity even on a visible enemy structure). All read via Sim.Core public APIs.
public sealed class StructDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Kind { get; set; }
    public int OwnerId { get; set; }
    // Structure footprints (docs/structure-footprints.md): which way it faces
    // (Heading: 0 N, 1 E, 2 S, 3 W; a dock: its slip's side), and its resolved
    // 4×4 layout when that isn't plain ground (walls and canals joined to the
    // neighbours this viewer can see). Public, like the building itself.
    public int Facing { get; set; }
    public bool HasFootprint { get; set; }
    public FootprintDto Footprint { get; set; } = new();
    public ResAmtDto[] Holdings { get; set; } = [];  // storage holdings / extractor buffer / site materials delivered
    public int Capacity { get; set; }                 // storage capacity / extractor buffer cap (0 if N/A)
    public int Workers { get; set; }                  // extractor: workers assigned
    public int WorkerCap { get; set; }                // extractor: worker cap
    public ResAmtDto[] Needed { get; set; } = [];     // construction site: build cost
    public int TargetKind { get; set; }               // construction site: what it becomes
    public int BuildersRequired { get; set; }
    public int BuildersPresent { get; set; }
    public bool Building { get; set; }                 // construction site: actively building
    public int BuildProgress { get; set; } = -1;       // construction site: % complete (0..100); -1 = N/A
    public long BuildEtaTicks { get; set; } = -1;      // construction site: ticks to completion while building; -1 = N/A
    // M15 — claimed working tiles (parallel arrays; JsonUtility-friendly).
    // UNLIKE the own-only enrichment above, claims are emitted for ANY
    // visible structure (extractor or pending site): they're physical land
    // use, revealed by scouting — and the reject toast needs no secret.
    public int[] ClaimX { get; set; } = [];
    public int[] ClaimY { get; set; } = [];
    // M19 — own HOUSES only (food homes): live SIGNED local food (negative
    // during a local famine by exactly the unpaid debt — the CastleFood
    // contract, per house), resident headcount, and the famine flag.
    // Zero/false on everything that isn't your house.
    public int LocalFood { get; set; }
    public int Residents { get; set; }
    public bool LocalFamine { get; set; }
    // Soil visibility — live fertility per claimed tile, parallel to
    // ClaimX/ClaimY. OWN extractors only (unlike the claim coords, soil
    // readings are private — you walk your own fields). This is what
    // makes crop rotation a PLAYABLE strategy instead of a hidden
    // cliff: the permanent desert latch sits at the catalog's
    // DesertThreshold, and now you can see a field approaching it.
    public int[] ClaimFertility { get; set; } = [];
    // M35 — each claim tile's environmental BASELINE, parallel to
    // ClaimFertility and own-only like it. "How worn is this field" is
    // (ClaimBaseline - ClaimFertility); a riverside field starts above the
    // flat band baseline, a dry-edge one below (docs/environmental-fertility.md).
    public int[] ClaimBaseline { get; set; } = [];
    // Kingdom analytics (docs/structure-rates-on-the-wire.md) — both in the
    // SAME unit, per game day, so a farm's output reads directly against a
    // house's appetite. OWN structures only.
    //   OutputPerDay: own extractors/refiners — what the current workers on
    //     the current soil make per day (ProductionRate, the tick's formula).
    //     The rate at pace, not a promise: Producing says whether it is
    //     running (false = no workers, buffer full, soil spent, no inputs).
    //   EatsPerDay: own food homes (House, Castle) — food the residents eat
    //     per day. The castle counts its own mouths (everyone not housed).
    public int OutputPerDay { get; set; }
    public bool Producing { get; set; }
    public int EatsPerDay { get; set; }
    // P2 — structure health. OWN structures always; ANY visible fortification
    // (Wall/Gate/Tower/Castle) too, because a besieger has to see what they
    // are breaching (docs/siege-visibility.md: exact, not banded). Every other
    // enemy kind stays private at -1/-1. Kinds whose catalog BaseHealth is 0
    // (Cache/Canal/Rubble) are indestructible and send 0/0 — there is nothing
    // to hide about a thing that cannot be hurt.
    public int Health { get; set; } = -1;
    public int MaxHealth { get; set; } = -1;
}
