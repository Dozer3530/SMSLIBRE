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

Install in QGIS via **Plugins ▸ Manage and Install ▸ Install from ZIP**. The
build bundles a self-contained .NET 10 sidecar, so nothing else is needed.

### Internal build (recommended for Olds College)

Bundles the licensed John Deere plugins and credentials so the plugin works
**with no configuration** — John Deere cards are recognised out of the box:

```bash
python qgis_plugin/build_plugin.py --runtime win-x64 --internal
# → build/smslibre_import_INTERNAL.zip
```

Requires `secrets/johndeere.appid`, `secrets/johndeere.adaptplugins.lic` and
`vendor/jd-plugins/plugins/` to be present locally (all git-ignored).

> **⚠ Never publish the INTERNAL zip.** It contains licensed vendor binaries and
> a licence key. Distribute it inside the College only — network share or USB,
> not GitHub.

### Public build

Excludes all licensed material; John Deere then requires each user to point the
dialog's *Vendor plugins* / *Application id* settings at their own licensed copy.

```bash
python qgis_plugin/build_plugin.py --runtime win-x64     # or linux-x64
# → build/smslibre_import.zip
```

The packager refuses to include licensed files in a public zip even when they
are staged in `bin/`, so the two builds cannot be confused.

Add `--install` to either to copy straight into your QGIS profile.

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
via JSON. Source: [`../sidecar/src/SmsImport`](../sidecar/src/SmsImport).

## Files

| File | Role |
|---|---|
| `smslibre_import/plugin.py` | toolbar/menu entry point |
| `smslibre_import/dialog.py` | import dialog + background worker |
| `smslibre_import/sidecar.py` | JSON wrapper around the .NET sidecar |
| `smslibre_import/styling.py` | picks the value channel, graduated styling |
| `build_plugin.py` | publishes the sidecar, zips/installs the plugin |
