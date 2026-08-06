<h1 align="center">SMSLIBRE</h1>

<p align="center">
  <strong>Import agricultural machine data straight into QGIS.</strong><br>
  Yield, as-applied, as-planted and field boundaries — from the card, into a map.
</p>

<p align="center">
  <a href="https://github.com/Dozer3530/SMSLIBRE/releases/latest"><img alt="latest release" src="https://img.shields.io/github/v/release/Dozer3530/SMSLIBRE?label=download&color=2c7fb8"></a>
  <img alt="QGIS 3.22+" src="https://img.shields.io/badge/QGIS-3.22%2B-589632">
  <img alt="status" src="https://img.shields.io/badge/status-experimental-orange">
</p>

---

A QGIS plugin that reads the data cards your machinery writes — the kind of
import Ag Leader **SMS** does — and turns them into styled QGIS layers, with
**every logged sensor channel kept as an attribute**.

It uses the **AgGateway ADAPT** plugin suite, the same import engine SMS itself
uses, so one plugin covers many manufacturers.

> **Internal tool — not for public distribution.** Built for Olds College Center
> For Innovation, Smart Farm. John Deere, ISOXML and field boundaries all import
> real data today, confirmed in QGIS — see [Format support](#format-support).

## Why

QGIS is already excellent at maps, styling, analysis and layout. The one thing
it cannot do is read proprietary agricultural machine formats. SMSLIBRE fills
exactly that gap and gets out of the way — no separate desktop app, no
re-implementation of GIS features that QGIS already does better.

## Install

Internal distribution only. Get `smslibre_import.zip` from the
[latest release](https://github.com/Dozer3530/SMSLIBRE/releases/latest) (private
repo), then in QGIS:

**Plugins ▸ Manage and Install Plugins… ▸ Install from ZIP** → select the file →
enable **SMSLIBRE Machine Data Import**.

The build bundles a self-contained .NET sidecar, so there is nothing else to
install. Windows x64 today; Linux builds from source with
`python qgis_plugin/build_plugin.py --runtime linux-x64`.

## Use

1. Toolbar ▸ **Import machine data…**
2. Choose the **card folder** — a USB/SD card as written by the display, or an
   ISOXML `TASKDATA` folder.
3. **Detect format** — confirms which reader applies.
4. **Import**, tick the layers you want, **Add selected to map**.

Layers arrive styled: the meaningful channel (yield volume, yield mass,
moisture, applied rate…) rendered with a quantile red→green ramp, classified on
non-zero readings so headland zeros don't flatten the map. Boundaries come in as
labelled outlines.

> Point at the **original card or export**, not at SMS's internal Vault — SMS
> reorganises data into a private store the vendor readers don't recognise.

## Format support

| Format | Status |
|---|---|
| **John Deere** GS2 / GS3 / GS4 | ✅ **Working** — licensed SDK, verified in QGIS on real cards |
| **ISO 11783 / ISOXML** (`TASKDATA`) | ✅ **Working** — verified on real cards |
| **Field boundaries** (any ADAPT source) | ✅ **Working** — verified on real cards |
| AgGateway ADM | ⚪ loads; untested on data |
| Climate FieldView | ⚪ loads; untested on data |
| Precision Planting (2020) | ⚪ loads; untested on data |
| Trimble AgData | 🔒 **vendor licence required** |

**About vendor licensing.** The John Deere and Trimble ADAPT plugins refuse to
initialise without a vendor-issued *application id*. Olds College holds John
Deere's **SDK License for Display Plugins for ADAPT**, so John Deere import works
here. Trimble still needs its own licence.

Two consequences of that licence, both already reflected in the design:

- It forbids contributing the licensed materials into an open-source project and
  restricts end users to internal purposes — hence **internal distribution**, and
  vendor plugins loaded from a licensed folder rather than redistributed.
- It prohibits reverse-engineering the licensed components, so the exploratory
  work in [`notes/JOHNDEERE_FORMAT.md`](notes/JOHNDEERE_FORMAT.md) is retained
  only as a record of a path **not** taken.

Note John Deere's current plugins target **.NET 10** and must come from Deere's
own release — the older copies bundled inside SMS are Ag Leader's build and this
licence does not cover them.

## What you get in QGIS

Real numbers from real cards:

- **AGCO ISOXML harvest card** — 45 layers, **550,219 points**, 46 channels:
  yield mass & volume, harvest moisture, feeder throughput, rotor speed, header
  height & engaged state, working width, processor loss, crop type, timestamps…
- **John Deere GS3 2630 card** — 34 layers, **157,583 points**, 41 channels:
  wet yield mass, harvest moisture, vehicle speed, fuel rate, heading, distance
  travelled, plus recorded weather (humidity, air/soil temperature, wind).

Everything lands in a **GeoPackage** (EPSG:4326), so it is ordinary QGIS data:
filter it, join it, run it through Processing, style it however you like.

## How it works

```
QGIS plugin (Python)   ──subprocess──►   smsimport (.NET sidecar)
  dialog · layer load · styling            ADAPT plugins → GeoPackage
```

The vendor importers are .NET assemblies. Loading a CLR *inside* QGIS's bundled
Python is fragile and a crash would take QGIS with it, so the import runs in a
separate process that speaks JSON. It is also how the plugin stays
cross-platform.

## Repository layout

| Path | Contents |
|---|---|
| `qgis_plugin/` | the QGIS plugin (Python) + `build_plugin.py` packager |
| `sidecar/` | the .NET sidecar: domain model, ADAPT host, GeoPackage writer, tests |
| `notes/` | current analysis: feasibility, real-card testing, John Deere format |
| `notes/archive/` | earlier phase, when the goal was porting all of SMS |
| `tools/` | reverse-engineering utilities used to get here |

## Build from source

```bash
# Core + tests (no SMS install needed)
dotnet test sidecar/tests/SmsLibre.Core.Tests -c Release

# Internal build: bundles the licensed vendor SDK, works with no configuration
python qgis_plugin/build_plugin.py --runtime win-x64 --internal --install

# Public build: excludes all licensed material
python qgis_plugin/build_plugin.py --runtime win-x64          # or linux-x64
```

## Provenance & licensing

**Internal tool for Olds College Center For Innovation, Smart Farm — not for
public distribution.** It reads data files the institution already owns.

"SMS" and "Ag Leader" are trademarks of Ag Leader Technology; ADAPT is an
AgGateway project; the manufacturer formats belong to their respective owners.
**No vendor software or licence key is redistributed here** — the proprietary
ADAPT plugins are loaded from each user's own licensed installation, as the
vendor SDK licence requires.
