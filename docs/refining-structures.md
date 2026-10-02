# Refining structures: a second hop in the supply chain

**Status: decided 2026-09-17, server side BUILT the same day** (see the
update at the bottom for the as-built shape). Client work is next.

## Decision

The economy gains **one intermediate hop** on the ore chain, and only there.
A new structure kind, the **Smelter**, consumes **Ore + Wood (fuel)** from its
own holdings and produces **Iron** (`Resource.Iron`, a new fungible resource).
The Sword's craft cost changes from Ore to Iron. To support it, `StructureSpec`
gains a **`InputCost`** table: a structure with inputs is a **refiner**, and its
production tick consumes inputs from holdings before it deposits output.

Two follow-on structures ride the same seam once it exists: a **Workshop**
(civil crafting: the Cart moves here, off the Barracks) and a **Smithy**
(weapons and shields move here, so the Barracks trains and nothing else).

Chains are capped at **two hops**: raw → refined → item. Food and construction
Wood stay **one hop forever**. No sawmill, no mill, no charcoal burner.

Launch shape (balance knobs, all in `StructureCatalog` / `EquipmentCatalog`):

| Structure | Inputs per period | Output | Notes |
|---|---|---|---|
| Smelter | 2 Ore + 1 Wood | 1 Iron | any biome; no claim; storage-backed |
| Sword (at Smithy) | 3 Iron + 2 Wood | 1 Sword | was 5 Ore + 5 Wood |
| Cart (at Workshop) | 20 Wood + 10 Stone | 1 Cart | unchanged cost, new home |

## Why

### Why the economy needs a second hop at all

Today there are four raw resources (Wood, Stone, Ore, Food) and four items
(Sword, Bow, Shield, Cart). Every recipe is raw-to-item, every item is forged
at the Barracks, and **Ore has exactly one consumer in the game: the Sword**.
A mine is a sword factory with extra steps. Every supply line is one hop,
extractor → stockpile → barracks, so there is nothing to route, split, or
choke.

That contradicts the game's own thesis. `docs/automation-as-core-game.md` law 3
says orders compose *through the world*: "one line fills a stockpile, another
drains it, a craft order eats from the barracks a supply line feeds. The
warehouse is the interface." With one-hop chains there is no second line to
drain the first. The Smelter is the first structure whose existence *requires*
two orders to cooperate through stock, which is the composition the vision
promises and the code cannot yet express.

The code already anticipated this. `EquipmentSpec.CraftedAt` was added so that
"a Workshop or a Smithy is a catalog row plus a StructureKind, not a second
intent", and the Cart's catalog entry says its move off the Barracks "waits for
that call". This is that call.

### Why fuel, not just ore

A smelter that turns Ore into Iron for free is a **renamed mine**: it adds a
haul and nothing else. Burning Wood per unit of Iron does three things the
rename would not:

- **A siting decision.** Ore comes from Hills, Wood from Forest. The smelter
  wants to be near both, or near the road between them, or near the barracks.
  That is an allocation decision, which law 1 keeps human. The Smelter needs no
  `RequiredBiome` precisely so the player has to *choose* where it goes.
- **A standing appetite.** A running smelter drains Wood forever, the
  industrial twin of "bread is electricity" (law 5). Wood stops being a
  build-phase resource and becomes an operating one.
- **A war target that bleeds.** Raze the smelter and the enemy's swords stop
  while their mine keeps filling. Cut the wood road and the smelter starves
  first, then the smithy. The chain fails in the order the map dictates.

### Why two hops is the ceiling

Factorio chains are deep because Factorio inputs are free once placed. Here
every hop is a **crew that eats** (law 4, law 5) and an **order the player must
place and re-crew when it dies**. A third hop (charcoal burner: Wood → Charcoal
→ Smelter) triples the orders for one weapon and pushes the player into
"automating the automation", which law 2 forbids. The vision doc's "Caps as
economics, not constants" says the limiter should be mouths and wages; two hops
already doubles the wage bill of a sword. That is enough pressure.

### Why food and construction wood stay one hop

- **Food.** M19 per-house food is the change that finally broke the Malthus
  wall (zero starvation deaths in 300 lab days). A mill or bakery adds a hop to
  the one resource the whole loop closes on. If food is ever refined, it is a
  *storage or cargo-density upgrade* (bread stores longer, packs tighter),
  never a requirement to eat.
- **Construction wood.** Wood is in every `BuildCost` in the catalog. A sawmill
  taxes the opening, which the vision doc already admits is "deliberately a
  little bit of a chore". If planks ever exist they gate Bow, Cart, and boats
  only. Not now.

### Why `InputCost` on the spec, not a new structure class

`Structure` already has `Holdings` (storage-backed kinds) and `Buffer`
(extractors). A refiner is a **storage-backed kind with a production tick**:
it eats from `Holdings`, it deposits to `Holdings`. That reuses the entire haul
/ deposit / pickup machinery, the standing supply-line order, and the raze
spill (`docs/sieges-and-conquest.md`), with zero new entities and zero new
persistence anchors. The one new thing is a spec field and a branch in the
production event.

Rejected: a separate `Refinery` entity with its own event. It would duplicate
the extractor's fencing, dormancy, and re-arm plumbing for no gain, and the
haulers would need to learn a third structure shape.

### Losing options

- **Rename only (Ore → Iron at the Barracks, no structure).** Adds a word, not
  a decision. Rejected above.
- **Smelter with `RequiredBiome = Hills` and claims, like the Mine.** Removes
  the siting decision and makes the smelter a mine upgrade. The whole point is
  that it can sit anywhere, so *where* is the player's problem.
- **Timed crafting at the Smithy (a smith who works N ticks per sword).**
  Deferred seam from M14, still deferred. Pacing lives upstream in mining,
  hauling, and now smelting; the craft itself stays instant.
- **Byproducts, quality tiers, or slag.** No. Every item stays a fungible,
  stateless `Resource` byte per `docs/equipment-model.md`.

## The production rule for a refiner

`ProductionTickEvent` grows one branch. When `spec.InputCost` is non-empty:

1. Validate as today (structure exists, right kind, fenced).
2. Compute `rate` from workers exactly as extractors do (`BaseRatePerWorker`,
   role bonus). No claim taper: `ClaimCount` is zero for refiners.
3. **Batches affordable** = min over inputs of `Holdings[input] / InputCost[input]`.
   Batches = min(rate, affordable, free storage for the output).
4. If batches is zero, go **dormant** exactly like an empty-worker extractor:
   clear the armed flag, do not reschedule. Re-arm on `HaulDepositEvent` to
   this tile (the existing pickup re-arm mirrored to deposit) and on
   `AssignWorkersIntent`.
5. Otherwise consume `batches × InputCost` from `Holdings`, deposit `batches ×
   1` of `OutputResource`, reschedule.

All-or-nothing per batch, integer math, no partial consumption. Deterministic
by construction: it reads only `Holdings`, `Workers`, and the spec.

Output goes to `Holdings`, not `Buffer`. `Buffer` and `BufferCap` stay
extractor-only. `FreeBuffer()` for a refiner means `StorageCapacity − Total()`.

## Acceptance tests

- A Smelter with 4 Ore, 1 Wood, one worker at rate 1 produces 1 Iron and holds
  2 Ore, 0 Wood after one period. Wood is the binding input.
- A Smelter with inputs but no workers does not tick. Assigning a worker arms
  it.
- A Smelter with workers but no Wood goes dormant. A haul deposit of Wood
  re-arms it; the next tick fires one period after the deposit, not
  immediately.
- A Smelter whose output would exceed `StorageCapacity` produces only what
  fits and consumes only the matching inputs.
- Crafting a Sword at a Smithy holding 3 Iron + 2 Wood succeeds. Crafting at a
  Barracks fails clean (wrong `CraftedAt`). Crafting with 5 Ore fails clean.
- Razing a Smelter spills its Iron, Ore, and Wood as a ground pile, like any
  storage kind.
- Snapshot round-trip: a Smelter mid-dormancy and a Smelter with an armed tick
  both restore to the same next-fire tick (existing
  `NextProductionTickSeq` anchor, no new field).
- Replay: a recorded game with smelter orders replays byte-identical.

## What gets built now vs. later

**Now (one milestone):**

- `Resource.Iron = 9` (append-only enum).
- `StructureKind.Smelter = 19`, `Workshop = 20`, `Smithy = 21`.
- `StructureSpec.InputCost` + the refiner branch in `ProductionTickEvent` +
  deposit re-arm.
- Catalog rows for the three structures. Sword → Iron. Cart → Workshop. Bow,
  Shield, Sword → Smithy. The Barracks keeps `StorageCapacity` so it can still
  hold equipment for `EquipUnitIntent`, but no `CraftedAt` points at it.
- Wire: the three kinds and Iron on the v2 genesis catalogue; the client's
  kind → prefab table gets three models (`docs/structure-models.md`); the
  order dashboard lists the smelter as a supply-line target.
- The standing craft order (`docs/automation-substrate.md`) already targets a
  tile plus an item, so it needs no change beyond the `CraftedAt` lookup.

**Not needed now, checked:** the AI brains. No rung in `src/Sim.Server/Ai/Rungs`
builds a Mine or issues `CraftEquipmentIntent`; `MusterRung` fields bare
soldiers by design and defers "the sword/ore chain" until the lab shows bare
squads losing. When that day comes, the AI needs a **SmeltRung** (build
smelter, run ore + wood lines into it) before an arming rung, and this is the
real cost of the second hop: every hop a human can automate, the AI must be
taught. Budget it then, not now.

**Later, each its own call:**

- **Iron in fortifications.** Gate and a future Castle upgrade take Iron. Gives
  ore a second consumer and makes the defensive game pull on the same chain as
  the offensive one.
- **Horses.** `docs/persistent-rts-design.md` §7.2 describes caravans with
  carts for capacity and horses for speed. A Stable producing a `Horse` item on
  the equipment/buff machinery (`docs/cart.md`) finishes that section with no
  new system. A Stable is a refiner with `InputCost = Food`.
- **Tools as a worker buff.** A Workshop item (Iron + Wood) that raises an
  extractor worker's rate. Same buff bag as the Cart.
- **Arrows.** Only after ranged combat exists. Do not hang a chain on a
  deferred seam.

## Future expansion

- **Any future refiner is a catalog row.** Stable, tannery, brewery, shipwright
  fittings: `InputCost` + `OutputResource` + `StorageCapacity`. No new event, no
  new intent.
- **Operating inputs generalise beyond refining.** The same `InputCost` branch
  can later express upkeep (a Lodge that burns Food to scout, a Barracks that
  burns Food to train) if the design ever wants structure-level wages on top of
  crew-level ones. That is a design call, not an engineering one; the seam is
  there.
- **Chain depth is a constant, not a rule.** Nothing in the engine prevents a
  third hop. The two-hop ceiling is a design law recorded here and enforced by
  review, not by code. If a future milestone wants a third hop, update this doc
  with an addendum and say what wage pressure justifies it.
- **What this closes off.** "Structures are pure sources or pure sinks" stops
  being true. Anyone reasoning about a structure's holdings must now allow for
  the structure itself draining them on a tick. The dormancy and re-arm
  contract is the load-bearing piece; keep it symmetric with the extractor's.

## Update 2026-09-17 — as built

Built the same day, with one deviation from the sketch above.

**The refiner is an `Extractor`, not a storage kind.** The sketch said a
refiner "eats from `Holdings`, deposits to `Holdings`". In the code an
`Extractor` has a single-resource output `Buffer`, not `Holdings`, and every
site that knows how to staff, arm, haul from, raze, snapshot, project or
raid a producing building pattern-matches on `Extractor`. Making the Smelter
an `Extractor` with a separate **input store** (`Extractor.Inputs`, fed by
`DepositInput`, drained by `ConsumeBatches`) kept all of those sites
working unchanged: haulers pick Iron out of the buffer exactly as they pick
Ore out of a mine, `AssignWorkersIntent` staffs it, the goal engine walks
workers to it, the view shows it, razing spills it. The only new plumbing is
the deposit case in `CargoTransfer.DepositInto` and the refiner branch in
`ProductionTickEvent`. `InputCap` bounds the input store **per recipe line**
(the output buffer keeps `BufferCap`); `StorageCapacity` stays zero for
refiners.

**Per-input cap, found by the host smoke.** The first cut had one shared
cap across all inputs. The `--refining` demo wedged on day 3: the fuel
hauler filled all 60 slots with wood, the ore hauler found no room, and the
smelter sat starved while full. A shared pool lets whichever supply line
runs faster crowd the others out, which is exactly the failure a player
cannot see from the outside. The cap is now per input, so every recipe line
always has its own room.

Everything else landed as written: `Resource.Iron = 9`;
`StructureKind.Smelter = 19`, `Workshop = 20`, `Smithy = 21`;
`StructureSpec.InputCost` / `InputCap` / `IsRefiner`; the Sword costs Iron
and is forged at the Smithy; Bow and Shield moved to the Smithy; the Cart
moved to the Workshop; the Barracks forges nothing (pinned by
`CraftEquipmentTests.Craft_AtBarracks_Rejected_ForgingMovedToSmithy`).
Snapshot format is v31. The wire's build-option DTO gained `Inputs` and
`InputCap` so the client's build menu can print the recipe; the client
itself (kind → prefab rows, build menu) is the next step and lives in the
sibling client repo.

Launch numbers as shipped (all knobs): Smelter 2 Ore + 1 Wood → 1 Iron per
worker per day, WorkerCap 2, Miner preferred at 2×, InputCap 60, BufferCap
20, build 40 Wood + 30 Stone. Sword 3 Iron + 2 Wood.

## Update 2026-09-19 — the AI learned the chain

The "SmeltRung" deferral above is paid: `ForgeRung` (Smithy → Mine on known
Hills → Smelter, staffed fewest-hands-first behind the Fortify surplus
gates) and `ArmRung` (forge to need, equip by the goal-shaped walk) in both
brains, with the ore/fuel/iron feed lines in `LogisticsLayer` and a
smithy-aware enemy estimate in `EnemyIntel`. Doctrine, lab findings and the
three new arbitration lessons are in `docs/ai-players.md` (2026-09-19
update); pins in `ArmTests`. Still each its own call: Miner training, the
Workshop cart for AI haulers, iron in fortifications.

## Update 2026-09-23 — fuel is 10 wood an ingot

User call after playing: the Smelter's recipe is now **2 ore + 10 wood → 1 iron**
(was 2 + 1), so iron leans on forestry as much as mining. `InputCap` rises from 60
to **200 per input**: at 10 wood an ingot, 60 wood was about a day and a half of
feed for a two-worker smelter, which broke "a few days of feed" above. A sword
(3 iron + 2 wood) now costs 6 ore and 32 wood end to end. Refining tests derive
their batches from `InputCost`, so they follow future retunes.

Related fix (M36): `PredicateEvaluator.StoredAmount` now counts a refiner's
**input** store for its input resources, so a supply line or haul job that keeps
ore at a smelter can read as satisfied instead of pushing ore into a full store.


## Update 2026-10-01 — the Smelter's ore comes from mountain veins (M44)

The Mine moved from Hills to Mountain, and it may only stand on an ore vein
its owner knows (surveyed by a Miner, or seen under someone's mine). The
Smelter recipe is unchanged. See `docs/stone-and-ore-land.md`.
