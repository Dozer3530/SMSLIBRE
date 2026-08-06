# SMSLIBRE — Stage 4: Target Architecture

Chosen direction (user decision): **clean-room Linux tool** that reads the same
data SMS does, *not* a literal port of SMS and *not* Wine. Rationale is in
[`STAGE1-3_FINDINGS.md`](STAGE1-3_FINDINGS.md): SMS's core is native C++/CLI
machine code that does not decompile to usable source, and its UI is WPF
(Windows-only). Rebuilding those is a multi-year effort; reading the (largely
open) data formats is not.

## Stack

| Concern | Choice | Why |
|---|---|---|
| Language | **Python 3.12+** | Fastest path; the whole geospatial stack has Linux wheels; already set up on both machines |
| Vector/geo I/O | **GeoPandas + pyogrio (GDAL)**, Shapely, pyproj | GDAL is the same engine SMS bundles; native on Linux |
| Raster/imagery | **rasterio / GDAL** | Handles GeoTIFF, MrSID, JP2 — the imagery SMS stores |
| DB (catalogue) | **mdbtools / Jackcess** to read `Main.mdb`; **SQLite** for our own store | JET4 data pages are unencrypted; no SQL Server needed. PostgreSQL optional later |
| Vendor formats | **AgGateway ADAPT** (open source .NET, runs on Linux) *or* reimplemented readers | ISOXML/JD/CNH parsing already solved in ADAPT |
| UI | **PySide6 / Qt** | Cross-platform native UI to replace WPF; Matplotlib canvas embeds cleanly |
| Plotting | **Matplotlib** (now) → optional GPU later | Fine for 30k–100k points; revisit if perf demands |

> Original brief suggested "PostgreSQL or SQLite." **SQLite** is the right
> default (single-user desktop, zero-admin, matches SMS's own embedded-DB model).
> PostgreSQL stays an option if multi-user/AgFiniti-style sync is ever wanted —
> and note SMS already bundles `libpq`, hinting it uses Postgres somewhere.

## Data-flow (target)

```
            ┌──────────────── SMS data on disk (read-only) ────────────────┐
            │  Vault/  (ISOXML, Shapefile, JD SQLite, vendor blobs)         │
            │  ProgramData/PointData/  (proprietary — later)                │
            │  Main.mdb  (JET catalogue: Grower→Farm→Field→Dataset index)   │
            └───────────────────────────┬──────────────────────────────────┘
                                        │  readers (open formats first)
                                        ▼
                      ┌───────────── smslibre core ─────────────┐
                      │  models: Grower/Farm/Field/Dataset/Layer │
                      │  io:     isoxml, shapefile, gs_sqlite,   │
                      │          jet_catalog (property-id decode) │
                      │  geo:    reproject (UTM), classify, clip  │
                      └───────────────────┬──────────────────────┘
                             ┌────────────┴────────────┐
                             ▼                          ▼
                   PySide6/Qt viewer          headless export (PNG/GeoTIFF/CSV)
```

## Build order (incremental, each independently useful)

1. **PoC (done):** boundary + yield map from Shapefiles → PNG + Qt viewer.
   `smslibre/poc/`.
2. **ISOXML reader:** parse `TASKDATA.XML` + binary `TLG` time logs → the same
   `FieldData` the PoC renders. Removes the "SMS must export a shapefile first"
   dependency for AGCO/NH/ISO data.
3. **JET catalogue reader:** open `Main.mdb`, decode numeric property-IDs, build
   the Grower→Farm→Field→Dataset tree; drive a browser panel in the UI.
4. **JD GS_Database (SQLite) reader:** the largest chunk of this user's data.
5. **More layers & operations:** as-applied, guidance lines, boundaries editing,
   imagery (GDAL rasters), simple analysis (zonal stats).
6. **Proprietary formats** (`PointData`, `.spf/.tso`) only if a needed dataset
   exists *only* in that form — otherwise re-derive from the standard sources.

## Non-goals (for now)

WPF-equivalent UI parity, SMS's report designer, licensing/AgFiniti sync, and
reimplementing the native spatial engine. The aim is a lean tool that does the
specific things the user needs on Linux, not a feature-clone of SMS.
