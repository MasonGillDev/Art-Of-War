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

// GET /view/{playerId} response: the fog-filtered slice of the world.
public sealed class ViewDto
{
    public int PlayerId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WaterLevel { get; set; }
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
}

// M18 — one standing order, definition + cursor. Flat per the file rule.
public sealed class OrderDto
{
    public int Id { get; set; }
    public int Kind { get; set; }            // Sim.Core OrderKind byte
    public int Loop { get; set; }            // Sim.Core LoopMode byte
    public bool Enabled { get; set; }
    public int CurrentStep { get; set; }
    public bool Dispatched { get; set; }     // current step's action submitted
    public int RetryCount { get; set; }
    public long StepEnteredTick { get; set; }
    public int[] ClaimedUnits { get; set; } = [];
    public OrderStepDto[] Steps { get; set; } = [];
}

public sealed class OrderStepDto
{
    public ConditionDto[] Conditions { get; set; } = [];
    // The action atom, flattened (mirrors Sim.Core ActionSpec).
    public int ActionKind { get; set; }
    public int ActionUnit { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int SecondX { get; set; }
    public int SecondY { get; set; }
    public int Resource { get; set; }
    public int Role { get; set; }
}

public sealed class ConditionDto
{
    public int Kind { get; set; }            // Sim.Core ConditionKind byte
    public int UnitId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Resource { get; set; }
    public long Threshold { get; set; }
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
}

public sealed class UnitDto
{
    public int Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Role { get; set; }
    public int OwnerId { get; set; }
    public int Age { get; set; }        // derived age in years
    public int Activity { get; set; }   // Sim.Core Activity enum; -1 = hidden (not the viewer's unit)
    public int PassengerCap { get; set; } // boats: max passengers (0 for non-boats / not own)
    public int Passengers { get; set; }   // boats: current embarked passenger count
    public int CargoResource { get; set; } // carried cargo resource (own units; 0/None if empty/not own)
    public int CargoAmount { get; set; }   // carried cargo amount (own units)
    public int Power { get; set; } = -1;   // effective combat power (own units; -1 = hidden)
    public string[] Buffs { get; set; } = []; // active buff kinds (own units only — loadout is private)
    // Movement destination (own units in transit; -1/-1 = none/hidden).
    // Solo moves read Unit.PathFinalDest; grouped units fall back to their
    // Group's PathFinalDest. Other players' plans are private — same rule
    // as Activity.
    public int DestX { get; set; } = -1;
    public int DestY { get; set; } = -1;
}

public sealed class RoadDto
{
    public int X { get; set; }
    public int Y { get; set; }
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
}
