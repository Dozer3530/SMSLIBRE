# SMSLIBRE — Phased Project Plan

Goal: a native Linux application with **full feature parity** to Ag Leader SMS
Advanced, **reusing SMS's own .NET code** wherever it is portable and
reimplementing only the native compute engine. Scope is defined by
[`SMS_FEATURE_INVENTORY.md`](SMS_FEATURE_INVENTORY.md) (835 features). Reuse vs
rebuild is quantified in [`STAGE_SALVAGE_LEDGER.md`](STAGE_SALVAGE_LEDGER.md).

Each phase lists **steps · deliverables · testing · exit criteria**. Phases ship
independently — the app is useful at the end of each.

---

## Testing strategy (applies to every phase)

| Layer | How | Runs where |
|---|---|---|
| Unit | xUnit on `SmsLibre.Core` (rasterizer, cleaning, PNG, classification) | everywhere, incl. CI |
| Import integration | xUnit driving SMS's real ADAPT engine on sample data; **guarded** — skips when the SMS install/data is absent | analysis box (+ optional self-hosted) |
| Render golden-image | hash/compare rendered PNGs against approved baselines | everywhere |
| UI smoke | `SmsLibre.Shot` headless-renders the window to PNG; assert it isn't blank | everywhere |
| Manual | run the GUI on Linux Mint each phase; visual check vs SMS screenshots | Mint |

Rule: **no feature is "done" without a test** (unit or golden-image) and a note
in `FEATURE_PARITY.md` flipped to ✅. CI (GitHub Actions) builds the solution and
runs the non-integration tests on every push.

---

## Phase 0 — Analysis & feasibility ✅ DONE
Stages 1–3, salvage ledger, feature inventory, DB schema, decompiled reference.
Exit: architecture chosen (native .NET + Avalonia, reuse ADAPT), scope known.

## Phase 1 — Vertical slice ✅ DONE
ISOXML import (SMS's ADAPT) → management tree → native yield render → legend/
stats → PNG export, in an Avalonia shell; pluggable importer registry; xUnit +
headless screenshot.
Exit met: `app/` builds, 11 tests green, GUI verified on real field 1516.

## Phase 2 — Import breadth  (the biggest copy-paste win)
**Goal:** read every source format SMS does, reusing its plugins.
- Steps:
  1. Investigate John Deere path: does `JohnDeere.CommonDataFramework` expose an
     ADAPT `IPlugin`, and does it P/Invoke the native `api-interop.dll`? (If
     native+Windows-only, decide: bridge vs reimplement the GS SQLite reader.)
  2. Wire importers behind `IFieldImporter`: JD, CNH Voyager2, Precision
     Planting, Climate, Trimble, AgGateway ADM.
  3. Generic **Shapefile** importer (own code; no ADAPT) for boundaries/points.
  4. Format auto-detection per Vault subfolder; import a whole Vault at once.
- Deliverables: all the user's real fields appear in the tree.
- Testing: one guarded integration test per format (sample dataset each);
  registry unit tests for detection.
- Exit: importing the full `Data_2/Vault` populates growers/farms/fields with
  yield + as-applied layers; per-format tests green.

## Phase 3 — Data model & catalogue
**Goal:** SMS's organizational data + our own persistence.
- Steps:
  1. Read `Main.mdb` (JET) via a managed JET reader; decode numeric property-IDs
     with `PropertyDefinition_Table` + `FactoryIds` (see findings).
  2. Merge catalogue tree with imported datasets; reconcile identities.
  3. Local **SQLite** store for SMSLIBRE's own workspace (import once, reopen fast).
  4. Port reusable `ALMS_DatasetsMobileDesktop` / `Units` / `Coordinates` logic.
- Testing: schema-mapping unit tests; round-trip store tests.
- Exit: open the existing SMS dataset read-only + a persistent native workspace.

## Phase 4 — Mapping & visualization depth  (89 features)
- Steps: multiple layer types (as-applied, as-planted, elevation, moisture);
  classification methods (equal-interval, std-dev, manual breaks, custom colors);
  legend editor; boundary overlay; multi-layer compositing & ordering;
  background imagery via GDAL rasters; on-screen zoom/pan/measure, scale/north.
- Testing: golden-image per layer type & classification method.
- Exit: a field's common layers render and match SMS visually within tolerance.

## Phase 5 — Editing & cleaning  (117 features — hardest reimplementation)
- Steps: yield cleaning (flow delay/latency, swath overlap, min/max flow &
  velocity, moisture, statistical filters) — reimplemented from the ISO/agronomy
  literature and validated against SMS output on shared datasets; boundary
  create/edit; region/point/flag editing; calibration.
- Testing: compare cleaned output distributions to SMS's exported cleaned data
  (we have both raw Vault + SMS-exported shapefiles for the same field).
- Exit: cleaned yield map is statistically close to SMS's for the test fields.

## Phase 6 — Analysis  (56 features)
- Steps: zonal statistics, summaries, product/variety comparison, multi-year
  overlay & cross-reference, profit/cost, field trials / replicated strips,
  batch analysis. (Native `ALA_Analysis` math is reimplemented; GDAL/GEOS help.)
- Testing: numeric unit tests with hand-computed expected values.
- Exit: core analyses produce correct numbers + maps.

## Phase 7 — Reports, export & prescriptions  (46 + 36 + 13 features)
- Steps: summary reports & charts; PDF export (a PDF lib, not Amyuni); map image
  export ✅; data export (Shapefile, GeoJSON, ISOXML via ADAPT, controller cards);
  variable-rate prescription create/edit + controller export.
- Testing: exported-file schema/round-trip tests; PDF smoke tests.
- Exit: produce the report/export types SMS does for a field.

## Phase 8 — Polish & Linux delivery
- Steps: preferences/units/projections, workspace persistence, performance on
  large datasets (GPU/tiled rendering if needed), Linux packaging (AppImage/Flatpak/
  `dotnet` self-contained), UI theming to resemble SMS.
- Testing: performance benchmarks; packaging smoke test on Mint.
- Exit: installable on Linux Mint; day-to-day workflows match SMS.

---

## Sequencing notes
- Phases 2–4 are mostly **reuse/port** (fast, high value) — do them first.
- Phase 5 is the **native reimplementation** crux — expect the most effort and
  the closest validation against SMS output.
- Everything after Phase 1 is independently shippable; prioritize by the
  features you actually use day to day.
