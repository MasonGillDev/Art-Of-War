using Sim.Core.Combat;
using Sim.Core.Equipment;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// The shared FORGE-AND-EQUIP play (2026-09-19): ArmRung runs it over the
// Smithy for weapons, CartRung over the Workshop for carts. One store,
// a list of catalog items in doctrine order:
//
//   * CRAFT to need, never to bank: an item is forged only while more
//     eligible units lack it than the store holds. The catalog names the
//     recipe (EquipmentSpec.CraftCost); the store's own holdings pay —
//     LogisticsLayer's feed lines keep those stocked, so a missing haft
//     is a haul away, not a decision. One craft per think.
//   * EQUIP by walking: EquipUnitIntent is goal-shaped (M30) — name the
//     store and the sim walks the unit there and equips on arrival. A
//     unit under a goal reports GoalKind on the wire, so it is never
//     re-tasked mid-walk. Every idle lacking unit the stock covers.
//
// Returns null when there is nothing to do — an outfitted roster and an
// empty store both fall through the ladder.
public static class Outfitter
{
    public static Decision? Plan(ThinkContext ctx, string rung, StructDto store, Resource[] wanted)
    {
        var storeTile = ThinkContext.TileOf(store);
        var intents = new List<Intent>();
        var crafted = false;
        var why = new List<string>();
        foreach (var item in wanted)
        {
            var spec = EquipmentCatalog.Spec(item);
            if (spec.CraftedAt != (StructureKind)store.Kind) continue;
            var lacking = ctx.OwnUnits.Where(u => Lacks(u, spec)).OrderBy(u => u.Id).ToList();
            if (lacking.Count == 0) continue;
            var stock = ThinkContext.AmountOf(store.Holdings, item);
            var name = item.ToString().ToLowerInvariant();

            var handed = 0;
            foreach (var u in lacking)
            {
                if (handed >= stock) break;
                if (!ctx.IsIdleStill(u) || !ctx.IsFree(u) || u.GoalKind != 0 || u.CargoAmount > 0) continue;
                intents.Add(new EquipUnitIntent(ctx.Reserve(u).Id, item, storeTile) { PlayerId = ctx.PlayerId });
                handed++;
            }
            if (handed > 0) why.Add($"{handed} {name}(s) issued");

            if (crafted || stock >= lacking.Count) continue;
            var affordable = spec.CraftCost.All(kv =>
                ThinkContext.AmountOf(store.Holdings, kv.Key) >= kv.Value);
            if (!affordable) continue;
            intents.Add(new CraftEquipmentIntent(storeTile, item) { PlayerId = ctx.PlayerId });
            crafted = true;
            why.Add($"forging a {name} ({lacking.Count} lack one)");
        }
        return intents.Count == 0 ? null : new Decision(rung, string.Join(", ", why), intents);
    }

    // A unit this item is for, who doesn't carry one and still has a
    // slot. Buffs on the wire are the equipped buff KINDS — own units
    // only, which is all this play ever looks at.
    public static bool Lacks(UnitDto u, EquipmentSpec spec) =>
        spec.AllowedRoles.Contains((UnitRole)u.Role)
        && !u.Buffs.Contains(spec.BuffKind)
        && u.Buffs.Length < BuffRules.MaxBuffsPerUnit;
}
