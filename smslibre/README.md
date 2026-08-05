# SMSLIBRE

A clean-room, **Linux-native** reader/viewer for Ag Leader SMS field data.

It does **not** reuse SMS's own code. It reads the same *open* formats SMS stores
data in (ESRI Shapefile, ISO 11783 ISOXML, SQLite, and Access/JET via mdbtools),
using GDAL/GeoPandas + Qt. The decompiled C# under `../analysis/` is a reference
spec only. See [`../notes/STAGE1-3_FINDINGS.md`](../notes/STAGE1-3_FINDINGS.md)
for why this approach (rather than porting SMS) is the right one.

## Status

Stage 5 proof of concept only: **import one field boundary + render its yield
map.** This proves the whole pipeline (read → reproject → classify → render →
Qt UI) on open formats, without touching SMS's WPF UI or native C++ core.

## Run it (Linux Mint or Windows)

```bash
python -m venv .venv
. .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r smslibre/requirements.txt

# Headless PNG using the bundled sample field (Smart Farm "15-16", 2023 harvest):
python -m smslibre.poc --out yieldmap.png

# Interactive Qt window:
python -m smslibre.poc --gui

# Your own data:
python -m smslibre.poc \
    --boundary path/to/boundary.shp \
    --yield    path/to/yield_points.shp \
    --yield-col Yld_Vol_Dr --units "bu/ac" --gui
```

The yield column is auto-detected when omitted. `--units` is just a label — the
raw values carry no unit metadata, so set it to whatever the export actually is
(the bundled sample's `Yld_Vol_Dr` looks like bu/ac given a ~108 mean).

## Layout

```
smslibre/
  poc/
    yieldmap.py   core: load boundary+yield, reproject to UTM, classify, render
    viewer.py     PySide6/Qt window embedding the map (seed of the real UI)
    __main__.py   CLI (python -m smslibre.poc)
  requirements.txt
```

## Where this goes next

The PoC intentionally reads pre-exported shapefiles. The path to a real tool,
in rough order:

1. **Read straight from the Vault** — parse ISO 11783 ISOXML (`TASKDATA.XML` +
   binary `TLG` logs) and the John Deere `GS_Database` SQLite directly, instead
   of relying on SMS to export shapefiles first.
2. **Read the catalogue** — open `Main.mdb` (JET) with mdbtools/Jackcess on
   Linux and resolve the numeric property-ID columns via
   `PropertyDefinition_Table` (see findings doc) to reconstruct the
   Grower→Farm→Field→Dataset tree.
3. **Grow the UI** — field/dataset browser tree beside the map; more layer types
   (as-applied, boundaries, guidance, imagery via GDAL rasters).
```
