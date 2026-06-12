using Sim.Core.Automation;
using Sim.Core.World;

namespace Sim.Server.Automation;

// M18 — evaluates one condition atom against what the order's OWNER can
// see. PURE READ: computed fresh from current state, never writes (the
// 100×-no-mutation test pins this). One case per ConditionKind; a new atom
// is one enum value + one case here.
//
// THE FOG CONTRACT (docs/m18-automation-engine-spec.md): structure-subject
// conditions require the tile to be in the owner's CURRENT visibility set —
// an unseen subject evaluates NOT-met (conservative: unknown is never
// true), so automation cannot react to fogged state. Unit-subject
// conditions reference the order's claimed units, which are the owner's
// own units — a player always knows their own units' state, no visibility
// check needed. A claimed unit that no longer exists (died) evaluates
// NOT-met; the step stalls and the bounded-retry rule eventually disables
// the order with a notice.
//
// The visibility set is computed ONCE per player per think
// (View.VisibleTiles) and passed in — N conditions must not recompute an
// O(sources × r²) read N times.
public static class ConditionEvaluator
{
    public static bool IsMet(
        GameWorld world,
        int ownerId,
        in ConditionSpec c,
        long stepEnteredTick,
        long now,
        IReadOnlySet<TileCoord> visible)
    {
        switch (c.Kind)
        {
            case ConditionKind.Always:
                return true;

            case ConditionKind.StoreAtLeast:
                if (!visible.Contains(c.SubjectTile)) return false;
                return StoredAmount(world, c.SubjectTile, c.Resource) >= c.Threshold;

            case ConditionKind.StoreBelow:
                if (!visible.Contains(c.SubjectTile)) return false;
                return StoredAmount(world, c.SubjectTile, c.Resource) < c.Threshold;

            case ConditionKind.CargoFull:
            {
                if (!TryGetOwnUnit(world, ownerId, c.SubjectUnitId, out var u)) return false;
                return u.CargoAmount >= u.CargoCapacity;
            }

            case ConditionKind.CargoEmpty:
            {
                if (!TryGetOwnUnit(world, ownerId, c.SubjectUnitId, out var u)) return false;
                return u.CargoAmount == 0;
            }

            case ConditionKind.UnitAtTile:
            {
                if (!TryGetOwnUnit(world, ownerId, c.SubjectUnitId, out var u)) return false;
                if (u.IsEmbarked) return false; // off-tile passenger; Position is stale
                return u.Position == c.SubjectTile;
            }

            case ConditionKind.ElapsedTicks:
                return now - stepEnteredTick >= c.Threshold;

            default:
                // Set-time validation rejects unknown kinds, so this is a
                // can't-happen — fail loudly at the cause (ban §4.9).
                throw new InvalidOperationException(
                    $"ConditionEvaluator has no case for ConditionKind {(byte)c.Kind} — " +
                    "was a new atom added to the enum without an evaluator case?");
        }
    }

    // What's observably stored at a visible tile: StorageStructure holdings,
    // or an Extractor's buffer when the resource matches its output. No
    // structure (or a stockless kind) reads as 0 — observable truth.
    private static long StoredAmount(GameWorld world, TileCoord tile, Resource r)
    {
        if (!world.Structures.TryGetValue(tile, out var s)) return 0;
        return s switch
        {
            StorageStructure storage => storage.AmountOf(r),
            Extractor e => e.Spec.OutputResource == r ? e.Buffer : 0,
            _ => 0,
        };
    }

    private static bool TryGetOwnUnit(GameWorld world, int ownerId, int unitId, out Unit unit)
    {
        if (world.Units.TryGetValue(unitId, out var u) && u.OwnerId == ownerId)
        {
            unit = u;
            return true;
        }
        unit = null!;
        return false;
    }
}
