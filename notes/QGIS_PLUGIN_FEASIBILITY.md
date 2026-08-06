# Feasibility: SMS's import engine as a QGIS plugin

**Pivot (user):** forget the Linux/native-app effort. Take **SMS's machine-data
import capability** and deliver it as a **QGIS plugin**.

**Verdict: feasible, and it is the best-scoped idea yet** — with one real
constraint (vendor DLLs can't be redistributed) and one surprise (SMS's own
vault is *not* readable by the vendor plugins; original device cards are).

---

## Why this is the right scope

Import is precisely the part of SMS that is **portable, reusable, and not
locked in its native C++ core** (see [`STAGE_SALVAGE_LEDGER.md`](STAGE_SALVAGE_LEDGER.md)).
Everything I was otherwise rebuilding badly — rendering, styling, classification,
analysis, layout, export — QGIS already does far better. So the plugin only has
to do the one thing SMS uniquely does: **turn proprietary machine data into
GIS layers.**

## What was verified (not assumed)

Using ADAPT's own `PluginManager.PluginFactory` in a plain .NET 8 host:

**All 9 vendor plugins load and instantiate correctly, outside SMS:**

| Plugin | Version | Owner |
|---|---|---|
| GS2_1800, GS2_2600, GS2_CommandCenter, GS3_2630 | 2.0.2.11 | Deere & Company |
| GS4_4600Plugin | 2.0.2.9 | Deere & Company |
| ClimateADAPT | 2.25.4.29 | Climate Corp. |
| PrecisionPlanting.ADAPT.2020 | 0.1 | Precision Planting |
| Trimble AgData | 2.0.0.0 | Trimble Inc. |
| ADMPlugin | 1.0.0.0 | AgGateway |

Plus, in `NetCoreDependencies`: **ISOv4Plugin** (ISO 11783) and **CNHVoyager2** —
so **~11 formats** total. ISOXML import is already **proven end-to-end**
(545,510 yield points extracted from real AGCO data → GeoJSON → rendered).

`PluginFactory.GetSupportedPlugins(path)` gives **automatic format detection** —
the user picks a folder and the right reader is chosen for them.

## The surprise: SMS's Vault is not plugin-readable

Exhaustive probe — **~6,000 directories** across `Vault\John Deere`,
`Voyager2`, `CNH_Voyager2_Repository`, `Raven JDP`, `EIC_GS*_Repository` ×
9 plugins — produced **zero** matches. SMS's Vault is its own *reorganized
internal copy*, not an original card layout. (Its path constants are
Dotfuscator-encrypted, so the expected layout can't be read statically.)

**Implication — and it matches the real use case:** the plugin reads
**original device cards / exports** (the USB or SD card out of the combine, an
ISOXML `TASKDATA` folder, a Climate/PP export). That is exactly what a user
points at when they say "import my machine data." Re-reading data *already
imported into SMS* is a separate, lower-value path (SMS can export shapefiles
for that, or we read its vault formats ourselves later).

ISOXML is the exception that already works from the vault, because `TASKDATA`
is a standard layout SMS preserved verbatim.

## Architecture: .NET sidecar + Python plugin

The plugins are .NET; QGIS is Python. Two options were considered:

| Approach | Verdict |
|---|---|
| **pythonnet in-process** | Fragile — QGIS ships its own Python; loading a CLR into it invites version/ABI pain and can crash QGIS. |
| **Sidecar process** ✅ | A small .NET console tool converts card → **GeoPackage**; the Python plugin runs it and adds the layers. Clean process boundary, easy to debug, crash-isolated, works on Linux *and* Windows. |

```
QGIS plugin (Python)                     sidecar (.NET, self-contained)
  ┌──────────────────────┐   subprocess   ┌────────────────────────────┐
  │ pick folder          │ ─────────────► │ PluginFactory auto-detect  │
  │ show detected format │                │ vendor plugin → ADAPT ADM  │
  │ choose ops/layers    │ ◄───────────── │ write GeoPackage + JSON    │
  │ add layers + style   │   .gpkg/.json  │ (yield, as-applied, bounds)│
  └──────────────────────┘                └────────────────────────────┘
```

Most of the sidecar already exists (`app/src/SmsLibre.Import` + `SmsLibre.Cli`);
it needs the `PluginFactory` generalization and GeoPackage output instead of
bespoke GeoJSON.

## The one hard constraint: redistribution

- **Open source, safe to bundle:** AgGateway `ApplicationDataModel`,
  `ISOv4Plugin`, `Representation`, `PluginManager` (public AgGateway/ADAPT
  project). An ISOXML-only plugin could ship self-contained.
- **Proprietary, must NOT be bundled:** the John Deere, Precision Planting,
  Trimble, Climate and CNH plugin DLLs ship with SMS and are not ours to
  redistribute. The plugin must **point at the user's own SMS install** (a
  settings path), which is legitimate for personal use.

So: ISOXML works for anyone; the vendor formats work for users who have SMS
installed. That's an honest limitation to design around, not a blocker.

## Effort estimate

| Piece | Effort |
|---|---|
| Sidecar: PluginFactory + auto-detect + GeoPackage output | small–medium (foundation exists) |
| QGIS plugin skeleton (dialog, settings, run sidecar, add layers) | small |
| Layer mapping (yield / as-applied / as-planted / boundaries, all meters as attributes) | medium |
| Styling presets (graduated yield renderer, sensible defaults) | small |
| Packaging (bundle sidecar per-OS, plugin zip) | medium |

Vastly smaller than the native-app path — weeks of evenings, not years.
