using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung — Arm (2026-09-19): spend the armoury. Once a Smithy stands
// (ForgeRung's job) this rung runs the shared forge-and-equip play
// (Outfitter) over it: sword before shield (power before health — the
// catalog's PowerModifier is what the Rival's overmatch gate and the
// Homesteader's sortie both cash in); bows only for archers, which no
// brain trains yet — the row is here so it works the day one does.
public sealed class ArmRung : IRung
{
    // Cart is hauler gear (docs/cart.md) and lives on CartRung's chain.
    private static readonly Resource[] Wanted = { Resource.Sword, Resource.Bow, Resource.Shield };

    public Decision? TryClaim(ThinkContext ctx)
    {
        if (!ctx.Cfg.Arm) return null;
        var smithy = ctx.OwnStructure(StructureKind.Smithy);
        if (smithy is null) return null;
        return Outfitter.Plan(ctx, "arm", smithy, Wanted);
    }
}
