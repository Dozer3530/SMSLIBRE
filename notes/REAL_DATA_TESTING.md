# Real-data testing — Olds College Smart Farm Vault

Every directory in `G:\Shared drives\Olds College Smart Farm Vault` was taken
through the importer end to end:

    discover -> import -> open the GeoPackage -> validate geometry and attributes

Run it with:

```bash
python tools/vault_test.py --root "G:/Shared drives/Olds College Smart Farm Vault" --out analysis/vault --depth 12 --cap 200000 --workers 5 --min-depth 2
```

It writes `analysis/vault/results.json` (the corpus the regression tests read)
and `analysis/vault/COVERAGE.md` (the report). `--only-detected` re-imports just
the claimed folders after a sidecar change; `--reuse-scan` skips the walk.

## Headline

**13,052 directories walked. 432 cards. 62,711,501 features across 16,689
layers.** No invalid or out-of-range geometry survived import.

| Outcome | Cards |
|---|--:|
| Imported with data | 232 |
| Detected but empty | 189 |
| Detected but failed | 11 |
| No reader | 12,620 directories |

| Reader | Cards | Features | Max channels |
|---|--:|--:|--:|
| ProtobufPlugins (JD Gen4) | 102 | 46,739,121 | 1351 |
| RCDPlugins (JD GS3/GS4) | 53 | 11,610,409 | 63 |
| Card in an archive | 32 | 4,128,215 | 590 |
| John Deere Gen4 logs (.jdl) | 12 | 2,460,216 | 1215 |
| Raven Slingshot (.jdp) | 2 | 1,595,167 | 489 |
| ISO v4 (ISOXML) | 28 | 1,549,997 | 102 |
| ADMPlugin | 3 | 436,928 | 56 |
| Trimble AgData | 0 | — | licence |
| PrecisionPlanting | 0 | — | no real cards |

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
