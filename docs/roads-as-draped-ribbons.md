# Roads are draped ribbons, not painted tiles

## The decision

A road is drawn as its own **geometry**: a ribbon mesh draped over the terrain,
running from each road tile's centre to the midpoint of every edge it shares with a
road neighbour. Roads are no longer painted into terrain colour, and no longer drawn
as instanced boxes.

The client builds this from the road grid it already has (`KnownWorld.RoadConditionAt`,
fed by the view's road rows). **Nothing changes on the server, and nothing new goes on
the wire** — this is presentation reading state that was already there.

## Why

**A tile is a square kilometre; a road is a line about thirty metres wide.** That ratio
is the whole problem, and it defeats both of the cheaper approaches:

- **Terrain vertex colour** (what this replaces). Terrain vertices sit on tile
  *corners*, so the finest mark the mesh can make is one whole tile. A road came out as
  a chain of 1 km blots — the "squares where roads are". No amount of palette work fixes
  a resolution limit.
- **A world-space splat/mask texture.** At a believable width a road is ~3% of a tile.
  Even at 32 texels per tile that is a single texel, which aliases into dashes, cannot
  express a crossroads, and carries no anchors for decoration. Sharp enough resolution
  would mean an absurd texture for the whole map.
- **Instanced flat boxes** (what the vertex colour replaced). A box per tile is a 220 m
  plank laid over a kilometre of ground; on any slope it floated at one end and sank at
  the other.

Geometry is the only option that holds a thin line, turns corners, follows the ground,
and gives models somewhere to stand.

### The connection rule

Every arm runs **tile centre → midpoint of a shared edge**. Two neighbouring road tiles
therefore end their arms at the *same point* on their common boundary. Roads meet
exactly, by construction — not approximately, and not because any code checked. This is
what makes the network hold together as tiles are revealed, built and lost in any order.

A tile with three or four arms *is* a crossroads. A tile with two is a through-road, one
is a spur. There is no junction special case, because there is nothing to special-case.

### Why they meet without a kink

Each arm is a cubic Bézier whose control point near the edge is pushed along the **edge
normal**, so every arm arrives perpendicular to the boundary it crosses. Both tiles' arms
are then parallel where they touch. Without this the two halves would meet at whatever
angle each happened to choose, putting a visible dog-leg on every tile line — the exact
grid artefact the change set out to remove.

### Why the line wanders

The sideways bend is chosen by sampling terrain height at several candidate offsets and
keeping the one that climbs least, so a road bends around a rise instead of charging over
it. On flat ground every candidate ties, so a deterministic per-arm hash breaks the tie —
otherwise every road on a plain would take the identical dead-straight line and the
network would look surveyed rather than worn.

The jitter is **deterministic** (hashed from tile and direction). A road must draw the
same on every client and after every reload, or it would visibly re-route whenever its
chunk rebuilt.

### A correctness fix this forced

`TerrainSampler.HeightAt` interpolated **bilinearly**, but the terrain mesh is two
triangles per tile split along the `i00→i11` diagonal. Those are different surfaces: they
agree at the four corners and nowhere else, and on a twisted tile
(`h00 + h11 != h01 + h10`) they diverge by tens of units mid-tile — far more than any
lift. Anything placed by bilinear hovered on one half of a sloped tile and sank into the
other.

`HeightAt` is now triangle-exact. That file's stated purpose is that there is exactly one
definition of where the ground is; bilinear was a second one wearing its name. Units and
structures on steep ground get more accurate placement as a side effect.

## Future expansion

- **Decoration is the reason samples are the public product.** `RoadNetwork` exposes
  position, tangent and half-width per sample — everything needed to line a road with
  fences, milestones, hedgerows or cart ruts, oriented to the road and draped on the
  land. The mesh builder is one consumer of those samples, not the point of them.
- **Surface by condition or region.** Width already grows with condition. Vertex colour
  per sample could carry mud → gravel → paved without touching the geometry.
- **Bridges and fords** are the known gap: a road crossing water currently drapes
  straight through it. The arm structure is the right place to detect that span and
  substitute a bridge model, because an arm already knows both its endpoints.
- **Junction patches are a square** at the tile centre. Adequate while width is modest;
  a fan matching the actual arm headings would be the upgrade if wide roads make the
  corners read as blunt.

## Acceptance

- Two adjacent road tiles show one continuous road across their shared edge, with no
  step in width and no visible angle change at the boundary.
- A road crossing a slope stays on the ground along its whole length and across its
  width — no edge buried, no edge floating.
- Three road tiles meeting form a T that reads as a junction, with no bare wedge.
- Roads on flat country are not all parallel straight lines.
- Switching to the Roads legibility layer still shows per-tile condition; the ribbon
  does not draw over it.
