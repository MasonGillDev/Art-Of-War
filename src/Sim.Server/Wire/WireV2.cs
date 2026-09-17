namespace Sim.Server.Wire;

// ── The v2 wire: the contract the PRODUCTION client speaks. ────────────────────
//
// v1 (WireDtos.cs) re-sends every known tile's {x, y, biome, elevation} on every
// poll. Measured on a live 252x252 game: a fully-explored view is 2.81 MB, of
// which 2.70 MB (96%) is those tile arrays — and x/y/elevation NEVER change while
// biome changes only on a rare degradation latch. At 4 tps that is megabytes per
// second of constant data.
//
// v2 splits the wire along the only line that matters:
//   GET /v2/world        — static, fetched ONCE on connect (the "genesis" payload)
//   GET /v2/view/{pid}   — per-tick, carries only what a tick can change
//
// v1 is untouched and still serves the debug client. It dies when that client does.

// GET /v2/world — everything about this world that a tick cannot change. Fetched
// once at session start and never again. Terrain topology is deliberately public
// (same stance as v1's /map/elevation): fog hides it VISUALLY, the client simply
// declines to raise geometry for tiles it hasn't explored.
public sealed class WorldDto
{
    public int WireVersion { get; set; } = 2;
    public int Width { get; set; }
    public int Height { get; set; }
    public int WaterLevel { get; set; }        // sea level on the 0..1000 scale
    public int MapSeed { get; set; }           // reproduces this map; also a session sanity check
    public int TicksPerDay { get; set; }       // Sim.Core Time.Day — so the client's clock isn't hard-coded

    // Row-major (index = y * Width + x), full map, fog-free.
    public int[] Elevation { get; set; } = []; // raw quantized heights, [0, 1000]
    public int[] Biome { get; set; } = [];     // GENESIS biome per tile; live drift arrives as view overrides
    // Rivers: a RiverEdge bit mask per tile (1 N, 2 E, 4 S, 8 W) saying which of the
    // tile's edges a river runs along. Terrain, so it is public and static like
    // Elevation; the client draws it as a ribbon along tile boundaries. docs/rivers.md.
    public int[] River { get; set; } = [];

    // C2 — what the player may build, straight off StructureCatalog.
    //
    // ON THE WIRE ON PURPOSE. The client needs to tell the player that a Farm wants
    // Grassland and a Quarry wants Mountain; the alternative is a hard-coded table
    // in the client that silently drifts from the catalog the moment anyone retunes
    // it. Sending the server's own table means the UI cannot disagree with the sim.
    //
    // It is STATIC — a property of the build, not of the world — so it rides genesis
    // and is fetched exactly once. It is also PUBLIC: what a Farm costs is common
    // knowledge, not intelligence about anyone's holdings.
    //
    // This is REFERENCE DATA, not permission. The catalog says what a kind requires;
    // only the sim says whether this tile, right now, will take it.
    public BuildOptionDto[] Buildable { get; set; } = [];

    /// The demographic rules, straight off the world's PopulationConfig.
    ///
    /// SAME ARGUMENT AS THE BUILD CATALOGUE. A breeding UI has to tell the player why
    /// a citizen is not eligible — too young, past the window, house too poor — and
    /// the alternative is hard-coding numbers that are explicitly a tuning knob
    /// ("change it here and every age gate follows"). The client would start lying the
    /// first time anyone retuned demography.
    ///
    /// Static and public: how long a pregnancy takes is not intelligence about anyone.
    public PopulationRulesDto Population { get; set; } = new();

    /// M31 — the dynasty's rules, straight off the world's RoyaltyConfig.
    ///
    /// The Royal TAG on a unit says who wears the crown; these say what wearing it
    /// MEANS. Without them the client can draw a crown and nothing else: it cannot
    /// show the aura the king actually projects, and it cannot tell the player their
    /// monarch is a child projecting nothing — which is a visible, plannable window
    /// of weakness the design deliberately telegraphs. A window nobody can see is not
    /// telegraphed.
    ///
    /// Public and static, like every other rules block here.
    public RoyaltyRulesDto Royalty { get; set; } = new();

    /// What can be forged, what it costs, and who may carry it — straight off
    /// EquipmentCatalog.
    ///
    /// THE SAME ARGUMENT AS THE BUILD CATALOGUE, and it had already failed once
    /// without it. The client kept a hand-written copy of "a Sword is for
    /// Soldiers, a Bow for Archers", and that copy silently omitted the Cart —
    /// so a hauler could never be given one through the UI, and nothing in
    /// either codebase could notice. A mirror maintained by hand drifts the
    /// first time the catalog grows, which is exactly when a player most needs
    /// the UI to be right.
    ///
    /// Sending the server's own table means a new item APPEARS in the client
    /// with no client work at all — which is the real answer to "is the catalog
    /// easy to expand".
    ///
    /// Static and public, like every rules block above: what a sword costs is
    /// common knowledge, not intelligence about anyone's armoury.
    public EquipmentOptionDto[] Craftable { get; set; } = [];

    /// The world's time of day, as parameters (Atmosphere/WorldClock.cs).
    ///
    /// ONE MAPPING, TWO CONSUMERS. The client's sky and server-side narration must
    /// agree about when things happen, so the client evaluates the server's formula
    /// from these rather than inventing a clock of its own — and each view carries
    /// LightPhase so it can check that it did.
    public LightCycleDto LightCycle { get; set; } = new();
}

/// How long a day of light lasts, and where tick 0 falls in it.
/// Phase = ((tick + PhaseOffsetTicks) mod TicksPerCycle) / TicksPerCycle,
/// with 0 midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset.
public sealed class LightCycleDto
{
    public long TicksPerCycle { get; set; }
    public long PhaseOffsetTicks { get; set; }
}

/// One forgeable item.
public sealed class EquipmentOptionDto
{
    /// The Resource value the item IS — equipment lives in the resource
    /// namespace, so a sword sits in a stockpile and can be hauled like grain.
    public int Item { get; set; }

    /// Which building forges it (StructureKind), from the catalog's CraftedAt.
    public int CraftedAt { get; set; }

    /// The bill of materials, consumed from that building's own holdings.
    public ResAmtDto[] Cost { get; set; } = [];

    /// Roles that may carry it. The client greys or hides by this rather than
    /// by a table of its own — the omission that lost the Cart.
    public int[] AllowedRoles { get; set; } = [];

    // What it DOES. The player is spending ore on this; the menu has to say
    // why. All four are the same fields the sim copies into the buff instance
    // at equip time, so the menu cannot promise an effect the sim won't grant.
    public int PowerModifier { get; set; }
    public int HealthModifier { get; set; }
    public int CargoModifier { get; set; }
    public int MoveCostPercent { get; set; }

    /// Stable buff identity. A unit carries at most one buff of a given kind,
    /// and the wire already reports a unit's buffs BY KIND — so this is what
    /// lets the client say "he already has a sword" without a second lookup.
    public string BuffKind { get; set; } = "";
}

/// World-level dynasty settings. Ages are in AGE-YEARS, like every population gate.
public sealed class RoyaltyRulesDto
{
    /// Tiles. An integer EUCLIDEAN disc (dx*dx + dy*dy <= r*r), the same shape
    /// Sight.Reveal uses — so a client drawing a square would be drawing a lie.
    public int AuraRadius { get; set; }

    /// Flat power added to each of the king's own units inside the disc.
    public int AuraPowerBonus { get; set; }

    /// Below this age a crowned heir is a child monarch and projects NOTHING.
    public int MajorityAge { get; set; }
}

/// World-level demographic settings. Ages are in AGE-YEARS, the sim's compressed
/// demographic clock — not game-days and not real years.
public sealed class PopulationRulesDto
{
    public long TicksPerYear { get; set; }
    public int MinTrainAge { get; set; }
    public int MinFertileAge { get; set; }
    public int MaxFertileAge { get; set; }
    public long GestationTicks { get; set; }

    /// Food the House pays at the moment breeding starts.
    public int BirthFoodCost { get; set; }
}

// One player-buildable structure kind.
/// The placement gestures carried by BuildOptionDto.PlacementMode. Lives beside the
/// field it describes rather than inside the projector that happens to fill it.
public static class PlacementModes
{
    public const int Single = 0;
    public const int SiteAndSlip = 1;
    public const int Path = 2;
    public const int NotBuildable = 3;
}

public sealed class BuildOptionDto
{
    public int Kind { get; set; }
    public string Name { get; set; } = "";

    /// Biome the tile must be, or 0 (None) for "anywhere". The single most useful
    /// thing the build UI can tell a player before they click.
    public int RequiredBiome { get; set; }

    public ResAmtDto[] Cost { get; set; } = [];
    public int BuildersRequired { get; set; }
    public long BuildDurationTicks { get; set; }

    /// Working tiles this kind claims around itself (Farm 15, LumberCamp 8; 0 for
    /// everything else). Claimed land excludes ALL other structures, so a player who
    /// does not know this will not understand why their next building is refused.
    public int ClaimCount { get; set; }
    public int ClaimRange { get; set; }

    /// HOW this kind is placed — the gesture the client must run, decided by the
    /// server because the server is where the rejections live.
    ///
    ///   0 Single      one click on a tile.
    ///   1 SiteAndSlip two clicks: the building's land tile, then an adjacent WATER
    ///                 tile for its slip. The Dock, and only the Dock.
    ///   2 Path        a whole line or route with its own intent (Canal, Wall).
    ///                 PlaceSiteIntent rejects these by name.
    ///   3 NotBuildable never placed by a player at all (Rubble is CLEARED, not built).
    ///
    /// A flag was not enough. "Needs something special" told the client to hide the
    /// Dock, when what it actually needed was to know the Dock takes a second click.
    public int PlacementMode { get; set; }

    // What the thing DOES once it stands. The build menu has to answer "why would I
    // want this?" before the player commits wood to it, and every field below is a
    // pure read of the same StructureSpec the sim resolves against — so the menu
    // cannot describe a building the sim would not build.

    /// Resource it produces, or 0 (None) for non-extractors.
    public int OutputResource { get; set; }

    /// The role that works it best — a real production bonus, not flavour. This is
    /// the "what units work it" the build menu needs, and getting it from here rather
    /// than from a table in the client is what keeps the two from drifting.
    public int PreferredRole { get; set; }

    /// Workers it can take (0 = takes none).
    public int WorkerCap { get; set; }

    /// Resources it can hold (0 = not storage).
    public int StorageCapacity { get; set; }

    /// How many may call it home (0 = not a home, or uncapped as with the Castle).
    public int ResidentCap { get; set; }

    /// Refiners only (docs/refining-structures.md): what ONE unit of OutputResource
    /// consumes from the building's input store, and how much input it will hold.
    /// Empty / 0 for everything else. This is what lets the build menu say
    /// "2 Ore + 1 Wood → 1 Iron" without a recipe table of its own.
    public ResAmtDto[] Inputs { get; set; } = [];
    public int InputCap { get; set; }

    /// Roles a unit STANDING ON THIS BUILDING can be trained into — the School's
    /// civilian trades, the Barracks' Soldier and Archer, empty for everything else.
    /// Inverted from RoleTrainerCatalog so the client never has to know which
    /// building teaches what; a role added sim-side simply appears.
    public int[] TrainsRoles { get; set; } = [];
}

// Per-tile knowledge state, run-length encoded row-major across the whole grid.
// Three states, matching the client's three-state fog:
//   0 = unknown     (never seen — opaque fog)
//   1 = remembered  (explored, not currently in sight — stale, dimmed, no activity)
//   2 = live        (in sight right now — full colour, full activity)
// RLE because the state field is enormously coherent: a 63,504-tile map early in a
// game is a handful of runs, and even a fully-explored one is dominated by long
// spans of a single state.
public static class FogState
{
    public const int Unknown = 0;
    public const int Remembered = 1;
    public const int Live = 2;
}

// A tile whose BELIEVED biome differs from the genesis grid in WorldDto — i.e. the
// player has seen it degrade (or remembers a pre-degradation state that has since
// moved on). Emitted only for known tiles, and only on difference, so this array is
// empty for most of a game and small even late.
public sealed class BiomeOverrideDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Biome { get; set; }
}

// An active combat on a tile the viewer can currently SEE (fog still applies —
// fighting on a remembered-but-unseen tile stays hidden). Surfaced on v2 because
// the client needs it for smoke/fire/battle presentation; v1 never carried it
// despite Sim.Core having projected it since M7.
public sealed class CombatDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int RoundNumber { get; set; }
    public long NextRoundTick { get; set; }
}

// GET /v2/view/{playerId} — the per-tick payload.
//
// Inherits every dynamic field from ViewDto (units, structures, roads, notices,
// orders, scout reports, diplomacy, graves, piles, the food block, Tick) and
// REPLACES the tile arrays with fog runs + biome overrides. Visible/Remembered are
// always empty here; the client reconstructs terrain from WorldDto + FogRun* and
// never reads them.
public sealed class ViewV2Dto : ViewDto
{
    // Parallel arrays, run i = (FogRunState[i] repeated FogRunLength[i] times),
    // laid out row-major from tile (0,0). Lengths sum to Width * Height.
    // int (not byte) on purpose: System.Text.Json serializes byte[] as a base64
    // string, which Unity's JsonUtility cannot read.
    public int[] FogRunState { get; set; } = [];
    public int[] FogRunLength { get; set; } = [];

    public BiomeOverrideDto[] BiomeOverrides { get; set; } = [];
    public CombatDto[] Combats { get; set; } = [];

    /// The world's time of day at Tick, in [0, 1) — the server's own evaluation of
    /// WorldClock.Phase. The client interpolates between ticks from genesis
    /// LightCycle and uses this to prove its sky and the chronicle agree.
    public double LightPhase { get; set; }
}
