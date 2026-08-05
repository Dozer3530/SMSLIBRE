# SMSLIBRE — Stage 1–3 Findings

Reverse-engineering / porting analysis of **Ag Leader SMS Advanced v26.00**
(Spatial Management System) toward a native Linux replacement.

Analysis performed on the Windows work laptop where SMS is installed. This repo
is intended to sync to the Linux Mint machine, so nothing here depends on the
Windows install being present.

- Install root: `C:\Program Files\Ag Leader Technology\` (734 MB, 1,355 files)
- Data root: `C:\ProgramData\Ag Leader\SMS\Data\Data_2\`
- Regeneration tools: `tools/` (PowerShell + a .NET decompiler harness)
- Raw inventories: `analysis/inventory/*.csv` (git-ignored, regenerable)

> **Three assumptions in the original brief were wrong.** SMS is **not** native
> C++ *(it's a .NET/C++CLI hybrid)*, storage is **not** SQL Server Express
> *(it's Access/JET)*, and the field data mostly does **not** live in the
> database *(it's in a flat-file Vault of standard ag formats)*. Details below.

---

## Stage 1 — Binary triage

### What ALMapping.exe actually is

`ALMapping.exe` is a **176 KB native x64 launcher**. The application is a
**.NET + C++/CLI hybrid** whose payload lives in the DLLs. Target runtime is
**.NET (Core) — TFM `net10.0`** with a **WPF** UI
(`PresentationFramework`, `wpfgfx_cor3`, `DirectWriteForwarder`, `PenImc`).

PE scan of all 842 EXE/DLL files (`tools/Get-PEInventory.ps1`):

| Kind | Count |
|------|-------|
| Managed (.NET) | 729 |
| Native | 113 |

Architecture: 278 × x64, 563 × x86 (the x86 are mostly localized WPF satellite
resources and a few legacy components), 1 × ARM64.

### The critical distinction: pure-IL vs C++/CLI mixed-mode

A CLR header does **not** mean recoverable C#. A deeper classification
(`tools/decompiler classify`) splits the managed set three ways:

| Kind | Count | Decompiles to usable C#? |
|------|-------|--------------------------|
| Native PE | 113 | No (machine code) |
| **Pure-IL** | 565 | **Yes** |
| **Mixed-mode (C++/CLI)** | 164 | **No — bodies are native** |

The **first-party SMS core is C++/CLI mixed-mode**: `ALMappingLib` (24.6 MB),
`ALM_Common`, `ALA_Analysis`, `ALI_NETAgInterface`, `ALP_PreprocessorDll`,
`ALM_SpatialObject`, `ALV_MapVis*`, the `ALI_*` importer interfaces, etc. For
these, decompilation recovers **type names and method signatures** but the
method *bodies* are native machine code. Verified directly: `ALM_SpatialObject`
decompiles to C++ RTTI, mangled vtables, and `delegate*<…>` native function
pointers — not algorithms. (It does reveal useful facts, e.g. the projector
class `ALM_Projector` and a dependency on the open-source `DotSpatial.Projections`.)

**Consequence:** the map-rendering, spatial-processing, and low-level import
engines cannot be "read off" as C#. They are effectively a black box with a
documented surface. This is the deciding factor for Stage 4/5 strategy.

### What *is* recoverable as clean C#

Everything decompiled to `analysis/decompiled/` (9,722 `.cs`, 57.5 MB, 0 errors)
with the harness in `tools/decompiler/` (built on ICSharpCode.Decompiler 10.1).

**Pure-IL, unobfuscated — high value:**
- The **ADAPT import/export layer** (see below) — mostly open source.
- `ALN_NetClasses`, `ALD_Data`/`ALD_Datasets` metadata model, `ALE_DataExtension`.
- `AgLeader.Shared.*`, `AgLeaderCompress` (BouncyCastle-based), `AgLeader.Common`.

**Pure-IL but obfuscated** (control-flow flattening + renamed `<Module>{guid}`
opaque-predicate helpers; public type/method names survive):
`ALMS_*Desktop`, `AgLVersatileComponent`, `ALN_NetClasses` (81%),
`ALMS_DBAccessMobileDesktop` (71%). Recoverable with effort; the obfuscation is
control-flow, not name-stripping, so the API shape is intact.

### The ADAPT layer (major find)

`SMS\ADAPT\` contains the **AgGateway ADAPT** framework plus vendor plugins:
- `AgGateway.ADAPT.ApplicationDataModel`, `.ISOv4Plugin`, `.Representation`, `.ADMPlugin`
- `JohnDeere.CommonDataFramework.*` (RCD / GS2 / GS3 / GS4 display plugins)
- `PrecisionPlanting.ADAPT.2020` (41 MB), `Trimble.Ag.Adapt`, `CNHVoyager2`, `ClimateADAPT`

**AgGateway ADAPT is open source (MIT, on GitHub)** and is plain .NET Standard /
.NET Core. It is the engine SMS uses to read/write manufacturer field data. Much
of the "how do I parse vendor X's files" problem is already solved here, in code
that runs on Linux .NET as-is.

---

## Stage 2 — Database

**Not SQL Server.** Storage is **Microsoft Access / JET 4.0** via the ACE OLE DB
provider. Connection string recovered verbatim from `ALD_Data.dll`:

```
PROVIDER=Microsoft.ACE.OLEDB.12.0;Data Source=%s\%s;
Jet OLEDB:Database Password=%s;Persist Security Info=False;Jet OLEDB:Engine Type=5
```

Databases in `Data_2\`:
- **`Main.mdb`** (24.5 MB) — the catalogue/index (projects, fields, datasets).
- `Weather.mdb` (10.3 MB), plus transient `Projects.mdb`, `ListsTrans.mdb`, etc.

### Password protection & why it does not block the port
`Main.mdb` has a **JET database password**, supplied by SMS at runtime, so the
schema can't be read through ACE without it. Two mitigations:
1. **JET4 database passwords do not encrypt the data pages** (only ACE enforces
   the check). On Linux, **`mdbtools` / Jackcess read password-protected `.mdb`
   tables regardless.** So the Linux port is *not* gated on the password.
2. On Windows, `tools/Bruteforce-JetPasswordFromStrings.ps1` (test embedded
   strings against ACE, self-verifying) recovered it. **The password is the
   hardcoded literal `moreappropriate`**, stored in `ALD_Data.dll`. Schema then
   exported with `tools/Export-AccessSchema.ps1` → `analysis/schema/Main/`.

### Schema shape (122 tables, 1,613 columns, 61 FKs, 106 PKs)
- **Logical/Physical dual hierarchy.** Organizational tree
  `Grower → Logical_Farm → Logical_Field` (+ `LogicalCrop`, guidance patterns),
  mirrored by `Physical_*` instance tables. `Dataset_Table` (3,476 rows) is the
  central spatial-dataset catalogue; `Dataset_Filing_Table` (2,632) is a wide
  junction linking each dataset to Field/Farm/Product/Operation/Year/Grower/
  Vehicle/Attachment/etc. `Physical_Dataset_Table` and `LoggingFileInfo_Table`
  tie datasets to the on-disk Vault/PointData payload.
- **⚠ Columns are named by numeric property ID, not words.** e.g. `Dataset_Table`
  columns are `2, 1, 106, 8, 105, 4, …`. The ID→meaning dictionary is
  `PropertyDefinition_Table` (512 rows) plus the decompiled
  `AgLeader.DBAccess.Mobile.FactoryIds` classes (`PropertyIds`, `AttributeIds`,
  `OperationIds`, `EnumTypeIds`, …) in `ALMS_DBAccessMobileDesktop`. **Any tool
  that reads this DB must first load that ID map** — it is the Rosetta stone.

### The database is a catalogue, not the payload
The actual agronomic data lives on disk in the **Vault** and **PointData**:
- `Vault\` — organised by **source device format**: `AGCO ISO11783`,
  `NH ISO11783` (**ISO 11783-10 / ISOXML**, an open standard: `TASKDATA.XML`
  + binary `TLGxxxxx.bin` time logs), `John Deere\…\RCD\EIC` (JD EIC `.fdd/.fdl`),
  `CNH_Voyager2_Repository`, `EIC_GS2/GS3`, `Raven JDP`, and John Deere GS3
  **SQLite** `GS_Database` files.
- `Vault\Spatial\` — **ESRI Shapefiles** (108 `.shp` sets) and some GeoPackage/KML.
- `ProgramData\PointData\` — 3,593 files, SMS's own internal point store.

So field data exists in a mix of **open standards** (ISOXML, Shapefile, SQLite)
and **vendor/SMS proprietary** blobs, indexed by `Main.mdb`.

---

## Stage 3 — Windows-specific dependencies

Shorter than feared, because SMS already does its heavy lifting through
cross-platform native libraries.

### Already portable (native Linux builds exist)
The entire geospatial stack is bundled and is Linux-native:
`gdal302.dll` (GDAL 3.2.2), `proj.dll`, `geos`/`geos_c`, `geotiff`, `tiff`,
`netcdf`, `hdf5`, `cfitsio`, `openjp2`, `webp`, `laszip`/LibLAS, `lti_dsdk_9.5`
(MrSID), `sqlite3`, `libpq`/`libecpg` (PostgreSQL client), `libxml2`, `iconv`,
`zlib`/`zstd`/`lzma`/`bz2`, OpenSSL. Also `DotSpatial.Projections` (managed).

### Windows-only — must be replaced
| Dependency | Role | Linux replacement |
|---|---|---|
| **WPF** (`PresentationFramework`, `wpfgfx_cor3`, `DirectWriteForwarder`, `PenImc`) | Entire UI | Full UI rewrite (Qt/PySide6, or Avalonia if staying .NET) |
| **C++/CLI core** (`ALMappingLib`, `ALM_*`, `ALA_*`, `ALP_*`, `ALI_*`) | Spatial engine, import preproc, rendering | Reimplement against open libs / ADAPT |
| **Amyuni** `cdintf*`, `acfpdfu*` | PDF printer driver | Direct PDF (pdfium is already bundled / ReportLab) |
| **Rogue Wave** `Alrw*` (SourcePro/Stingray) | Legacy grids/UI | Qt widgets |
| GDI+, COM, registry reads | Misc | Qt / plain files |

### Registry & COM (modest)
Own key: `HKLM\SOFTWARE\Ag Leader Technology\SMS Basic\1.0`. Also reads IE
`FEATURE_BROWSER_EMULATION` (embedded web view) and AppCompat layers. COM usage
is light. Licensing (`AlrwSFLEXNETasudm` + `*.lic`) is **moot** — personal use,
per project scope.

---

## Implications for Stage 4 / 5 (summary; full plan separate)

The C++/CLI core being native machine code means a **literal port is not
viable** — there is no C# source to translate for the parts that matter most.
The realistic path is a **clean-room Linux tool that reads the same data**,
using:
- **GDAL/Fiona/Shapely/Rasterio** (Python) or the bundled native GDAL for I/O,
- **AgGateway ADAPT** (open source, runs on Linux .NET) for vendor formats, or
  reimplemented readers for the open formats (ISOXML, Shapefile, SQLite),
- **PySide6/Qt** for the UI, **SQLite/PostgreSQL** for storage,
- the **decompiled C# as a specification/reference**, not as code to port.

The proprietary internal formats (`PointData`, `.spf/.tso`) are the hard part,
but the underlying data can usually be re-derived from the standard-format Vault
sources, which lets the PoC avoid them entirely.

**PoC (Stage 5):** pick one field whose Vault source is **ISOXML or Shapefile**,
read its boundary (ISOXML `PFD` partfield or `.shp`), read its yield (ISOXML
`TLG` time logs or the JD `GS_Database` SQLite), and render the yield map with
GDAL + matplotlib/Qt. This proves the pipeline without touching WPF or the
native core.
