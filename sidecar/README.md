# smsimport — the SMSLIBRE sidecar

The .NET half of [SMSLIBRE](../README.md). It runs the **AgGateway ADAPT**
import plugins and converts a machine data card into a **GeoPackage** that QGIS
opens natively, keeping every logged sensor channel as an attribute.

It runs as a separate process because the vendor importers are .NET assemblies:
loading a CLR inside QGIS's bundled Python is fragile, and a crash there would
take QGIS down with it. The QGIS plugin invokes this and parses its JSON.

## Projects

| Project | Role |
|---|---|
| `src/SmsLibre.Core` | domain model, GeoPackage writer (points + polygons, no GDAL dependency), point cleaning |
| `src/SmsLibre.Import` | `AdaptHost` — loads every available ADAPT plugin, auto-detects a card's format, flattens logged operations and field boundaries |
| `src/SmsImport` | the CLI itself; emits JSON on stdout |
| `tests/SmsLibre.Core.Tests` | unit tests — no SMS install needed, runs in CI |
| `tests/SmsLibre.Import.Tests` | integration tests — need SMS's ADAPT assemblies, skip without them |

## Command line

```bash
smsimport plugins                      # every reader that loads, + init errors
smsimport detect  <cardPath>           # which readers claim the card
smsimport scan    <root> --depth 4     # find every importable card in a tree
smsimport import  <cardPath> out.gpkg  # convert; --plugin <name> to force one
```

All commands print a single JSON object on stdout (progress goes to stderr), so
the output is a stable contract for the plugin.

`--sms <dir>` points at the Ag Leader SMS install that supplies the vendor
plugins; it defaults to the standard Windows path.

## Building

The ADAPT assemblies are referenced from an SMS installation via
`$(SmsNetCoreDir)` (see [`Adapt.props`](Adapt.props)) — they are **not**
redistributed. Override the path on non-default installs:

```bash
dotnet build sidecar/SmsLibre.Sidecar.sln -c Release \
  -p:SmsNetCoreDir=/path/to/SMS/NetCoreDependencies
```

`SmsLibre.Core` alone has no such dependency, which is why CI can build and test
it on a stock Linux runner.

### Packaging note

The sidecar is published **self-contained but not single-file**. Single-file
embeds our copies of the ADAPT assemblies, giving the vendor plugins a second
assembly identity — every `IPlugin` cast then fails and most readers silently
disappear. See the comment in `qgis_plugin/build_plugin.py`.
