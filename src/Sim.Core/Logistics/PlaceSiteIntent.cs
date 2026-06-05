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

    [System.Text.Json.Serialization.JsonConstructor]
    public PlaceSiteIntent(TileCoord tile, StructureKind kind)
    {
        Tile = tile;
        Kind = kind;
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

        // OwnerId carried from the issuing player via the base Intent.PlayerId.
        // The built structure (when BuildCompleteEvent fires) inherits this.
        sim.World.AddStructure(new ConstructionSite(Tile, Kind) { OwnerId = PlayerId });
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"PlaceSite(kind={Kind} @ {Tile.X},{Tile.Y})";
}
