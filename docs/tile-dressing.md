# Tile dressing: recipes turn a built structure into a place

**Decision (2026-09-17).** A structure's tile is dressed by a **recipe per structure
kind** — an asset of RULES (main building, door-to-road, fence, path, clutter, worked
plots) — resolved deterministically by a **dresser** into placed props, and published
to the other ground layers through a **registry** so scatter, grass and terrain colour
agree with it. The farm is the first recipe. Presentation only: the sim's tile, claims
and roads are inputs, nothing flows back.

Files (production client): `Client/Dressing/TileRecipe.cs` (+ `TileRecipeSet`),
`TileDresser.cs`, `TileDressingRegistry.cs`, `PackMaterials.cs`,
`StructurePlacements.cs`; hooks in `EntityRenderer`, `ScatterRenderer`, `GrassCarpet`,
`TerrainColorizer`/`TerrainRenderer`, `EntityPicker`, `UnitMotion`;
`Editor/SetupTileRecipes.cs` (Window > Aow > Set Up Tile Recipes).

## Why

- **The user's brief:** "each structure has a script that defines how to place defined
  props on that tile … fires when a build finishes … robust, adapts with new systems
  and assets", with the farm as the worked example (house facing the road, fence round
  the perimeter, wood path door→road, clutter, plots with tall yellow grass and a
  field ground texture).
- **Rules, not positions.** A recipe says "two to four barrels in the yard, off the
  path, not under the house", and the dresser resolves that from the tile's hash, the
  roads beside it and the ground under it. One recipe dresses every farm differently
  and the same farm identically on every client and every reload. Authored positions
  would break the moment a tile's roads or slope differed.
- **"Fires when a build finishes" is a cache signature, not an event.** The dressing
  is a pure function of (kind, claims, nearby roads, recipe version, terrain bake); the
  renderer rebuilds a tile's dressing when that signature changes and keeps it
  otherwise. So a farm dresses itself the tick it appears, re-dresses when a road is
  laid beside it or a plot is claimed, and needs no event plumbing — which also makes
  it survive reloads and fog reveal in any order.
- **Roads are on tile edges** (`docs/roads-as-draped-ribbons.md`, update of this date),
  so "face the road" is decided per edge: the north edge carries a road when the tile
  above and its east neighbour are both road tiles, and so on; failing an edge, any
  road neighbour; failing that, a hashed direction so a lone farm still has a front.
- **The ground layers consult one registry** rather than each re-deriving "is this a
  farm plot": the nature scatter keeps trees off yards and plots, the grass carpet
  grows the recipe's crop cards at the recipe's density on plots (whatever the biome)
  and thins in the yard, the terrain tints plots toward tilled earth and the yard toward
  beaten ground. Each layer folds `TileDressingRegistry.Version` into its own cache
  signature, so a new farm clears and re-plants its ground on the next tick.
- **One placement, every reader.** `StructurePlacements.TryPlace` answers "where does
  this structure's model stand" for the picker, the person clearance and the hearth,
  from the dressing if there is one and the plain model set if not — so a house turned
  to face the road is picked and walked around where it is drawn.
- **Packs on the wrong pipeline** (`RPGPP_LT` ships built-in Standard materials) are
  handled once, in `PackMaterials`: a URP Lit twin per source material, cached. No
  hand edits to a pack's materials.

Alternatives ruled out: prefab-per-structure set pieces (one arrangement for every
farm, no road awareness, and the debug client already had that); authoring in code per
kind (recipes are data so the art side owns them); a Unity-scene-based tile template
(no determinism across clients, and a GameObject per prop defeats the instancing budget).

## Future expansion

- **More recipes** are assets: House, Lumber camp, Quarry, Mine, School, Barracks…
  each an entry in the set. Kinds without a recipe keep the model set or the district.
- **Anchors for choreography.** `DressedTile` keeps the door point, the facing and the
  gate; the work choreography can walk a farmer along the plots, a miner into the
  door, a hauler up the path — "a similar approach for animations within a tile", as
  the user put it.
- **Plot props** (rows, hay, a scarecrow) belong on the recipe as clutter for plots;
  the registry already marks plots.
- **Hand-made assets** replace prefab references on the recipe assets; nothing else.
- **Per-recipe LOD** if yards ever cost: props already vanish past `PropDistance`.

## Update 2026-09-17 (late) — ground kinds: a texture per tile, from an array, faded

**Decision.** A tile's ground texture is chosen per tile by a byte map
(`GroundMap`), sampled point-wise by the terrain shader, indexing one
**Texture2DArray** built from a **Ground Kinds asset** (`GroundKindSet`: a row per
kind — name, albedo, tiling, tint). Recipes name a kind by string (`YardGroundKind`,
`PlotGroundKind`). The shader blends the tile's layer with its neighbours' across the
tile line over `FadeTiles`, so a field fades into the meadow beside it.

**Why an array, not slots.** The first cut bound four textures to the terrain
material with an if-chain. The user: "definitely array — trying to do things right
the first time". A layer per kind means: no branching, no cap short of the parameter
table (32), adding a texture is adding a row, per-layer tiling/tint travel as a
vector table, and blending two tiles' layers for a fade is two more samples. The
cost is one shape — every layer is resampled to `LayerSize` on build by the editor
menu (blit through a render texture, so import settings do not matter).

**Why a per-tile map, not vertex colour.** Terrain colour lives on shared tile
corners and interpolates across a whole tile; a texture chosen that way would smear
a tile wide. The map is sampled per fragment at its own tile, so the edge is the tile
line — and then softened deliberately, in the shader, by `FadeTiles` either side.
Everything is bound as shader GLOBALS (map, array, params, fade), so no material
needs assigning and the terrain material knows nothing about kinds.

**Workflow.** Window > Aow > Set Up Ground Kinds creates the asset with a starter
vocabulary (Field, Yard, Sand, Cobble, Moss from the nature pack), builds the array
beside it and points the recipe set at it. Re-run after adding rows. Row order is the
id: append, do not reorder, once tiles are authored against it.

Deferred: a sibling normal-map array; two kinds per tile with a weight (the map has
the byte for it; the shader's fade already does two-layer blending).

## Update 2026-09-17 (later) — a ROW of buildings, a back-yard fence, plants round the walls

**Decision.** A recipe may stand several copies of its building on one tile
(`BuildingsMin..BuildingsMax`, up to 4), in a row along the road frontage, each with
its own path down to the road; `RowPosition` slides the row front to back (0.5 = the
tile centre, so every existing recipe is unchanged) and `RowJitter` lets each house
stray a little in yaw and depth. The fence has two layouts: `Yard` (round the tile,
a gap at every path) and `BackYard` (flanks, rear, and a run along the backs of the
houses that stops at each wall, so the fence joins house to house). Clutter gains
`NearBuilding`: a ring just off a house's walls. The first House recipe is a hamlet:
two or three `rpgpp_lt_building_03` cottages, wood paths, a shared fenced back yard,
and the PolygonNature bushes, flowers and ferns grown up against the walls.

**Why a row on the recipe, not a second structure kind.** The sim has one House on
one tile; how many roofs that reads as is presentation. Putting the count on the
recipe keeps the sim's tile the unit of truth, lets the same rule dress a farmhouse
(1..1) and a hamlet (2..3), and keeps the door/gate anchors: the first house's are
the tile's, for the choreography and the pick box. Every building rides the model
path on its own (culled, LOD'd, block proxy), so a row costs no new draw kind.

**The road frame.** The dresser now works in (along, across): `along` toward the
road, `across` along the frontage. Fence rectangles, house slots, paths and the
back-yard line are all expressed there and rotated into the world once, which is
what made two fence layouts one loop. A latent bug fell out: the corner posts were
turned by +90° per side while the winding needed −90°; corners now take the same
turn as the straight run that starts at them.

**Deferred.** Picking hits only the first house (StructurePlacements answers with
`Main`); a hamlet's other cottages need the picker to walk `Buildings`. Plants on
the plots, and a per-house colour variation, wait for the hand-made assets.

## Update 2026-09-18 — School, Castle; a wall is a fence at scale; props keep off each other

**Decision.** The Castle no longer draws the generated 850k-triangle model: it is a
recipe like everything else — the pack's largest hall set back on a cobbled ward,
a curtain wall made of the nature pack's dry-stone wall prop at `FenceScale` 3 with
a pillar at `CornerScale` 2.5 for a tower on each corner, banners flanking the gate
(`Clutter.NearGate`), and outbuildings that are the pack's other houses placed as
clutter along the wall. The School is the same hall on a cobbled yard with the lesson
held outside (benches, the master's table, a well) and shade trees along the fence.

**Why scale the fence rather than add a "wall" feature.** A wall is a fence with
bigger pieces: same rectangle, same gap at the gate, same corner turn. Two dials
(`FenceScale`, `CornerScale`) give a curtain wall and towers from props the packs
already have, and the hand-made assets will drop into the same slots.

**Why an overlap test now.** Placing whole buildings as clutter made collisions
visible: the dresser now keeps every laid prop's ground radius and rejects a spot
that lands on one (with a little tolerance), for every recipe. Fence-hugging props
also step inside by the wall's half-thickness plus their own radius, so a shed does
not stand in the wall.

Deferred: the Castle's `StructureModelSet` entry still points at the glb (harmless,
the dressed tile wins); real wall and tower pieces when the hand-made set arrives.

## Update 2026-09-18 — ground kinds become the terrain's splat

The ground-kinds array is no longer only a per-tile overlay for yards and plots: it is
the vocabulary every ground material comes from. `GroundKindSet` gained a per-biome
table (base / dry / wet), role kinds (scree, sediment), a per-row `Replace` (modulate
the tile tint vs replace it), a sibling normal array and height in alpha; the global
`Strength` is gone (it was one number for every row). `GroundMap` carries two bytes per
tile now (kind, believed biome). Which layer a fragment wears is decided by the baked
terrain field. Decision and rationale: `docs/terrain-surface-data.md`.

## Update 2026-09-24 — every building kind gets a recipe; the yard carries the identity

`Set Up Tile Recipes` now authors ten more recipes: Stockpile, LumberCamp, Quarry,
Mine, Dock, Barracks, Lodge, Smelter, Workshop, Smithy. With these every structure
kind is dressed by a recipe or by the fortification kit, except the Canal (it is
water; nothing stands there). Wall, Gate, Tower and Rubble stay with
`FortificationDresser`.

**Five buildings, fourteen dressed kinds: buildings repeat, the yard differs.** The
RPGPP_LT pack has five buildings (01 long hall, 02 low house, 03 broad cottage, 04
compact and tall, 05 big square). The other option was to hunt more building packs or
to push the Synty modular kit into bespoke buildings per kind. Both lost: a new pack
means a new look to reconcile, and a hand-built kit building per kind is a lot of art
work for things the camera mostly sees from altitude. So kinds share buildings, and
what tells them apart is the yard: its ground kind (Scree, RockGround, Litter, Sand,
Pebbles, Leaves, Trail, Cobble, Yard), its setpieces (a cave mouth for the mine's adit,
rock clusters for the quarry face, stumps and log piles, ore heaps, net racks, a
palisade), and whether there is a fence at all. What each building went to:

| Building | Kinds |
|---|---|
| 01 long hall | School, Castle, Barracks (two in a straight row) |
| 02 low house | Farm, Quarry, Dock, Smithy |
| 03 broad cottage | House (2–3), Workshop |
| 04 compact, tall | LumberCamp, Mine, Lodge |
| 05 big square | Stockpile, Smelter |

**Footprints follow the props' scale.** A work building's `Footprint` is about 2 world
units per prefab metre, the same as `PropScale`, so a hut and its barrels agree. Only
the institutions (School, Castle) are blown up past that.

**Work yards have no fence but keep a `FenceInset`.** With no `FenceStraight` nothing
is drawn, but the inset still defines the yard's ring, and `NearFence` props (rock
faces, log piles, the adit, the slag heap) stand on it round a building at the tile
centre. This is a convention, not new code: the dresser already treated an inset with
no fence this way. The trap it avoids: with `FenceInset` 0, `NearFence` puts props on a
line 2.5 inside the tile edge, which the in-tile check (3 inside) rejects every time.

**Two more packs join the dressing.** Polytope's environment pack (URP Amplify shaders,
instancing on) gives ore rocks, pine log piles and stumps; the Synty nature pack gives
the rock clusters, the cave entrance, bricks, campfire, torches and signpost. RPGPP has
none of those.

Deferred and known: the Dock faces the road, not the water (the dresser does not know
the shore edge; a `FaceWater` rule would need the water mask per edge); there is no
anvil, forge or target prefab in any pack, so the Smithy is read by its canopy, fire,
trough and racked shields and the Barracks' practice is arrows in the ground; the
campfires and torches are unlit (particle FX are not flattened into props); a large
`NearFence` setpiece can still clip a building, because `Blocked` tests the prop's
centre and not its radius.
