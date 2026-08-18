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
    // M26 — the wall segment awaiting confirmation (FortifyRung), same
    // observation discipline as PendingSite: next think, if NO tile of
    // the ordered line grew a structure, the server rejected the whole
    // intent (fail-clean)...
    public (List<(int X, int Y)> Tiles, long OrderedAt)? PendingWall;
    // ...and the next attempt halves — a bisect hunt for the bad tile
    // (a length-1 rejection blacklists that exact tile). Null = no cap
    // (an accepted segment resets it). Droppable: worst case a restart
    // re-orders one rejected segment and re-learns.
    public int? WallSegmentCap;
    // M27 — the canal order awaiting confirmation (IrrigateRung), same
    // observation discipline: if the anchor tile (path[0]) grew no site
    // by the next think, the server rejected the dig (a fog-hidden claim
    // or reservation mid-path) — blacklist the anchor so the BFS reroutes
    // from a different stretch of the water frontier.
    public (List<(int X, int Y)> Tiles, long OrderedAt)? PendingCanal;
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
    // tile → (tick seen, headcount). Refreshed on sight, cleared when
    // the tile is re-observed empty, expired after ThreatMemoryTicks.
    // Droppable like the rest: a restart re-spots anything still
    // prowling the moment it enters sight.
    public Dictionary<(int X, int Y), (long Tick, int Count)> SightedHostiles { get; } = new();
    // Cross-think OWNERSHIP of breeding candidates (arbitration lesson #6):
    // per-think reservations can't stop Eat from re-staffing a freed parent
    // the think after Grow freed them. Designation persists until the
    // breeding starts; every other selector skips designated units.
    public HashSet<int> DesignatedParents { get; } = new();

    // M25 — ENEMY INTEL (the Rival's map of the other side,
    // docs/m25-rival-spec.md): last-known foreign structures, tile →
    // (tick seen, kind, owner), and last-known castles keyed by owner.
    // The exact SightedHostiles discipline: refreshed on sight, cleared
    // when the tile is re-observed and the structure is gone (razed or
    // absent). War decisions additionally refuse intel older than
    // IntelStaleTicks. Droppable — a restart re-scouts.
    public Dictionary<(int X, int Y), (long Tick, int Kind, int OwnerId)> KnownEnemyStructures { get; } = new();
    public Dictionary<int, ((int X, int Y) Tile, long Tick)> KnownEnemyCastles { get; } = new();
    // M25 — probe rotation (the Rival's scouting pressure — ProbeRung's
    // spiral leg index, bounded by ProbeLegBudget per ledger #9). Reset
    // by EnemyIntel when the castle map CHANGES (a castle discovered or
    // observed razed): the world moved, the eyes earn a new budget.
    public int ProbeLeg;
    // M25 — the faction we are at (or moving toward) war with. A HINT,
    // not authority: WarRung re-adopts it from the view's diplomacy rows
    // every think (a restart, or a war someone declared ON us, re-derives
    // it) and clears it when the target is defeated or peace lands.
    public int? WarTarget;
    // M25 — when we last sued for peace. Own outgoing proposals aren't
    // on the wire (IncomingProposals is the target's slice), so the
    // brain can't SEE its open offer — this throttles re-proposing to a
    // cadence instead. Null = never.
    public long? LastPeaceProposedTick;
    // M25 — the campaign roster (ConquerRung): soldiers claimed for the
    // march, wired into ThinkContext's designation set so every other
    // selector — Defend's sortie, Muster's demob, staffing — skips them
    // (ledger #6 machinery, reused verbatim). CampaignLaunched separates
    // ASSEMBLE (converge on the rally) from MARCH (walk to the enemy
    // castle) — the one bit the view can't re-derive; dropping it on a
    // restart just walks the army home to reassemble.
    public HashSet<int> CampaignSoldiers { get; } = new();
    public bool CampaignLaunched;
    // M25 — the raid party (RaidRung): a small bounded detachment
    // (RaidPartySize, ledger #9) harassing the war target's extractors
    // while the campaign assembles. Same designation discipline as the
    // campaign roster. Dry-strike tracking mirrors ZeroBufferThinks:
    // enemy holdings are FOGGED, so "the structure is empty" is learned
    // by standing on it empty-handed for consecutive thinks — such
    // targets are struck from the list for this war.
    public HashSet<int> RaidParty { get; } = new();
    public HashSet<(int X, int Y)> DryRaidTargets { get; } = new();
    public Dictionary<(int X, int Y), int> RaidDryThinks { get; } = new();
    // Scavenge expedition (ScavengeRung): the party claimed for the
    // dead-kingdom sweep (designation discipline, ledger #6) and the
    // spill piles it has SEEN and not yet drained — piles ride the wire
    // only in current sight, so the freight loop needs its own map of
    // where the loot lies. Entries clear when the tile is re-observed
    // empty. Droppable: a restart re-learns the field by walking it.
    public HashSet<int> ScavengeParty { get; } = new();
    public HashSet<(int X, int Y)> ScavengePiles { get; } = new();
    // Battlefield salvage crew (SalvageRung): civilians claimed for the
    // loot walk, wired into ThinkContext's designation set (ledger #6) so
    // staffing/parent/scout selectors skip them mid-haul. Droppable: a
    // restart re-crews from whoever is idle, and carried loot still walks
    // home through the same cargo-first branch. No dry-target memory —
    // the host retires a looted grave from the view itself.
    public HashSet<int> SalvageCrew { get; } = new();
}
