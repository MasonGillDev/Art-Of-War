using Sim.Core.World;

namespace Sim.Core.Food;

// M19 Phase 2 (docs/m19-per-house-food-spec.md) — the food-home shape:
// any structure whose residents eat from its larder. The famine-debt
// machinery (lazy catch-up, debt, grace, death cadence, both
// self-rescheduling events and their fences) runs per home — the
// Castle's M13 implementation, generalized. Implementors: Castle (the
// uncapped mess hall for the mobile class; rate = population minus
// every housed resident) and House (rate = ResidentCount, harsh local
// famine by user decision: a dry house starves its own even when the
// castle is full).
//
// The anchor fields carry the same single-mutation contracts they had
// on Castle: Holdings[Food] is reduced only by FoodConsumption.CatchUp;
// FoodDebt grows only there and shrinks only in CargoTransfer.DepositInto;
// the event anchors are written only by their schedulers and cleared by
// their own fences.
public interface IFoodHome
{
    int OwnerId { get; }
    TileCoord At { get; }

    long LastFoodConsumedTick { get; set; }
    int FoodDebt { get; set; }
    long? FamineStartTick { get; set; }
    long? NextFamineCheckTick { get; set; }
    long? NextFamineCheckSeq { get; set; }
    long? NextStarvationDeathTick { get; set; }
    long? NextStarvationDeathSeq { get; set; }

    // Both satisfied by StorageStructure on the two implementors.
    int AmountOf(Resource resource);
    int Withdraw(Resource resource, int amount);
}
