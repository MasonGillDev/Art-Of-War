using System.Text.Json.Serialization;

namespace Sim.Core.Sieges;

// M26 — reclaim razed ground (docs/sieges-and-conquest.md update). The
// conquest flow the design wants: destroy a structure → clear the rubble →
// build on the land. This intent swaps a Rubble pile for an ordinary
// ConstructionSite targeting StructureKind.Rubble — a MATERIALS-FREE labor
// job (catalog: no cost, one builder, a day of work) that reuses the whole
// construction stack: AssignBuildersIntent staffs it, StartOrResume
// schedules it, pause/resume fences it, the snapshot round-trips it, and
// BuildCompleteEvent's clearing branch finishes it by leaving the tile
// EMPTY (the canal's "no resulting structure" pattern).
//
// ANY player may clear ANY rubble — reclaiming the land of fallen kingdoms
// is the point, and rubble is unowned wreckage (owner sentinel -3). The
// clearing site belongs to whoever ordered it and is contestable like any
// site (site HP 25; razing it turns the tile back to rubble — consistent).
public sealed class ClearRubbleIntent : Intent
{
    public TileCoord Tile { get; }

    [JsonConstructor]
    public ClearRubbleIntent(TileCoord tile) { Tile = tile; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Grid.InBounds(Tile))
            return IntentOutcome.Reject($"tile {Tile.X},{Tile.Y} out of bounds");
        if (!world.Structures.TryGetValue(Tile, out var s) || s is not Rubble)
            return IntentOutcome.Reject($"no rubble at {Tile.X},{Tile.Y}");

        // Swap: the pile out, the clearing job in. Direct replacement of the
        // tile's occupant, same shape as SiegeDamage.RazeStructure's swap.
        world.Structures.Remove(Tile);
        world.AddStructure(new ConstructionSite(Tile, StructureKind.Rubble)
        {
            OwnerId = PlayerId,
        });
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ClearRubble(@ {Tile.X},{Tile.Y})";
}
