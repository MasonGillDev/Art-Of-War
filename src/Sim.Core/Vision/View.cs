namespace Sim.Core.Vision;

// Projections — what a viewer (client / UI) gets for each visible entity.
// Carry only what's safe to show. No "Activity" or "Assignment" leaks for
// other players' units (a viewer shouldn't know whether an enemy unit is
// Hauling vs Idle). Own-player views fall through full detail elsewhere
// via direct world access.
public sealed record UnitView(int Id, TileCoord Position, UnitRole Role, int OwnerId, int Health, int AgeYears);

public sealed record StructureView(TileCoord At, StructureKind Kind, int OwnerId);

// M7 — public projection of an active combat. Surfaces only when the
// contested tile is in the viewer's Visible set (fog still applies).
public sealed record CombatView(TileCoord Tile, byte RoundNumber, long NextRoundTick);

// M6 diplomatic projections. Diplomatic state is PUBLIC KNOWLEDGE — every
// player sees every faction, every relationship, every pending war. Fog
// still hides positions and holdings; existence + diplomatic posture is
// surfaced to all.
public sealed record FactionView(int Id);
public sealed record RelationshipView(
    int LoId, int HiId,
    Sim.Core.Diplomacy.RelationshipState State,
    long? PendingEffectiveTick);
public sealed record PendingWarView(int LoId, int HiId, long EffectiveTick);
public sealed record ProposalView(
    int Id, int ProposerId, int TargetId,
    Sim.Core.Diplomacy.RelationshipState DesiredState,
    long ExpiryTick);

// The filtered view a player gets of the world. Visible tiles show
// current state; explored-not-visible tiles show only remembered biome;
// unexplored tiles are absent from both sets and from RememberedTerrain.
// VisibleUnits / VisibleStructures include OWN entities unconditionally
// plus any OTHER entities standing on a currently-visible tile.
//
// Diplomatic state (Factions / Relationships / PendingWars) is public
// knowledge — populated identically for every PlayerView. IncomingProposals
// is the only diplomatic field that's per-viewer scoped: offers stay
// private to the proposer/target pair until acceptance turns them into a
// relationship change (which is then public).
public sealed record PlayerView(
    int PlayerId,
    IReadOnlySet<TileCoord> Visible,
    IReadOnlySet<TileCoord> Explored,
    IReadOnlyDictionary<TileCoord, Biome> RememberedTerrain,
    IReadOnlyList<UnitView> VisibleUnits,
    IReadOnlyList<StructureView> VisibleStructures,
    IReadOnlyList<FactionView> Factions,
    IReadOnlyList<RelationshipView> Relationships,
    IReadOnlyList<PendingWarView> PendingWars,
    IReadOnlyList<ProposalView> IncomingProposals,
    // M7 — contested tiles inside the viewer's Visible set. Fog still
    // applies: combat on an explored-but-not-visible tile is hidden.
    IReadOnlyList<CombatView> OngoingCombats);

// PURE-READ WALL. Same discipline as Roads.Road.ConditionAt:
// computed fresh from current world state on every call, NEVER mutates.
// Path queries (visibility queries from a UI) must not affect the sim hash.
public static class View
{
    // Tiles currently visible to player `playerId`. Union of all of P's
    // owned vision sources' Euclidean discs. Returns a fresh HashSet every
    // call. Never writes.
    //
    // Bounded by `sources × r²` — no global per-tile sweep.
    public static HashSet<TileCoord> VisibleTiles(GameWorld world, int playerId)
    {
        var visible = new HashSet<TileCoord>();

        // Owned units contribute their role-based vision radius around
        // their current position.
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId != playerId) continue;
            var r = Sight.RadiusFor(u.Role);
            if (r > 0) AddDisc(world, visible, u.Position, r);
        }

        // Owned structures contribute their kind-based vision radius
        // around their tile. Non-vision kinds (Stockpile/Extractor/etc.)
        // return 0 and are skipped.
        foreach (var s in world.Structures.Values)
        {
            if (s.OwnerId != playerId) continue;
            var r = Sight.RadiusFor(s.Kind);
            if (r > 0) AddDisc(world, visible, s.At, r);
        }

        return visible;
    }

    // Builds a player's full filtered view. PURE READ — never writes any
    // sim state. The headline determinism test (M3 Phase F) calls this
    // many times during a scenario and asserts the sim hash is unchanged.
    //
    // Tiering rules:
    //   - currently-visible: full state available via raw world access on
    //     those tiles (clients can fetch live activity for them).
    //   - explored-not-visible: only static biome is included in
    //     RememberedTerrain. No live activity from non-owned entities.
    //   - unexplored: absent from both sets; no remembered terrain.
    //
    // Entity rules:
    //   - OWN units/structures: always included regardless of fog.
    //   - other-player entities: included only if standing on a
    //     currently-visible tile.
    public static PlayerView BuildPlayerView(GameWorld world, int playerId) =>
        BuildPlayerView(world, playerId, now: 0);

    // M8 — overload taking the sim's current tick so derived age is
    // accurate. The zero-now overload above is kept for callers that don't
    // care about age (most existing tests).
    public static PlayerView BuildPlayerView(GameWorld world, int playerId, long now)
    {
        var visible = VisibleTiles(world, playerId);
        var explored = world.Explored.TryGetValue(playerId, out var e)
            ? new HashSet<TileCoord>(e)
            : new HashSet<TileCoord>();

        // Remembered terrain = biome lookup for explored-but-not-visible tiles.
        var remembered = new Dictionary<TileCoord, Biome>();
        foreach (var t in explored)
            if (!visible.Contains(t))
                remembered[t] = world.Grid.BiomeAt(t);

        // Units: own unconditionally; others only if their tile is visible.
        var visibleUnits = new List<UnitView>();
        var popCfg = world.PopulationConfig;
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId == playerId || visible.Contains(u.Position))
            {
                var age = Sim.Core.Population.Population.AgeYears(u, now, popCfg);
                visibleUnits.Add(new UnitView(u.Id, u.Position, u.Role, u.OwnerId, u.Health, age));
            }
        }

        // Structures: same rule against the structure tile.
        var visibleStructures = new List<StructureView>();
        foreach (var s in world.Structures.Values)
        {
            if (s.OwnerId == playerId || visible.Contains(s.At))
                visibleStructures.Add(new StructureView(s.At, s.Kind, s.OwnerId));
        }

        // M6 diplomatic state — public knowledge for Factions / Relationships
        // / PendingWars; only IncomingProposals is per-viewer scoped.
        var factions = new List<FactionView>();
        foreach (var (id, _) in world.Players)
            factions.Add(new FactionView(id));

        var relationships = new List<RelationshipView>();
        var pendingWars = new List<PendingWarView>();
        foreach (var (pair, rel) in world.Diplomacy.Relationships)
        {
            relationships.Add(new RelationshipView(
                pair.Lo, pair.Hi, rel.State, rel.PendingEffectiveTick));
            if (rel.PendingEffectiveTick is { } tick)
                pendingWars.Add(new PendingWarView(pair.Lo, pair.Hi, tick));
        }

        var incomingProposals = new List<ProposalView>();
        foreach (var (_, p) in world.Diplomacy.Proposals)
        {
            if (p.TargetId != playerId) continue;
            incomingProposals.Add(new ProposalView(
                p.Id, p.ProposerId, p.TargetId, p.DesiredState, p.ExpiryTick));
        }

        // M7 — combats on visible tiles only (fog applies).
        var ongoingCombats = new List<CombatView>();
        foreach (var (tile, state) in world.CombatStates)
        {
            if (!visible.Contains(tile)) continue;
            ongoingCombats.Add(new CombatView(tile, state.RoundNumber, state.NextRoundTick));
        }

        return new PlayerView(
            playerId,
            visible,
            explored,
            remembered,
            visibleUnits,
            visibleStructures,
            factions,
            relationships,
            pendingWars,
            incomingProposals,
            ongoingCombats);
    }

    // Adds a Euclidean disc of radius `r` around `center` to `into`.
    // Same shape Sight.Reveal uses for explored — kept identical so the
    // visible/explored discs match exactly for the same source.
    private static void AddDisc(GameWorld world, HashSet<TileCoord> into, TileCoord center, int r)
    {
        var grid = world.Grid;
        var rSquared = r * r;
        var xLo = Math.Max(0, center.X - r);
        var xHi = Math.Min(grid.Width  - 1, center.X + r);
        var yLo = Math.Max(0, center.Y - r);
        var yHi = Math.Min(grid.Height - 1, center.Y + r);
        for (var y = yLo; y <= yHi; y++)
        {
            var dy = y - center.Y;
            var dy2 = dy * dy;
            for (var x = xLo; x <= xHi; x++)
            {
                var dx = x - center.X;
                if (dx * dx + dy2 <= rSquared)
                    into.Add(new TileCoord(x, y));
            }
        }
    }
}
