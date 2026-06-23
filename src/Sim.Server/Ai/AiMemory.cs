namespace Sim.Server.Ai;

// Droppable hints only — everything else is re-derived from the view.
public sealed class AiMemory
{
    public int ScoutLeg;
    // Site order awaiting confirmation + tiles where placement was
    // observed to fail (rejected server-side; skip them next search).
    public (Sim.Core.World.TileCoord Tile, long OrderedAt)? PendingSite;
    public HashSet<(int X, int Y)> BlacklistedTiles { get; } = new();
    // Claim-exhaustion inference: consecutive zero-buffer observations per
    // staffed extractor; past the threshold the extractor is treated as
    // dead (crew released, dropped from supply counts). Droppable like the
    // rest — a restart re-observes for 12 thinks and re-concludes.
    public Dictionary<(int X, int Y), int> ZeroBufferThinks { get; } = new();
    public HashSet<(int X, int Y)> DeadExtractors { get; } = new();
    // Set when the known-land inventory drops below the bank floor —
    // re-opens scouting past its budget. ForestStarved is Build's
    // distress flag (no known forest for a replacement camp); Eat ORs it
    // into LandStarved each think.
    public bool LandStarved;
    public bool ForestStarved;
    // First think each extractor was observed — farm mortality accounting
    // (age ≈ working life; replacements pre-build before the cliff).
    public Dictionary<(int X, int Y), long> FirstSeen { get; } = new();
    // The apprentice en route to the School, with the role they're
    // walking toward (role floors made the target variable) — same
    // ownership rule as parents (cleared on graduation or death).
    public (int Id, Sim.Core.World.UnitRole Target)? DesignatedTrainee;
    // The recruit en route to the Barracks (Muster rung) — same
    // ownership rule (cleared on graduation to Soldier or death).
    public int? DesignatedRecruit;
    // The veteran en route to the School to DEMOBILIZE (war footing
    // unwinding; cleared when they stop being a Soldier or die).
    public int? DesignatedVeteran;
    // THREAT MEMORY (Defend rung): last-known hostile sightings —
    // tile → (tick seen, headcount, estimated combat power). Refreshed on
    // sight, cleared when the tile is re-observed empty, expired after
    // ThreatMemoryTicks. Droppable like the rest: a restart re-spots anything
    // still prowling the moment it enters sight.
    // M25 — hostiles are bandits OR a declared-Enemy faction (the view's
    // public diplomacy), so a tile can hold soldiers/archers, not just
    // bandits. Power is summed from each sighted unit's ROLE via the combat
    // catalog (enemy Power is hidden in the fair view), so force-parity reads
    // true strength instead of assuming everyone is a bandit.
    public Dictionary<(int X, int Y), (long Tick, int Count, int Power)> SightedHostiles { get; } = new();
    // Cross-think OWNERSHIP of breeding candidates (arbitration lesson #6):
    // per-think reservations can't stop Eat from re-staffing a freed parent
    // the think after Grow freed them. Designation persists until the
    // breeding starts; every other selector skips designated units.
    public HashSet<int> DesignatedParents { get; } = new();

    // M25 — THE CAMPAIGN (RivalRung): the faction this colony is currently
    // prosecuting a war against, and a short reason for the trace. Set when a
    // casus belli fires (or in retaliation for an incoming war); the offensive
    // war machine (muster → form → march → siege) keys off it; cleared when the
    // war ends (peace or the target's elimination). Droppable like the rest: a
    // restart re-derives it from the view's diplomacy + the same triggers.
    public int? CampaignTarget;
    public string CampaignReason = "";
    // The current siege OBJECTIVE: the tile of the enemy structure the field
    // army is marching on, chosen by strike doctrine (military → economy →
    // castle). Updated each think from the target's visible structures; held
    // through re-fog so a column doesn't lose its target, dropped once reached.
    public Sim.Core.World.TileCoord? CampaignObjective;
    // The faction this colony has already sued for peace with (one olive branch
    // per campaign — proposals don't dedup, so the flag throttles the offer).
    // Cleared when the campaign ends.
    public int? PeaceProposedTo;
}
