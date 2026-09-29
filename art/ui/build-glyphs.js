// Glyph source of truth for the world UI (docs/world-ui.md, "The asset contract").
// Every shape is drawn on a 24-unit grid, white stroke of weight 2, round caps and joins,
// no fill (except tiny dots: pupil, pommel), no shadows or gradients. One SVG per name.
//
//   node build-glyphs.js [--out <unity Resources/UI dir>]
//
// Writes  art/ui/glyphs/<name>.svg     + <out>/Glyphs/<name>.png     (128 px)
// Rasteriser: @resvg/resvg-js, resolved from RESVG_DIR (or a node_modules beside this file).

const fs = require('fs');
const path = require('path');

const args = process.argv.slice(2);
const outIdx = args.indexOf('--out');
const OUT = outIdx >= 0 ? args[outIdx + 1]
  : path.resolve(__dirname, '../../../Art Of War(prod)/Assets/Resources/UI');
const RESVG_DIR = process.env.RESVG_DIR || __dirname;
const { Resvg } = require(require.resolve('@resvg/resvg-js', { paths: [RESVG_DIR] }));

const W = 2; // the single stroke weight across the whole set

// ---- shape vocabulary -------------------------------------------------------
const P = (d) => d;                                               // stroked path
const C = (cx, cy, r, fill = false) => ({ circle: [cx, cy, r], fill });

const arrowRight = (x1, x2, y) => [P(`M${x1} ${y} H${x2}`), P(`M${x2 - 3} ${y - 3} l3 3 -3 3`)];
const arrowDown = (x, y1, y2) => [P(`M${x} ${y1} V${y2}`), P(`M${x - 3} ${y2 - 3} l3 3 3 -3`)];
const arrowUp = (x, y1, y2) => [P(`M${x} ${y1} V${y2}`), P(`M${x - 3} ${y2 + 3} l3 -3 3 3`)];

const hammer = [P('M13 3 L21 11 L17 15 L9 7 Z'), P('M11 9 L4 20')];
const hammerMirror = [P('M11 3 L3 11 L7 15 L15 7 Z'), P('M13 9 L20 20')];
const pickaxe = [P('M3 21 L15.5 8.5'), P('M7 5 A12 12 0 0 1 19 17')];
const sack = [P('M9 9 C4 12 3 21 12 21 C21 21 20 12 15 9 Z'), P('M9 9 L8.5 5.5 H15.5 L15 9')];
const box = (x, y, w, h) => [P(`M${x} ${y} h${w} v${h} h${-w} Z`), P(`M${x} ${y + h / 2} h${w}`)];
const openBox = (x, y, w, h) => [P(`M${x} ${y} v${h} h${w} v${-h}`)];
const hull = [P('M3 15 H21 L18 20 H6 Z')];
const houseRoof = [P('M3 11 L12 3 L21 11'), P('M5 10 V21 H19 V10')];
const swordsCrossed = [
  P('M4 4 L16 16'), P('M16 16 L20 20'), P('M14 18 l4 -4'),
  P('M20 4 L8 16'), P('M8 16 L4 20'), P('M10 18 l-4 -4'),
];
const wheat = [
  P('M12 22 V6'),
  ...[15, 11, 7].flatMap(y => [
    P(`M12 ${y} c-3 0 -5 -2 -5 -4 c3 0 5 2 5 4`),
    P(`M12 ${y} c3 0 5 -2 5 -4 c-3 0 -5 2 -5 4`),
  ]),
];
const stone = [P('M4 15 L7 7 L15 5 L20 11 L18 18 L8 19 Z'), P('M15 5 L13 12 L18 18'), P('M13 12 L4 15')];
const bow = [P('M6 3 Q21 12 6 21'), P('M6 3 V21'), ...arrowRight(2, 17, 12)];
const shield = [P('M12 3 L4 6 V12 C4 17 8 20 12 21 C16 20 20 17 20 12 V6 Z'), P('M12 3 V21')];
const sword = [P('M3 3 H6 L17 14 L14 17 Z'), P('M11 20 L20 11'), P('M15.5 15.5 L20 20'), C(20.5, 20.5, 1.2, true)];
const cog = [C(12, 12, 3.5), ...[0, 45, 90, 135, 180, 225, 270, 315].map(a => {
  const r = a * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);
  const f = (v) => v.toFixed(2);
  return P(`M${f(12 + 7 * c)} ${f(12 + 7 * s)} L${f(12 + 9.5 * c)} ${f(12 + 9.5 * s)}`);
})];
const personLeft = [C(9, 7, 4), P('M3 21 v-2 a4 4 0 0 1 4 -4 h4 a4 4 0 0 1 4 4 v2')];

// A circuit: a clockwise loop of radius r round (12,12) from the top through `sweep`
// degrees, an arrowhead on the open end, and a stop dot at each angle in `stops`.
const circuit = (r, sweep, stops) => {
  const f = (v) => v.toFixed(2);
  const at = (deg) => { const a = deg * Math.PI / 180; return [12 + r * Math.cos(a), 12 + r * Math.sin(a)]; };
  const end = -90 + sweep;
  const [ex, ey] = at(end);
  const t = end * Math.PI / 180;
  const [tx, ty] = [-Math.sin(t), Math.cos(t)];            // direction of travel at the tip
  const barb = (s) => {                                      // 3.5 back from the tip, 40° off
    const a = Math.atan2(-ty, -tx) + s * 40 * Math.PI / 180;
    return P(`M${f(ex)} ${f(ey)} L${f(ex + 3.5 * Math.cos(a))} ${f(ey + 3.5 * Math.sin(a))}`);
  };
  return [
    P(`M12 ${12 - r} A${r} ${r} 0 ${sweep > 180 ? 1 : 0} 1 ${f(ex)} ${f(ey)}`),
    barb(1), barb(-1),
    ...stops.map(d => { const [x, y] = at(d); return C(+f(x), +f(y), 2, true); }),
  ];
};

// ---- the sets ---------------------------------------------------------------
const glyphs = {
  // activity, one per wire state
  'act-idle':     [P('M3 10 h7 l-7 9 h7'), P('M13 4 h6 l-6 7 h6')],
  'act-moving':   arrowRight(4, 20, 12),
  'act-working':  pickaxe,
  'act-building': hammer,
  'act-hauling':  sack,
  'act-waiting':  [P('M6 3 h12'), P('M6 21 h12'), P('M7 3 v3 l5 5 5 -5 v-3'), P('M7 21 v-3 l5 -5 5 5 v3')],

  // resources
  'wood':   [C(12, 9, 3.75), C(7.75, 16.5, 3.75), C(16.25, 16.5, 3.75), C(12, 9, 1, true), C(7.75, 16.5, 1, true), C(16.25, 16.5, 1, true)],
  'stone':  stone,
  'ore':    [P('M3 17 L8 8 L14 6 L21 12 L19 18 Z'), C(9, 13, 1.2, true), C(14, 10.5, 1.2, true), C(15, 15, 1.2, true)],
  'food':   wheat,
  'sword':  sword,
  'bow':    bow,
  'shield': shield,
  'cart':   [P('M4 8 H15 L17 15 H4 Z'), C(7, 18, 2.5), C(14, 18, 2.5), P('M15 8 L21 5')],
  'iron':   [P('M3 16 L6 8 H18 L21 16 Z'), P('M8 12 h3')],

  // vitals
  'age':        [P('M4 21 h16'), P('M5 13 h14 v8 h-14 Z'),
                 P('M5 16 c1.2 -1 2.3 -1 3.5 0 c1.2 1 2.3 1 3.5 0 c1.2 -1 2.3 -1 3.5 0 c1.2 1 2.3 1 3.5 0'),
                 P('M12 13 V9'), P('M12 7 c-1.5 -1.5 -1 -3 0 -4 c1 1 1.5 2.5 0 4 Z')],
  'health':     [P('M12 20 C6 15 3 12 3 8.5 A4.5 4.5 0 0 1 12 6.5 A4.5 4.5 0 0 1 21 8.5 C21 12 18 15 12 20 Z')],
  'power':      [P('M13 2 L5 13 h6 l-1 9 L19 10 h-6 Z')],
  'workers':    [...personLeft, P('M16 3.5 a4 4 0 0 1 0 7'), P('M21 21 v-2 a4 4 0 0 0 -3 -3.9')],
  'residents':  [...houseRoof, P('M10 21 V15 H14 V21')],
  'passengers': [...hull, C(12, 7, 2.5), P('M8 15 v-1 a4 4 0 0 1 8 0 v1')],
  'progress':   [{ circle: [12, 12, 9], fill: false, opacity: 0.35 }, P('M12 3 A9 9 0 1 1 3 12')],

  // verbs, one per OrderIssuer verb
  'move':           [P('M12 2 V22'), P('M2 12 H22'), P('M9 5 l3 -3 3 3'), P('M9 19 l3 3 3 -3'), P('M5 9 l-3 3 3 3'), P('M19 9 l3 3 -3 3')],
  'haul':           [P('M6 10 C2.5 12.5 2 20 8 20 C14 20 13.5 12.5 10 10 Z'), P('M6 10 L5.5 7 H10.5 L10 10'), ...arrowRight(14, 21, 15)],
  'unload':         [...openBox(4, 12, 16, 8), ...arrowUp(12, 12, 3)],
  'load':           [...openBox(4, 12, 16, 8), ...arrowDown(12, 3, 11)],
  'deliver':        [...box(2, 7, 10, 10), ...arrowRight(14, 21, 12)],
  'form-army':      [P('M5 3 V21'), P('M5 4 H19 L16 8 L19 12 H5')],
  'disband':        [...personLeft, P('M17 8 l4 4'), P('M21 8 l-4 4')],
  'put-to-work':    cog,
  'start-building': [...hammerMirror, P('M19 2 v6'), P('M16 5 h6')],
  'recall-crew':    [P('M4 5 H15 a3 3 0 0 1 0 6 H10'), P('M7 2 L4 5 L7 8'), C(12, 14.5, 2.2), P('M7.5 22 a4.5 4.5 0 0 1 9 0')],
  'train':          [P('M12 21 V8'), P('M5 10 H19'), C(12, 5, 2.5), P('M8 21 h8')],
  'equip':          [P('M8 4 L4 7 L6 10.5 L8 9.5 V20 H16 V9.5 L18 10.5 L20 7 L16 4 a4 2.5 0 0 1 -8 0 Z')],
  'craft':          [P('M3 7 H21 V10 H15 C15 13 16 15 18 16 V20 H6 V16 C8 15 9 13 9 10 H3 Z')],
  'loot':           [P('M3 10 V20 H21 V10'), P('M3 10 V9 a9 5 0 0 1 18 0 v1'), P('M3 10 H21'), P('M10 10 h4 v3 h-4 Z')],
  'clear-rubble':   [P('M3 21 v-5 l4 -4 5 5 -4 4 Z'), P('M9.5 14.5 L17 7'), P('M17 7 L20 4'), P('M18.5 2.5 L21.5 5.5')],
  'family':         [C(9, 6, 3), P('M4 21 v-3 a5 5 0 0 1 10 0 v3'), C(18, 12, 2), P('M15 21 v-2 a3 3 0 0 1 6 0 v2')],
  'board-boat':     [...hull, ...arrowDown(12, 3, 12)],
  'put-ashore':     [...hull, ...arrowUp(12, 12, 3)],
  'build':          [...houseRoof, P('M12 13 v6'), P('M9 16 h6')],
  'engage':         swordsCrossed,
  'cancel':         [P('M6 6 L18 18'), P('M18 6 L6 18')],
  'back':           [P('M15 5 L8 12 L15 19')],
  'confirm':        [P('M4 12 L10 18 L20 7')],
  'close':          [C(12, 12, 9), P('M9 9 l6 6'), P('M15 9 l-6 6')],
  // a standing order's verbs: walk a circuit of stops; set its engage and leash
  'patrol':         circuit(8, 300, [-90, 30, 150]),
  'tune':           [P('M3 8 H7.5'), C(10, 8, 2.5), P('M12.5 8 H21'), P('M3 16 H13.5'), C(16, 16, 2.5), P('M18.5 16 H21')],
  'repeat':         [P('M4 12 V10 a3 3 0 0 1 3 -3 H19'), P('M16 4 l3 3 -3 3'),
                     P('M20 12 v2 a3 3 0 0 1 -3 3 H5'), P('M8 14 l-3 3 3 3')],
  'undo':           [P('M9 14 L4 9 L9 4'), P('M4 9 H14 a6 6 0 0 1 0 12 H11')],
  'demolish':       [P('M3 11 L12 3 L15 5.7'), P('M5 10 V21 H10'), P('M12 21 L14 17 L11.5 14 L14 10'),
                     P('M19 13 V21 H15'), P('M18 5 l2 2'), P('M21 9 l1 1')],

  // marks: the one red place (the cursor slash) and the stall chip
  'invalid':        [C(12, 12, 9), P('M5.6 5.6 L18.4 18.4')],
  'starving':       [P('M3 12 H21 A9 7.5 0 0 1 3 12 Z'), P('M9 21.5 H15'), P('M9 4 l3 3 3 -3')],

  // vitals, cont.: the soil a claim works
  'soil':           [P('M3 15 H21'), P('M5 19 H9'), P('M12 19 H19'), P('M12 15 V9'),
                     P('M12 11 c-3 0 -5 -2 -5 -5 c3 0 5 2 5 5'), P('M12 9 c2 0 4 -2 4 -4 c-2 0 -4 2 -4 4')],

  // building kinds, for the build ring (SimVocabulary.KindName, kebab-case)
  'stockpile':      [...box(3, 13, 8, 8), ...box(13, 13, 8, 8), ...box(8, 3, 8, 8)],
  'tower':          [P('M7 21 V4 H9 V6 H11 V4 H13 V6 H15 V4 H17 V21'), P('M4 21 H20'), P('M12 9.5 V12.5'),
                     P('M10.5 21 v-3 a1.5 1.5 0 0 1 3 0 v3')],
  'castle':         [P('M3 21 V5 H5 V7 H7 V5 H9 V21'), P('M15 21 V5 H17 V7 H19 V5 H21 V21'), P('M9 11 H15'),
                     P('M10 21 V16 a2 2 0 0 1 4 0 V21'), P('M2 21 H22')],
  'lumber-camp':    [P('M8 3 L3 13 H13 Z'), P('M8 13 V20'), P('M13 21 L19 8'), P('M17.5 7 L21.5 9 L20 12.5 L17 11')],
  'quarry':         [P('M2 6 H5 V10 H8 V14 H16 V10 H19 V6 H22'), P('M9 17 H15 V21 H9 Z')],
  'mine':           [P('M2 21 C5 10 9 5 12 5 C15 5 19 10 22 21'), P('M8 21 V13 H16 V21'), P('M7 13 H17'), P('M2 21 H22')],
  'farm':           [P('M3 21 H21'), P('M7 12 H17'), P('M3 21 L7 12'), P('M21 21 L17 12'), P('M9.5 21 L10.5 12'),
                     P('M14.5 21 L13.5 12'), P('M12 9 V4'), P('M12 6.5 c-2 0 -3.5 -1.5 -3.5 -3.5 c2 0 3.5 1.5 3.5 3.5')],
  'house':          [...houseRoof, P('M16 6.5 V3 H18 V8.3'), P('M8 21 V15 H11 V21'), P('M14 14 h3 v3 h-3 Z')],
  'dock':           [P('M3 11 H21'), P('M6 11 V17'), P('M12 11 V17'), P('M18 11 V17'),
                     P('M2 20 q2.5 -2 5 0 t5 0 t5 0 t5 0')],
  'school':         [P('M12 6 C9 4 5 4 3 5 V19 C5 18 9 18 12 20 C15 18 19 18 21 19 V5 C19 4 15 4 12 6 Z'), P('M12 6 V20')],
  'barracks':       [P('M4 21 V12 H20 V21'), P('M3 12 L12 8 L21 12'), P('M12 8 V2'), P('M12 2.5 H17 L15.5 4 L17 5.5 H12'),
                     P('M10 21 V17 H14 V21')],
  'lodge':          [P('M2 12 C5 7 9 5 12 5 C15 5 19 7 22 12 C19 17 15 19 12 19 C9 19 5 17 2 12 Z'), C(12, 12, 3.5), C(12, 12, 1.2, true)],
  'canal':          [P('M2 7 H22'), P('M2 17 H22'), P('M4 12 q2 -2 4 0 t4 0 t4 0 t4 0')],
  'wall':           [P('M4.5 21 V5 H7.5 V8 H10.5 V5 H13.5 V8 H16.5 V5 H19.5 V21 Z'), P('M4.5 14 H19.5'), P('M9 8 V14'), P('M15 8 V14'),
                     P('M7 14 V21'), P('M12 14 V21'), P('M17 14 V21')],
  'gate':           [P('M3 21 V4 H21 V21'), P('M7 21 V13 a5 5 0 0 1 10 0 V21'), P('M10 21 V8.5'), P('M14 21 V8.5'), P('M7 16 H17')],
  'smelter':        [P('M5 21 V9 L8 6 H13 V2 H16 V6 L19 9 V21 Z'), P('M9 21 V17 a3 3 0 0 1 6 0 V21'),
                     P('M12 20.5 c-1.5 -1 -1 -2.5 0 -3.5 c1 1 1.5 2.5 0 3.5 Z')],
  'workshop':       [C(12, 12, 8.5), C(12, 12, 2), ...[0, 60, 120, 180, 240, 300].map(a => {
                      const r = a * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);
                      return P(`M${(12 + 2 * c).toFixed(2)} ${(12 + 2 * s).toFixed(2)} L${(12 + 8.5 * c).toFixed(2)} ${(12 + 8.5 * s).toFixed(2)}`);
                    })],
  'smithy':         [P('M3 8 H17 C19 8 21 7 22 5 C21 9 19 11 16 11 V14 H14 V17 H17 V20 H7 V17 H10 V14 H8 V11 C6 11 4 10 3 8 Z')],
};


// ---- emit -------------------------------------------------------------------
function prim(p) {
  if (typeof p === 'string') return `<path d="${p}"/>`;
  if (p.circle) {
    const [cx, cy, r] = p.circle;
    const op = p.opacity != null ? ` opacity="${p.opacity}"` : '';
    return p.fill
      ? `<circle cx="${cx}" cy="${cy}" r="${r}" fill="#fff" stroke="none"${op}/>`
      : `<circle cx="${cx}" cy="${cy}" r="${r}"${op}/>`;
  }
  throw new Error('bad primitive');
}
function svg(prims) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="${W}" stroke-linecap="round" stroke-linejoin="round">`
    + prims.map(prim).join('') + `</svg>\n`;
}
function emit(set, srcDir, outDir, px) {
  fs.mkdirSync(srcDir, { recursive: true });
  fs.mkdirSync(outDir, { recursive: true });
  for (const [name, prims] of Object.entries(set)) {
    const s = svg(prims);
    fs.writeFileSync(path.join(srcDir, `${name}.svg`), s);
    const png = new Resvg(s, { fitTo: { mode: 'width', value: px } }).render().asPng();
    fs.writeFileSync(path.join(outDir, `${name}.png`), png);
  }
  return Object.keys(set).length;
}
const nG = emit(glyphs, path.join(__dirname, 'glyphs'), path.join(OUT, 'Glyphs'), 128);
console.log(`glyphs ${nG} -> ${path.join(OUT, 'Glyphs')}`);
