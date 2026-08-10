# Real-data testing — Olds College Smart Farm Vault

Tested the importer against the shared drive
`G:\Shared drives\Olds College Smart Farm Vault` (real cards from 2018–2026:
John Deere, New Holland, CNH Voyager2, Trimble, Raven, SeedMaster, Climate).

## Headline: vendor plugins are licence-gated

`IPlugin.Initialize()` must be called before use (missing this was a real bug,
now fixed). Calling it surfaced the decisive constraint:

| Plugin | Initialize() result |
|---|---|
| **ISOv4Plugin** (ISOXML) | ✅ works |
| **ADMPlugin**, **ClimateADAPT**, **PrecisionPlanting.ADAPT.2020** | ✅ initialises |
| Deere GS2_1800 / GS2_2600 / GS2_CommandCenter / GS3_2630 / GS4_4600 | ❌ *"Plugin.Initialize() must be called using your application id. plugin requires a license."* |
| Trimble AgData | ❌ *"Invalid license provided for 'Trimble AgData Plugin'"* |

**The John Deere and Trimble ADAPT plugins require a vendor-issued application
id / licence key.** SMS has one because Ag Leader is a licensed ADAPT partner.
This is a deliberate vendor access control, not a bug and not a path problem —
no amount of pointing at the right folder will change it. Extracting SMS's key
and presenting our tool as SMS would be circumventing that control, so it is not
an option here.

That reframes the goal: **"SMS's full import suite" is not fully transferable.**
The licence-free subset is, and there are good routes to most of the rest.

## What actually works today

- **ISOXML (ISO 11783)** — fully proven: AGCO card → 45 layers, 550,219 points,
  46 channels. This matters more than it sounds: most modern displays, including
  John Deere and Trimble, can **export ISOXML**, so the licence-free path covers
  a lot of real work.
- **Climate / Precision Planting / ADM** — initialise cleanly; untested against
  data (none of these cards in the sample set).

## Routes to the licence-gated formats

1. **Export ISOXML from the display** — zero engineering, already works.
2. **Read the formats ourselves.** John Deere GS3 cards store
   `RCD/GS_Database_1_0.db` — plain **SQLite**. Reading a data file the user owns
   is straightforward interoperability work and needs no vendor licence. Same
   idea for CNH Voyager (`.cn1` + `shared/*.FMD/*.FLD`).
3. Ask John Deere / Trimble for a developer application id (they issue them).

## Gap found: boundaries are being ignored

The Trimble card `2026_Trimble_qi_May13\TASKDATA` imported cleanly but produced
**0 layers** — correctly, because the importer only maps
`Documents.LoggedData → OperationData`. Inspection shows the card actually holds
**21 field boundaries** (`PFD`/`PLN`, 3,996 vertices), 2 farms and 2 customers.

Setup/prescription cards like this are common and valuable in QGIS. Next step:
import `Catalog.FieldBoundaries` (and guidance lines) as polygon/line layers,
which needs polygon support in the GeoPackage writer.

## Scan results

A new `smsimport scan <root>` walks a tree and reports every importable card.
Over the 2025/2026 folders (182 directories): 4 hits — 3 ISOXML, 1 Trimble
AgData. The 2025 harvest folders (John Deere, New Holland, Voyager2) yielded
nothing detectable, consistent with the licence wall plus formats whose plugins
aren't present.

## John Deere Gen4 seeding: geometry investigated (not our bug)

The 2025 Brandt/Steckler OFPE card
(`1RW9540DHRA086175_05132025`, a Gen4 `JD-Data/log/*.jdl` machine log) imports
127 layers / 143,567 points, but several layers looked suspicious — extents where
min == max. Investigated:

| Operation | Layers | Features | Spatially moving | Effectively parked |
|---|--:|--:|--:|--:|
| DataCollection | 51 | 142,431 | 0 | 51 |
| Fertilizing | 38 | 568 | 2 | 36 |
| SowingAndPlanting | 38 | 568 | 2 | 36 |

Findings:

- **Points are not duplicated.** Distinct coordinates ≈ feature count in every
  layer (e.g. 15,179 distinct of 15,433), so nothing is being written twice.
- **Nothing is being dropped.** Instrumented the import to record any record
  whose geometry is not a `Point`: the count is **zero**, so every record the
  plugin returned was imported.
- The `DataCollection` layers hold 15k points spanning ~0.000001° (about 10 cm) —
  consistent with stationary logging, not a track.
- The agronomic operations carry only ~15 points per layer over ~50 m.

**Conclusion: this is a faithful import of what the John Deere plugin returns for
this card.** SMSLIBRE is not losing the coverage.

**Open question for the farm:** is this card supposed to contain full seeding
coverage? It is a machine *log* export (`JD-Data/log`) rather than field
documentation, so it may legitimately hold diagnostics and summaries. The
decisive test is to import the same card in SMS: if SMS also shows no coverage
track, the data is not there; if SMS shows a full seeding map, the plugin is
being invoked differently and we should match it.

`skippedGeometries` is now reported by `smsimport import` so this class of
question can be answered without re-instrumenting.
