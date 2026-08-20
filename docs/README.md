# Documentation

**[SMSLIBRE_User_Guide.docx](SMSLIBRE_User_Guide.docx)** — the complete user
guide: installing, importing, every dialog control, all supported formats,
troubleshooting, the command line, and the licensing rules. Written for someone
who has never seen the plugin. Edit it freely; it is a normal Word document.

## Rebuilding the guide

The guide is generated so it can be kept honest as the plugin changes — the
screenshots are captures of the real dialog and the map is rendered from a real
import, not mock-ups.

```bash
# 1. real screenshots of the dialog (needs QGIS installed)
"C:\Program Files\QGIS 3.44.12\bin\python-qgis-ltr.bat" docs/make_screenshots.py

# 2. diagrams and data figures
python docs/make_figures.py

# 3. the document itself
set NODE_PATH=%APPDATA%\npm\node_modules
node docs/build_guide.js
```

| File | What it is |
|---|---|
| `build_guide.js` | Builds the .docx (uses the `docx` npm package) |
| `make_screenshots.py` | Drives the real dialog and grabs it to PNG |
| `make_figures.py` | Diagrams, plus maps rendered from a real GeoPackage |
| `images/` | Generated figures |
| `sample_result.json` | A real sidecar result, used to populate the screenshots |
| `image_sizes.json` | Figure dimensions, so the document scales them correctly |
