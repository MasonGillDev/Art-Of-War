namespace Sim.Core.Logistics;

// Places a ConstructionSite on a tile so materials can be hauled in and
// builders can be assigned. Does NOT consume resources or start construction
// — see AssignBuildersIntent and BuildCompleteEvent for the rest of the chain.
//
// Rejection cases (per docs/intent-validation.md, every check re-runs at
// resolution time, mutates nothing on failure):
//   * Kind is not player-buildable (Castle / ConstructionSite / Tower).
//   * Tile out of bounds.
//   * Tile already has a structure on it.
//   * Tile's biome doesn't match the kind's RequiredBiome (unless the kind
//     has no biome requirement, e.g. Stockpile).
public sealed class PlaceSiteIntent : Intent
{
    public TileCoord Tile { get; }
    public StructureKind Kind { get; }

    // M12 — Dock-only. The water tile the boat will spawn on once the
    // Dock is built (Phase C production-job). Must be 4-adjacent to
    // Tile and Water biome at resolution time. Null for non-Dock kinds.
    public TileCoord? DockSlip { get; }

    // M15 — claiming kinds only (Spec.ClaimCount > 0: LumberCamp, Farm).
    // The working tiles the player painted; null = "auto-select for me"
    // (deterministic Claims.AutoSelect; placement rejects if the land
    // can't support a full claim). Ignored for non-claiming kinds.
    public List<TileCoord>? ClaimTiles { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public PlaceSiteIntent(TileCoord tile, StructureKind kind, TileCoord? dockSlip = null,
        List<TileCoord>? claimTiles = null)
    {
        Tile = tile;
        Kind = kind;
        DockSlip = dockSlip;
        ClaimTiles = claimTiles;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var spec = StructureCatalog.Spec(Kind);
        if (!spec.IsPlayerBuildable)
            return IntentOutcome.Reject($"{Kind} is not player-buildable");

        if (!sim.World.Grid.InBounds(Tile))
            return IntentOutcome.Reject($"tile {Tile.X},{Tile.Y} out of bounds");

        if (sim.World.Structures.ContainsKey(Tile))
            return IntentOutcome.Reject($"tile {Tile.X},{Tile.Y} already has a structure");

        // M15 — full structural exclusion: NO structure of any kind may be
        // placed on a tile claimed by anyone (the owner included). Claims
        // are physical territory. docs/extraction-claims.md.
        if (Claims.ClaimantAt(sim.World, Tile) is { } claimant)
            return IntentOutcome.Reject(
                $"tile {Tile.X},{Tile.Y} is claimed by the structure at {claimant.X},{claimant.Y}");

        if (spec.RequiredBiome != Biome.None)
        {
            // M9: validate against the DERIVED biome (BiomeAt), not the
            // worldgen biome. A formerly-Forest tile that has degraded to
            // Grassland is now legitimately Grassland — it rejects a
            // LumberCamp and accepts a Farm. See docs/biome-degradation.md.
            var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                sim.World, Tile, sim.Now, sim.World.BiomeDegradationConfig);
            if (biome != spec.RequiredBiome)
                return IntentOutcome.Reject(
                    $"{Kind} requires {spec.RequiredBiome} but tile is {biome}");
        }

        // M12 — Dock placement requires:
        //   * The dock tile is a land tile (not Water, not None).
        //   * DockSlip is supplied.
        //   * DockSlip is 4-adjacent to Tile.
        //   * DockSlip is in bounds and currently Water (derived biome).
        if (Kind == StructureKind.Dock)
        {
            var dockTileBiome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                sim.World, Tile, sim.Now, sim.World.BiomeDegradationConfig);
            if (dockTileBiome == Biome.Water || dockTileBiome == Biome.None)
                return IntentOutcome.Reject(
                    $"Dock requires a land tile but {Tile.X},{Tile.Y} is {dockTileBiome}");
            if (DockSlip is not TileCoord slip)
                return IntentOutcome.Reject("Dock placement requires a slip tile");
            if (!sim.World.Grid.InBounds(slip))
                return IntentOutcome.Reject($"slip {slip.X},{slip.Y} out of bounds");
            if (!Is4Adjacent(Tile, slip))
                return IntentOutcome.Reject(
                    $"slip {slip.X},{slip.Y} is not 4-adjacent to dock tile {Tile.X},{Tile.Y}");
            var slipBiome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                sim.World, slip, sim.Now, sim.World.BiomeDegradationConfig);
            if (slipBiome != Biome.Water)
                return IntentOutcome.Reject(
                    $"slip {slip.X},{slip.Y} must be Water but is {slipBiome}");
        }

        // M15 — claiming kinds reserve their working tiles AT PLACEMENT so
        // two in-flight sites can't promise the same land. Explicit list →
        // strict validation; omitted → deterministic auto-select. Either
        // way the claim resolves (and fail-clean rejects) BEFORE the site
        // mutates the world. Stored canonical (y, x).
        List<TileCoord>? claim = null;
        if (spec.ClaimCount > 0)
        {
            if (ClaimTiles is not null)
            {
                var reason = Claims.Validate(sim.World, Tile, spec, ClaimTiles, sim.Now);
                if (reason is not null)
                    return IntentOutcome.Reject(reason);
                claim = new List<TileCoord>(ClaimTiles);
                claim.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            }
            else
            {
                claim = Claims.AutoSelect(sim.World, Tile, spec, sim.Now);
                if (claim is null)
                    return IntentOutcome.Reject(
                        $"insufficient claimable {spec.RequiredBiome} for {Kind} " +
                        $"(needs {spec.ClaimCount} tiles within range {spec.ClaimRange})");
            }
        }

        // OwnerId carried from the issuing player via the base Intent.PlayerId.
        // The built structure (when BuildCompleteEvent fires) inherits this.
        var site = new ConstructionSite(Tile, Kind)
        {
            OwnerId = PlayerId,
            DockSlip = Kind == StructureKind.Dock ? DockSlip : null,
        };
        if (claim is not null) site.ClaimTiles.AddRange(claim);
        sim.World.AddStructure(site);
        return IntentOutcome.Applied;
    }

    private static bool Is4Adjacent(TileCoord a, TileCoord b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return (dx == 1 && dy == 0) || (dx == 0 && dy == 1);
    }

    public override string Describe() =>
        $"PlaceSite(kind={Kind} @ {Tile.X},{Tile.Y}{(DockSlip is { } s ? $", slip={s.X},{s.Y}" : "")})";
}
