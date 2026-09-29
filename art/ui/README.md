# UI art sources

The artist's half of `docs/world-ui.md`, "The asset contract". Everything here is source;
the client only ever loads the PNGs it produces.

| Here | Ships as | Spec |
|---|---|---|
| `glyphs/*.svg` (72) | `Assets/Resources/UI/Glyphs/*.png`, 128×128 | 24-unit grid, white stroke weight 2, round caps, no fill, no shadow |
| `fonts/Inter-*.ttf` | `Assets/Resources/UI/Fonts/Body.ttf`, `BodyBold.ttf` | Inter 4.1, SIL Open Font License (`LICENSE-Inter.txt`) |

The SVGs are *generated*, not hand-drawn: `build-glyphs.js` holds every shape as path data
on the 24 grid and rasterises with resvg. Edit the shape there, not the SVG.

```
cd art/ui
npm install
npm run build              # writes into ../../../Art Of War(prod)/Assets/Resources/UI
npm run build -- --out X   # or anywhere else
```

Names are the contract. Activity `act-<state>` per wire state; resources by wire name;
vitals `age health power workers residents passengers progress`; verbs by `OrderIssuer`
verb name, kebab-case (`form-army`, `put-to-work`, `clear-rubble`, `board-boat`,
`put-ashore`). A mode pill shows its verb's glyph, so there is no separate mode set.

`Title.ttf` is still Palatino Linotype copied from Windows and cannot ship. It is a
serif choice for the parchment HUD, which the world UI retires, so it was left for that
decision rather than swapped blind.
