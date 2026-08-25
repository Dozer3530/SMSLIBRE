# Real-data testing — Olds College Smart Farm Vault

Every directory in `G:\Shared drives\Olds College Smart Farm Vault` was taken
through the importer end to end:

    discover -> import -> open the GeoPackage -> validate geometry and attributes

Run it with:

```bash
python tools/vault_test.py --root "G:/Shared drives/Olds College Smart Farm Vault" --out analysis/vault --depth 12 --cap 200000 --workers 5 --min-depth 2
```

It writes `analysis/vault/results.json` and `analysis/vault/COVERAGE.md` (the
report). `--only-detected` re-imports just the claimed folders after a sidecar
change; `--reuse-scan` skips the walk.

The regression tests read **every** `results.json` on disk — each
`analysis/campaign/<drive>/` plus the single-drive `analysis/vault/` — and merge
them by card path, keeping the richest record for a card that appears twice.
That is 568 cards rather than the vault's 428. The reader set happens to be the
same nine either way, so the test count does not move; what moves is the pool
each reader's representative is drawn from, and the reach of the
no-bad-geometry assertion, which now covers all three drives.

## Headline (three drives, August 24 2026 campaign)

An unattended 8-phase campaign: unit tests, a full sweep of every shared drive,
a re-import of each drive to prove the results reproduce, and a combined report.
15.4 hours.

| Drive | Dirs walked | Cards | Imported | Features |
|---|--:|--:|--:|--:|
| Smart Farm Vault | 13,472 | 801 | 554 | **108,857,115** |
| 210600 STAAR | 9,260 | 116 | 82 | **67,377,366** |
| M: sfdata | 10,339 | 455 | 3 | **99,674** |
| **All** | **33,071** | **1,372** | **639** | **176,334,155** |

No invalid or out-of-range geometry survived import on any drive. Six failures,
all known: one Trimble licence, five Precision Planting claims on drone imagery.

**Every card re-imported reproduced exactly** — 123 cards across the three
drives, compared on layer count, feature count, channel count and operation
list. Nothing drifts between runs.

**M: holds almost no raw machine data.** 451 of its 455 claims are Precision
Planting over-claiming folders it cannot read; the drive is analysis output,
aerial imagery and planning documents. Three real cards (HyperLayer exports)
import cleanly. That is the drive's true state, not a coverage gap.

### What still cannot import (the improvement backlog)

Genuine gaps — data folders outside any imported card:

| What | Where | Why |
|---|---|---|
| 18 folders of `.jdl` under a `JD-Data/log` the Deere plugin declines | vault, e.g. `Brandt Seeding/JD-Data/log/2024_Test_Test_Test` | card structure present but plugin returns nothing; LooseGen4 correctly stays out of intact trees |
| `.bin` calibration files (`CalFiles`) | vault 2022 harvest | combine calibration, not spatial data — likely not importable by design |
| `.db` folders | mostly image databases (crop-stage photos, OPI screenshots) | not machine data |
| CNH Voyager2 native (`.fmd`/`.fld`, empty-stub TASKDATA) | both drives | no reader exists; route remains ISOXML export from the display |
| Raven Viper channel values (rates) | 210 imported cards | track/elevation/speed/distance only until DDI record types 118/155/156/157 are decoded |
| Point-grid prescriptions | 1 job | only polygon rate maps are parsed |

## What the sweep found

Testing detection alone would have proved nothing. Every finding below came
from importing and then checking what actually landed in the GeoPackage.

### Corrupt GPS fixes were being imported

Three ISOXML harvest cards carried coordinates no receiver can produce —
latitude −214, latitude 95.8, lat/lon transposed. Six bad fixes in 5,200 points
is enough to stretch a layer's extent across the globe and wreck any
classification built from it.

Cause was duplicated knowledge: `RavenReader` range-checked coordinates,
`AdaptHost` only skipped (0,0). The rule now lives once in
`Coordinates.IsPlausible` and every reader calls it. Boundary rings are filtered
too, and a ring left with under three vertices is dropped rather than emitted as
a degenerate polygon.

### A card slightly wider than 1,999 columns would have lost everything

SQLite refuses a table with 2,000 or more columns — verified against the
e_sqlite3 build we ship. A 2022 forage harvester card already writes layers of
1,535 columns. `OperationLayer.ChannelsToKeep` now fits a layer to the limit by
keeping the channels with the most readings; the overflow on a card that wide is
dominated by channels null at every point.

### Three shapes of card were invisible

| Shape | What was wrong | Recovered |
|---|---|---|
| ISOXML inside a `.jdp` | 224 of 499 `.jdp` files are a zip holding a TASKDATA; ISOv4 needs a folder | 30 with boundaries |
| Gen4 logs out of their card | `.jdl` copied out of `JD-Data/log/`, so the Deere plugin declines the folder | 2025 silage season, 99,311 points from one folder |
| A card that was zipped | `2. Saskler\...\Combine Data` holds only `Case Combine.zip`, `JD 9770 #1.zip`, `JD 9770 #2.zip` — no unzipped copy anywhere | 476,184 points |

### The totals were inflated by a third

The first clean sweep reported 133.8 M features. 44.3 M of those were the same
data read twice: 116 cards with data sat inside another card with data, because
two readers legitimately claimed folders at different depths — a Gen4 card and
the log folder inside it, an archive and the folder someone extracted it into.

Fixed at the source (`LooseGen4` declines anything inside a `JD-Data` tree;
`ArchivedCard` yields to an unzipped twin; loose logs are offered before
archives). The report also detects the overlap independently and excludes
enclosed cards from the totals rather than trusting the readers not to overlap.
19 overlaps remain, listed in the report: a zip one folder above its extracted
copy, which the sibling check does not see. They are excluded from the totals.

## What does not import

**New Holland Voyager2 (`.cn1`) — no.** This nearly went down as a success: 18
directories detect as ISOXML. They import nothing, because the card's
`TaskData.xml` is a 208-byte stub — a well-formed `ISO11783_TaskData` element
with no children. The logged data is beside it in CNH's `.agp/.nav/.pls/.agf`
files, which no ADAPT plugin reads. The import now says so and points at the
route that works: export ISOXML from the display.

**Trimble AgData — licence.** The plugin loads and refuses our application id
("Invalid license provided"). No code change fixes it; the request is pending.

**Precision Planting — no cards.** All 12 hits are drone imagery:
`RedEdge_M350` multispectral captures in `SYNC000nSET` folders. The plugin
claims anything named `*SET` and then throws a NullReferenceException. There is
no Precision Planting data in the vault, so the reader remains untested.

**Raven native `.jdp` — 251 folders, unsupported.** The other 275 `.jdp` files
hold Raven's own job layout (`DDOP.XML` plus `.jdf/.jhf/.sct/.ab`, no TASKDATA).
Reading them needs real format work, so they are deliberately left unclaimed and
keep showing up as a gap rather than importing nothing quietly.

The remaining unclaimed file types are documents, imagery and shapefiles —
`.shp` exports open natively in QGIS and are not a gap.

## Permissive plugins

Several ADAPT plugins answer yes to almost any folder and then return nothing:
RCDPlugins claims report and imagery directories, PrecisionPlanting claims bare
field folders. 189 cards are "detected but empty" for this reason or because
they are genuine setup/prescription cards. The report lists the file types
beside each so the two are distinguishable — a folder of PDFs was never a card.

Of the archives, 194 of 224 hold only a prescription (a TZN treatment zone and
its shapefile — a rate plan for work not yet done). Those are not mapped as
machine data, and the import says so instead of returning a bare "nothing found".

## Performance notes

- Detection is cheap; **starting** the sidecar is not — it loads every ADAPT
  plugin from the SMS install. Spawning it per directory took two hours for
  1,669 directories. One `smsimport scan` process walks the same tree at about
  17 directories a second.
- Readers answer `IsDataCardSupported` recursively, so detection on a container
  folder walks everything beneath it. `1. Smart Farm` (~20,000 directories) did
  not answer in 150 s. `scan --min-depth 2` skips levels where a card cannot be;
  the report discards hits up there anyway.
- The vault lives on a Google Drive shared drive, where a read can fail with
  "Incorrect function." on a perfectly good file. The harness retries once and
  labels failures `licence` / `environment` / `timeout` / `format`, so only
  `format` reads as a real gap.

## Open questions

- `Chopper 2022` and `Chopper Data` exceed the 2,400 s per-card timeout.
- Three Saskler `PreSeed` folders fail in RCDPlugins with a path error.
- JD Gen4 seeding layers show single-point extents on some cards. Import the
  same card in SMS to settle whether the coverage is in the data at all.
