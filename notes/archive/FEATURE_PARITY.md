# SMSLIBRE — Feature Parity Roadmap

Goal (user): **every feature SMS Advanced has**, natively on Linux. SMS is a
25-year commercial product, so this is a long roadmap; this file makes "every
feature" concrete and tracked. Status keys: ✅ done · 🟡 partial · ⬜ todo.

Reuse strategy per the salvage ledger: **reuse SMS's real .NET code** where it's
portable (ADAPT import, `AgLeader.Shared`), **reimplement** what's locked in the
native C++ core (cleaning, analysis, rendering). Legend below marks which is which:
🔁 = reuses SMS code · 🔧 = clean-room reimplementation.

> **The exhaustive feature list now exists:** `SMS_FEATURE_INVENTORY.md` — all
> **835 help topics** extracted from `ALMapping.chm`, grouped by area (89 mapping,
> 117 editing/cleaning, 56 analysis, 46 reports, 54 setup, 37 import, 36 export,
> …). That is the real scope of "every feature." The status list below is the
> high-level rollup; the inventory is the itemized checklist.

## 1. Data management & import
- ✅ 🔁 ISO 11783 (ISOXML) import — via SMS's `AgGateway.ADAPT.ISOv4Plugin`
- ⬜ 🔁 John Deere (GS2/GS3/GS4, EIC) — ADAPT plugins present, wire them in
- ⬜ 🔁 Case IH / New Holland (Voyager2), AGCO, Precision Planting, Climate, Trimble, Raven — ADAPT plugins present
- ⬜ 🔁 Ag Leader native formats (AgLeaderFile — native DLL; needs bridge or reimpl)
- ⬜ 🔧 Shapefile / generic CSV import
- ✅ 🔧 Management tree (Grower → Farm → Field → Dataset)
- 🟡 🔧 Read `Main.mdb` catalogue (schema mapped; reader not yet wired) — [[STAGE1-3_FINDINGS]]
- ⬜ 🔧 File/database management, backup & restore, data repair

## 2. Mapping & visualization
- ✅ 🔧 Yield map rendering (classified points) — native `YieldRaster`
- ✅ 🔧 Quantile legend + class table
- 🟡 🔧 Legend editing (class count only; no manual breaks/colours yet)
- ⬜ 🔧 Other layer types: as-applied, as-planted, elevation, moisture, singulation, …
- ⬜ 🔧 Multiple layers / overlays, layer ordering, transparency
- ⬜ 🔧 Background imagery (GDAL rasters, tiles), basemaps
- ⬜ 🔧 Field boundary overlay, zoom/pan/measure, north arrow/scale on screen
- ⬜ 🔧 Themes / equal-interval / std-dev / manual classification methods

## 3. Editing & data cleaning
- 🟡 🔧 Yield cleaning (basic: drop zeros/outliers, clip) — SMS's real cleaner is
  native `ALP_PreprocessorDll` (not portable, not readable); refine toward parity
- ⬜ 🔧 Delay/offset, flow calibration, swath-width correction, overlap removal
- ⬜ 🔧 Boundary create/edit, region/polygon editing, point/flag editing

## 4. Analysis
- ⬜ 🔧 Spatial/zonal statistics, summary by field/zone
- ⬜ 🔧 Profit & cost analysis, product comparison
- ⬜ 🔧 Multi-year / multi-layer cross-reference, trials & replicated strips
- ⬜ 🔧 Batch analysis
  (SMS's analysis math lives in native `ALA_Analysis` — reimplement from domain
   knowledge/standards; the decompiled bodies are machine code, not readable C#)

## 5. Prescriptions & controller I/O
- ⬜ 🔁/🔧 Create/edit variable-rate prescriptions
- ⬜ 🔁 Export to controller formats (ADAPT export plugins exist)

## 6. Reports & output
- ⬜ 🔧 Report designer, summary reports, charts
- ✅ 🔧 Export map as image (PNG) — native `PngWriter`
- ⬜ 🔧 Print / PDF export (SMS uses Amyuni; use a PDF lib instead)
- ⬜ 🔁 Export: Shapefile, GeoJSON, ISOXML (ADAPT export), controller cards

## 7. Cloud / integration / misc
- ⬜ 🔁 AgFiniti cloud sync (`AgLeader.Shared.Web` reusable; needs account/API)
- ⬜ 🔧 Weather, scouting, soil sampling, tissue sampling modules
- ⬜ 🔧 Units, projections, preferences, layout/workspace persistence
- n/a Licensing/activation — not needed (personal use)

## Done this iteration (the vertical slice)
Import (ISOXML via SMS's ADAPT) → management tree → native classified yield
render → legend/stats → PNG export, in a native Avalonia app (`app/`). This
proves the architecture end-to-end; the rest of the list is breadth on top of it.
