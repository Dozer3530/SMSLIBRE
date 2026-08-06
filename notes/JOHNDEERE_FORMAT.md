# John Deere GS3 card format — reverse-engineering groundwork

Goal: read John Deere machine data **without** the licence-gated ADAPT plugin
(see [`REAL_DATA_TESTING.md`](REAL_DATA_TESTING.md)). Reading a data file the
user owns is ordinary interoperability work and needs no vendor licence.

Sample card: `samples/cards/jd_gs3_2025` (copied from the Olds College vault,
GreenStar 3 2630, harvest Oct 2025).

## Card layout

```
GS3_2630/<session name>/
  RCD/
    GS_Database_1_0.db        SQLite — metadata only (94 KB): tasks, crops,
                              operation types. NOT the logged points.
    EIC/Documentation/XX/     ** the actual logged data **
      <guid>.fdl              XML  — the log SCHEMA (27 KB)
      <guid>.fdd              BIN  — the log RECORDS (up to 1.7 MB)
    StoredMaps/…/*.pvf,*.pth  rendered coverage/path maps (display artefacts)
    Applications, ContextData, ExportOnly, MapSettings
```

25 `.fdd`/`.fdl` pairs on this card — one per logged run.

## `.fdl` — the schema (plain XML, fully readable)

`<RCDLogFile xmlns="urn:schemas-johndeere-com:RCD:LogFile">` with namespaces for
BasicTypes / Setup / SpatialTypes / Representation / UnitSystem. Contains:

- **Header**: schema, UoM and RepresentationSystem versions; source app
  (`RCDFieldDoc`); `FieldRef`/`TaskRef` GUIDs.
- **Setup / participants**: Client (`Olds College`), Farm (`Smart Farm`),
  Field, Operator — the whole management hierarchy, named.
- **`<Meter id=…>`** ×14 — each with `<MeasuredElement>`, calibration
  (`<CalibrationParam value= sourceUOM= variableRepresentaton="vrYieldCalibration1">`),
  `<vrLatency>`, and `<SectionRef>`s.
- **`<Section id=…>`** ×9 — header sections with `vrInlineOffset` /
  `vrLateralOffset` in mm (e.g. −5334, −3810, −2286 → section geometry).
- **`<DefinedTypeColumn columnID="MeasElement86" definedTypeRepresentation="dtHeaderStatus"/>`**
  ×13 — **this is the key**: it names the columns of the binary stream and gives
  each a semantic type (`dtHeaderStatus`, `dtRecordingStatus`, …).

## `.fdd` — the records (binary, semi-self-describing)

Length-prefixed structures; the header carries GUIDs matching the `.fdl`
(`uuidSession`, field/task refs). Readable ASCII identifiers appear inline —
`MeasElement78`, `MeasElement82`, `Section88_State`, `dtiHeaderStatusOn/Off`,
`dtiRecordingStatusOn/Off` — i.e. **the binary references the same column IDs the
FDL declares**.

Opening bytes:
```
01 00 9D 00 24 00 "9ED5F59C-7C09-43b1-8922-1A4E378942C5" 00 01 00 00 00 24 00 "68f1db54-…"
        ^^len?     ^^len  ^^ GUID string (0x24 = 36 chars)
```
Pattern: `uint16` length prefixes ahead of GUID strings — consistent with a
tagged/length-delimited record format.

## `.fdd` structure — what is now decoded

Probing (`tools/fdd_probe.py` + manual analysis) established:

**It is NOT compressed.** Entropy 3.71 bits/byte (8.0 would mean
compressed/encrypted); only 10 incidental zlib-looking byte pairs, no gzip. So
the records are plain structured binary — decodable without a codec.

**It is a length-prefixed TLV stream.** Every string is preceded by a `uint16`
length. Annotated header:

```
01 00                      version / record tag (uint16 = 1)
9D 00                      block length (0x9D = 157)
24 00  "9ED5F59C-7C09-…"   len 0x24 = 36, then the GUID
01 00 00 00
24 00  "68f1db54-…"        uuidSession   (matches the .fdl header)
24 00  "fc3073ce-…"        TaskRef       (matches the .fdl)
24 00  "68f1e18c-…"        FieldRef      (matches the .fdl)
01 00 02 00 22 00 03 00 10 00
0D 00  "MeasElement78"     len 0x0D = 13, then the COLUMN NAME
…      "MeasElement82", "MeasElement86",
       "dtiHeaderStatusOn", "dtiHeaderStatusOff",
       "Section88_State", "dtiRecordingStatusOn", …
```

So the file **declares its own column schema up front**, using exactly the
`columnID` values the `.fdl` defines (`MeasElement86` → `dtHeaderStatus`,
`Section88_State` → `dtRecordingStatus`). Schema side: effectively solved.

**Positions are not stored as plain absolute lat/lon.** Scans for float64,
float32, int32×1e7, int32×1e6 and int32-semicircles found no dense coordinate
track (23 scattered lat matches in 1.7 MB, no adjacent lat/lon pairing), and a
scale-independent lat/lon-ratio scan produced only false positives. The most
likely explanations, in order: positions are **delta-encoded** against a base
fix (normal for track logs), or they live in a per-column block whose values are
offsets from a base declared elsewhere in the TLV stream.

### Remaining work
1. Walk the TLV stream end-to-end to enumerate blocks and find where the schema
   block ends and record blocks begin.
2. Map each declared column to its value encoding and width.
3. Identify the position column and its base/delta scheme.

## Assessment

**Feasible, but a real project — not a quick win.**

- ✅ The schema side is *solved*: `.fdl` is XML, so channel names, units,
  calibrations, section geometry and the management hierarchy are readable now.
- ⚠️ The work is the `.fdd` record framing: block structure, column encoding,
  and locating the GPS position channel. That is genuine binary RE — measured in
  sessions, not minutes.

## Faster routes to John Deere data

1. **Export ISOXML from the display.** GS3/GS4 can write ISO 11783; our ISOXML
   path already works end to end. Zero engineering.
2. **Apply for a John Deere developer application id** and call
   `Initialize(appId)` — the plugin architecture already supports it; it is a
   one-line change once an id exists. Sensible for an officially published plugin.
3. Reverse-engineer `.fdd` (this document) — best long-term independence, most effort.
