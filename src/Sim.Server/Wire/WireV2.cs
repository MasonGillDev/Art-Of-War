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
}
