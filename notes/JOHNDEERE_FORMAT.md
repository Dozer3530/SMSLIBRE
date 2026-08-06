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
