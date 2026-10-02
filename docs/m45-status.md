# M45 status: haul route UX (amounts you can see and change)

**Design:** https://claude.ai/artifact/VQhWr77ukPDx8NfndFtzbX (playtest markup + proposals A–D).
**Decision docs:** `docs/hauling-queue-and-routes.md` (addendum 2026-10-01). Started 2026-10-01.
**Rule:** no commits unless asked. The user tests by hand. Focused test filters while
iterating; the full suite once at the end.

## Why

Playtest 2026-10-01 (three hours to day X): "not having options to pick how much to pickup
and drop off is annoying. we need this for routes especially." The server already took a
per-rule percent; the client hid it behind a slider that collapsed to a dot, froze it on
commit, and could not change a running route at all.

## Locked with the user (2026-10-01)

- Amounts read in units per carrier ("25 wood each"); the percent stays as small print.
  The server keeps the percent.
- A crew is the units the player named, so **every named member carries** at their own
  capacity, except Soldiers and Archers, who stay escort (the M36 decision). Replaces
  "only Hauler-role members carry".
- Live edit is a server intent that keeps the crews (not a clear-and-rebuild).
- The stop card replaces the wheel for rules; the wheel keeps verbs on units/buildings.
- Every server ask on the design page is built: per-unit capacity and route id on the
  unit view, route names, update intents for routes and jobs, and the last serve per crew.
- Assets are built in Blender in the existing ornate-glyph pipeline, nothing generic.

## Phases

| Phase | Scope | Status |
|---|---|---|
| S1 | Sim: carriers rule; `HaulRoute.Name` + `Revision`; `RouteCrew.LastServe`; `UpdateHaulRouteIntent`, `RenameHaulRouteIntent`, `UpdateHaulJobIntent`; serve fenced on revision; snapshot v46; intent JSON | done (`HaulRouteEditTests` 12, `IntentJsonTests` +1) |
| S2 | Wire: `UnitDto.CargoCapacity` + `HaulRouteId`; `HaulRouteDto.Name`; `HaulCrewDto` last serve | done (`HaulWireTests` +1) |
| C1 | Client wire mirror + intent factories | done |
| C2 | Bubble slider fix (track collapsed to a dot) | done (`min-width` on `.wui-slider`; leash and patrol tuning still use it) |
| C3 | Stop card replaces the stop wheel; pill with Send and a two-press Discard | done (`StopCard`, `RoutePlanner` rewrite) |
| C4 | Route on the map (loop + stop pins) and the route card (rename, edit stops, join at a chosen stop, end route) | done (`HaulRouteOverlay`, `RoutePins`, `RouteCard`, `UiTyping`); crew tokens folded into the pins (lit = a crew heads there) |
| C5 | Queue amount presets + "Change…" on job rows | done (`JobCard`, `JobPlanner.BeginEdit`) |
| A1 | Blender assets: paired pick-up / drop glyphs, stop pin glyph, card frame if the existing frames don't fit | done: `rule-pickup`, `rule-drop`, `route-stop` (plain + ornate) from `WorldWheel_Ornate.blend` via `_scripts/route_rule_glyphs.py`; the cards reuse the existing `panel_frame` |

## Baseline

- Full suite before this milestone (2026-10-01, after the retask change): 1573 passed, 3 failed
  (two raid lab tests red by decision; `ArmTests.Forge_RaisesASmithy` red from M44's
  Hills/Mountain swap, another session's work).

## Not done / for the user

- Nothing in the client has been run in Play. Compile-checked only (`_AowTypeCheck`, `_AowWireBoundary`).
- Unity has to import the six new glyph PNGs (metas are written; focus the editor once).
- The old corner HUD's Haul page (H) shows names but has no Change button.
- Escort engagement, per-job source sets and trade stops remain deferred (decision doc).

## Result

- Full suite 2026-10-01 after M45 (with M44's concurrent work on disk): Sim.Tests 1608 passed,
  0 failed; Sim.Persistence.Tests 60 passed, 0 failed.
