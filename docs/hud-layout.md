# HUD layout: corners for the facts, one card for the thing you picked

## The decision

The production client's HUD is a fixed set of slate fixtures in the corners — realm
strip top-left, date and pace top-right, minimap bottom-left, a dock of six pages
along the bottom, the newest log lines bottom-right — and ONE context card on the
right that exists only while something is selected or a gesture is running. The card
holds both the description of the selection and the verbs that apply to it.

Every element shows only what the wire carries. Where the art board suggests a
feature the wire does not carry yet, the slot is drawn as a **coming soon** row that
names what will go there and what it needs.

## Why

- **The landscape is the star.** The art board's frames put a strip in each top
  corner, a minimap, and nothing else until something is selected. A HUD that fills
  the bottom third of the screen with panels defeats the diorama it is drawn over.
- **One card, not two.** The first client split the inspector (left) from the orders
  bar (bottom-centre). The player read a building on one side and found its verbs on
  the other, and the two disagreed about which building they meant more than once.
  Description and verbs are the same question — "what is this and what can I do" —
  so they are one card.
- **No realm-wide resource bar.** There is no kingdom stockpile in the sim; goods
  live in buildings. The board's five-icon resource strip would have been a lie. The
  realm strip shows what `ViewDto` asserts about the realm: population, castle food
  and its runway, the crown, and wars declared or coming.
- **Coming-soon slots, not empty space or invented data.** The user's rule: what is
  shown is what is on the wire; anything else says so and names its future. A slot
  that teaches the shape of the game beats a silent gap.
- **Badges, not icon glyphs.** The runtime font's symbol coverage is unknown, and
  a missing glyph is a box. A rounded badge with the hotkey letter reads as an icon,
  never breaks, and doubles as the key hint (B P Y L M O).

Alternatives ruled out:

- **A modal command bar with hotkeys per mode** (the old debug client's Esc/B/H/J
  modes). Modes hide state; the gesture state machines in `PlayerController` already
  carry the same information and the card can just say what the next click does.
- **UXML for structure.** The panels are composed in C# because their contents are
  functions of the selection and the wire; a static UXML tree would need rebuilding
  from code anyway.
- **A second camera for the minimap.** A 252×252 texture painted from the known
  world is cheaper, and by construction cannot reveal more than the player knows.

## The skin (same day, second pass)

The first pass laid the panels out but wore Unity's default UI Toolkit chrome:
bevelled grey buttons, the stock font, a fat scrollbar, flat fills. The user's
verdict was "essentially the same", and it was right. The skin is now:

- **An authored stylesheet**, `Assets/Resources/UI/Aow.uss`, loaded by `Hud` at
  runtime with `Resources.Load` so nothing in the scene needs assigning. Every
  visual is a class; C# sets an inline style only for data (a bar's width, a
  semantic text colour). Hover, press, disabled and the card's slide-in are USS
  transitions.
- **A generated glass texture** (`panel.png`, `button.png`, 9-sliced) with a top
  highlight and a vertical gradient, rendered by a small script in the session
  scratchpad. Regenerate rather than hand-edit.
- **Vector icons drawn with Painter2D** (`HudIcons`), designed on a 24-unit grid.
  No font glyphs (coverage unknown), no bitmaps (sharp at one size only).
- **Fonts as stand-ins.** Palatino Linotype Bold for titles, Segoe UI for body,
  copied from the Windows font folder into `Resources/UI/Fonts`. These are licensed
  for the machine, NOT for redistribution in a shipped game. Before shipping, swap
  in open-licence equivalents (Cinzel or Cormorant for titles, Inter for body) at
  the same paths; nothing else changes.

## Future expansion

- **Theme as asset.** `Aow.uss` is the only file an art pass needs to touch;
  `HudTheme` keeps just the semantic colours the C# side reads.
- **New dock pages** slot in as a `Dock.Page` and a hotkey; the card and the strips do
  not change.
- **Wire growth.** `orders`, `scoutReports` and `graves` already ride the view
  undeclared; declaring them fills the coming-soon slots on the People and Realm
  pages without moving anything.
- **Overview markers** (banners over settlements at altitude) belong to the world
  renderer, not the HUD; the Map page's slot points at them.

## Acceptance

- With nothing selected, the only fixtures on screen are the two top strips, the
  minimap, the dock and at most five fading log lines.
- Selecting a building shows its state as bars and every applicable verb on one
  card; dimmed verbs explain themselves on hover.
- Clicking the minimap or a log line that names a tile flies the camera there.
- No number on screen is derived from anything but the wire.

## Update 2026-09-17 — the hotbar, top centre

A row of eight slots between the two top strips. Each slot binds a KIND of thing — a
role of person or a kind of building — never a specific one. Its number key, or a
click, selects the nearest thing of that kind to the camera and flies there; idle people
are preferred over busy ones, and pressing again skips what is already selected, so
repeated presses walk outward through everything you own of that kind. Ctrl+number
binds the current selection's kind, clicking an empty slot opens a picker, right-click
clears. Bindings live in PlayerPrefs (a per-player convenience, not game state).

Why kinds rather than control groups: the substrate makes people interchangeable
within a role — "a builder" is the unit of thought, not "builder #17" — and what the
player keeps asking is "where is a free one". A classic control group would go stale
every time the sim retasked or replaced someone.

Caveats: the number row belongs to the strategic view's layers while that view is up
(1–4), so the hotbar is inert then. Boats are not offered, since the pivot cannot sit on
water yet. The bar reads the view and moves the camera; it never sends an intent.
`Hud/Hotbar.cs`.

## Update 2026-09-17 — the Machine page

A seventh dock page, *Machine* (K), for standing orders: list plus form on one page, form-first by the user's call. Details in `docs/automation-substrate.md` (update of the same date).

## Update 2026-09-17 — superseded in progress by the World UI

The corner-fixtures-plus-one-card layout is being replaced by an in-place radial
interface with no fixed chrome: see `docs/world-ui.md`. That work is a separate layer
and this HUD stays untouched and playable until the new layer covers every verb the
card offers. Nothing above is edited; the history stands.
