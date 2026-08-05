# SMSLIBRE — Native Salvage Ledger

Direction (user decision): **full-native Linux, rebuild the UI, but reuse SMS's
real code wherever it already runs on native .NET.** This ledger measures how
much can be reused vs must be reimplemented, so the rebuild is sized in numbers
instead of guessed.

Method: `tools/decompiler deps` dumps every managed assembly's references +
P/Invoke targets; `tools/salvage_ledger.py` propagates "taint" from the two
things that genuinely can't run on native Linux .NET — the **C++/CLI native
core** and **WPF/WinForms** — through SMS's own assemblies only. The .NET base
class library and normal NuGet packages (log4net, Newtonsoft, DotSpatial, the
ADAPT ecosystem) are portable and never taint. Full table:
`analysis/inventory/salvage-ledger.csv`.

## Headline

| Bucket | Count | Meaning |
|---|--:|---|
| **NATIVE-REUSABLE** (first-party) | **11** | SMS's own managed libs that run on Linux .NET as-is |
| **THIRD-PARTY-REUSABLE** (ADAPT/vendor) | **36** | the entire multi-vendor import engine, bundled, portable |
| **WPF-UI** | 7 | UI-coupled → rebuild in a native toolkit |
| **CORE-REIMPLEMENT** | 27 | the native C++/CLI engine → reimplement on open libs |

So **~47 assemblies of real, tested code are reusable natively**, the UI is a
rebuild (unavoidable), and the genuine engineering is the **27-assembly native
core**.

## THIRD-PARTY-REUSABLE — the biggest win (36 assemblies)

The hardest problem in ag software — reading every manufacturer's field data —
is already solved in **open, pure-.NET libraries SMS bundles but did not write**,
all of which run on native Linux .NET:

- **AgGateway ADAPT** (`ApplicationDataModel`, `ISOv4Plugin`, `Representation`,
  `ADMPlugin`, `PluginManager`) — the open standard object model + ISO 11783
  reader. MIT-licensed, on GitHub.
- **John Deere CommonDataFramework** (24 assemblies: RCD/GS2/GS3/GS4 display
  plugins, EIC, protobuf mappers, ShapeFileIO, representation/unit systems).
- **PrecisionPlanting.ADAPT.2020**, **Trimble.Ag.Adapt**, **CNHVoyager2**,
  **ClimateADAPT**, **crop-list**.

These read the Vault data into ADAPT's documented `ApplicationDataModel`. A
native app can consume that model directly — no reverse engineering of vendor
formats required.

## NATIVE-REUSABLE — first-party (11)

Run on Linux .NET unchanged (some are obfuscated, but obfuscation doesn't stop
execution — we reference the compiled DLLs and call them):

`AgLeader.Shared`, `AgLeader.Shared.Core`, `AgLeader.Shared.Data`,
`AgLeader.Shared.Data.IO.Compression`, `AgLeader.Shared.Web`,
`AgLeader.Shared.Web.AgFiniti.CloudSync`, `AgLeader.Common`, `AgLeaderCompress`,
`AgLVersatileComponent`, `AgFiniti.WebPlatform.Services.Contracts`,
`ALN_SystemSecurity` (has a Windows crypto P/Invoke to stub).

## WPF-UI — rebuild (7)

- **`ALN_NetClasses`** — genuinely WPF-bound (`PresentationFramework`,
  `PresentationCore`, `WindowsBase`, `System.Xaml`). The main .NET↔WPF bridge.
- `ALMS_DBAccessMobileDesktop`, `ALMS_SpatialDesktop`, `ALMS_UtilityDesktop`,
  `ALMS_CoordinatesDesktop`, `ALMS_DatasetsMobileDesktop`, `ALMS_UnitsDesktop` —
  only *lightly* coupled (`System.Windows.Forms` / `System.Drawing.Common` for
  dialogs/imaging). Several may be reusable with small shims rather than a full
  rewrite.

## CORE-REIMPLEMENT — the real work (27)

The native C++/CLI engine + native PEs. This is where SMS's processing lives and
where "feature by feature" genuinely applies:

- **`ALMappingLib`** (24.6 MB) — main application logic/orchestration.
- **`ALM_Common`**, **`ALM_SpatialObject`**, **`ALM_Advanced`** — spatial engine.
- **`ALP_PreprocessorDll`** — import preprocessing (yield cleaning, etc.).
- **`ALA_Analysis`**, **`ALRC_ReportChart`**, **`ALLegend`** — analysis, charts, legends.
- **`ALV_MapVisDll`/`ALV_MapVisUI`** — map visualization/rendering.
- **`ALI_*`** (`NETAgInterface`, `MiscSpatialInterface`, …) — device/format import glue.
- **`ALD_Data`/`ALD_Datasets`/`ALE_DataExtension`** — the JET data layer.
- Native PEs: `AgLeaderFile`, `AgLeaderCottonFile`, `ALM_AltovaXMLParserUR`, etc.

Mitigation: much of what this core *does* maps onto libraries SMS already
bundles and that are Linux-native — **GDAL/GEOS/PROJ** (spatial ops, rendering
geometry), the **ADAPT** layer (import), **SQLite/mdbtools** (data). So "reimplement"
often means "wire open libs + the reusable managed layer together," not
"write a spatial engine from scratch." The exception is SMS-specific algorithms
(their particular yield-cleaning, analysis math) which must be re-created from
the decompiled reference where it's readable.

## Architectural implication (important)

Because the reusable engine (ADAPT + `AgLeader.Shared.*`) is **.NET**, the
native rebuild reuses far more of SMS's *actual code* if it is itself **.NET**:

- **.NET + Avalonia** (recommended): build the new app in C#/.NET, reference the
  real ADAPT + reusable SMS DLLs and run them natively on Linux .NET; build the
  UI in **Avalonia** (cross-platform, deliberately WPF-like — even some XAML
  concepts carry over). Maximizes reuse; directly answers "don't rebuild it all."
- **Python + Qt** (the Stage-5 PoC path): simpler and great for rendering, but
  cannot load the .NET ADAPT libraries directly — it would reimplement or shell
  out to them, giving up the biggest reuse win.

The PoC was correctly built in Python to prove the pipeline fast. For the native
rebuild the user chose, **.NET + Avalonia is the higher-reuse choice** and is the
recommended pivot.

## Proof: SMS's real importer runs on plain .NET (`tools/adapt-proof/`)

Not a claim — demonstrated. A ~60-line .NET 8 console app references the
**unmodified** `AgGateway.ADAPT.ISOv4Plugin.dll` (+ `ApplicationDataModel`,
`Representation`) straight from the SMS install and calls
`Plugin.Import(taskDataPath)` on a **real Vault ISOXML dataset**
(`AGCO ISO11783/2024/09_26/.../TASKDATA`). Result:

```
Importer: AgGateway.ADAPT.ISOv4Plugin 2.0.0.0
Supports card? : True
  Growers: 1   Farms: 1   Fields: 1   Products: 1   LoggedData: 1
    • Field: 1516
```

It parsed the ISO 11783 task data and returned SMS's grower/farm/field/logged-data
object graph — **SMS's own import engine, executing outside SMS, with no WPF, no
native C++ core, and no Wine.** These assemblies are `netstandard2.0`, so the same
DLLs run identically on Linux .NET (this run was on Windows only because that's
where the analysis box is). Clean `dotnet build` + `dotnet run`, no manual steps.

This is the concrete validation of the whole "native rebuild" bet: the hardest,
most valuable part of SMS — reading everyone's field data — is reusable as-is.
