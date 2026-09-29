# M40 status: salvage and the chart's world marker

**Decision doc:** `docs/salvage.md`. Built 2026-09-24.
**Rules:** no commits unless asked; tests green; numbers are config knobs.

## What was built

| Part | Scope | Status |
|---|---|---|
| Server | `HaulJobKind.Salvage`, the source rule (charted or seen, nobody's), crew 1–6, mixed "take everything" loads, the job ending on an empty arrival, the driver keeping the crew at work without reading the source | done, `SalvageJobTests` 7 + `IntentJsonTests` +1 |
| Client: salvage | drag from a charted tile, cache, or pile to your building; "Salvage — bring it all home…" on the tile / cache wheel then click the building; the tile bubble shows the chart entry and any pile; Haul page + bubble rows; Tab-view chart chips | built, `_AowTypeCheck` + `_AowWireBoundary` clean, **not run in Play** |
| Client: gold fog | `ChartGlow` (per-tile gold / ember map, rebuilt only when the chart changes) + one read per pixel at the end of the fog march; dials `ChartGold`, `ChartEmber`, `ChartGlowTint`, `ChartGlowEmission` on `FogRenderer` | built, C# compile-checked; **the shader is not compiled until Unity imports it, and the look is untuned** |

## Baseline

- Before: 1317 + 54. After: 1324 + 55, 0 failed.

## Notes

- **No editor step.** The new `FogRenderer` fields take their code defaults on the
  existing component, and Unity recompiles the shader on import.
- **Perf check:** the F9 perf harness (`PerfLogs/*.csv`) before and after with a find
  charted. Set `ChartGlowTint` and `ChartGlowEmission` to 0 to switch the read off.
- **Not covered:** the stacked-plane fog stand-in (`Aow/FogVolume`, off while the volume
  runs) has no glow.
