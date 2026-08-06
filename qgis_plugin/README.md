# SMSLIBRE Machine Data Import — QGIS plugin

Import precision-ag machine data straight into QGIS: yield, as-applied,
as-planted — **every logged sensor channel** becomes a layer attribute.

Reads cards using the **AgGateway ADAPT** plugin suite (the same engine Ag Leader
SMS uses), so one plugin covers many manufacturers.

## Supported formats

| Format | Source |
|---|---|
| **ISO 11783 / ISOXML** (`TASKDATA`) | open source — always available |
| John Deere **GS2 1800 / 2600 / CommandCenter**, **GS3 2630**, **GS4 4600** | from your SMS install |
| **Climate FieldView** | from your SMS install |
| **Precision Planting** (2020) | from your SMS install |
| **Trimble AgData** | from your SMS install |
| **AgGateway ADM** | from your SMS install |

The vendor plugins are proprietary and **not redistributed** — the plugin loads
them from your own Ag Leader SMS installation. ISOXML works without SMS.

## Install

**From the built zip** (QGIS ▸ Plugins ▸ Manage and Install ▸ Install from ZIP):

```bash
python qgis_plugin/build_plugin.py --runtime win-x64     # or linux-x64
# → build/smslibre_import.zip
```

Add `--install` to copy straight into your QGIS profile instead.

The build bundles a self-contained .NET sidecar (~80 MB), so no .NET runtime
install is needed.

## Use

1. Toolbar ▸ **Import machine data…**
2. Pick the **card folder** — the folder written by the display (a USB/SD card
   root, or an ISOXML `TASKDATA` folder).
3. **Detect format** confirms which reader applies.
4. **Import** converts to a GeoPackage, then tick the layers and
   **Add selected to map**.

Layers are styled automatically: the most meaningful channel (yield volume,
yield mass, moisture, applied rate…) with a quantile red→green ramp.

> **Point at the original card, not SMS's Vault.** SMS reorganises data into its
> own internal store that the vendor plugins do not recognise. Use the card or
> the export as it came off the machine.

## Architecture

```
QGIS plugin (Python)  ──subprocess──►  smsimport (.NET sidecar)
  dialog, layer load                     ADAPT PluginFactory → GeoPackage
  auto-styling                           every channel as an attribute
```

The importers are .NET; embedding a CLR inside QGIS's Python would be fragile
and could crash QGIS, so the sidecar runs as a separate process and communicates
via JSON. Source: [`../app/src/SmsImport`](../app/src/SmsImport).

## Files

| File | Role |
|---|---|
| `smslibre_import/plugin.py` | toolbar/menu entry point |
| `smslibre_import/dialog.py` | import dialog + background worker |
| `smslibre_import/sidecar.py` | JSON wrapper around the .NET sidecar |
| `smslibre_import/styling.py` | picks the value channel, graduated styling |
| `build_plugin.py` | publishes the sidecar, zips/installs the plugin |
