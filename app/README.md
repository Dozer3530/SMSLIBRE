# SMSLIBRE app (native .NET + Avalonia)

The real application: a native, cross-platform SMS replacement that **reuses
SMS's own ADAPT import engine** and renders with a clean-room native renderer.
No SMS, no WPF, no Wine. See [`../notes/FEATURE_PARITY.md`](../notes/FEATURE_PARITY.md)
for the road to full feature parity.

## Projects
| Project | Role |
|---|---|
| `SmsLibre.Core` | Domain model, native yield rasterizer (`YieldRaster`), cleaning, PNG writer — no external deps |
| `SmsLibre.Import` | Reuses SMS's `AgGateway.ADAPT.*` DLLs to import field data |
| `SmsLibre.App` | Avalonia UI: management tree · map · legend (the SMS layout) |
| `SmsLibre.Cli` | Headless import + render to PNG (batch/export, CI-testable) |
| `tools/SmsLibre.Shot` | Headless screenshot of the app for verification without a display |

## Build & run

Requires the .NET SDK (8+) and the AgGateway ADAPT DLLs from an SMS install. The
`SmsLibre.Import` project references them via `$(SmsNetCoreDir)`, defaulting to
the Windows install path. **On Linux, point it at your install:**

```bash
dotnet build app/SmsLibre.sln -c Release -p:SmsNetCoreDir=/path/to/SMS/NetCoreDependencies
dotnet run --project app/src/SmsLibre.App -c Release       # the GUI
# or headless:
dotnet run --project app/src/SmsLibre.Cli -c Release -- /path/to/TASKDATA out.png
```

> The ADAPT DLLs are SMS's own files, copied into the build output at compile
> time (not committed here). They are `netstandard2.0`, so they run natively on
> Linux .NET. This is the core reuse strategy — see
> [`../notes/STAGE_SALVAGE_LEDGER.md`](../notes/STAGE_SALVAGE_LEDGER.md).

## Status
Vertical slice complete: ISOXML import (via SMS's engine) → tree → native
yield-map render → legend/stats → PNG export. Everything else in the parity
roadmap builds on this spine.
