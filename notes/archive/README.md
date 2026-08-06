# Archive — superseded work

These documents come from an earlier phase of this project, when the goal was to
run or rebuild the whole of Ag Leader SMS on Linux. **That goal was dropped.**
SMSLIBRE is now a QGIS plugin for importing agricultural machine data.

They are kept because the analysis behind the current design lives here — in
particular *why* importing is the part worth reusing and the rest was not.

| Document | What it still explains |
|---|---|
| `STAGE1-3_FINDINGS.md` | How SMS is actually built: .NET + C++/CLI + WPF, Access/JET storage (not SQL Server), the Vault's on-disk formats, and its Windows dependencies. |
| `STAGE_SALVAGE_LEDGER.md` | The measurement that decided everything: which SMS code is portable managed .NET (the ADAPT import stack — reusable) versus native machine code (the compute engine — not). |
| `SMS_FEATURE_INVENTORY.md` | All 835 SMS features, extracted from its help file. Useful as a map of the problem domain. |
| `STAGE4_ARCHITECTURE.md` | The abandoned native-application architecture. |
| `FEATURE_PARITY.md` | The abandoned full-parity tracker. |
| `PROJECT_PLAN.md` | The abandoned phased plan for that rebuild. |

Current, live documents are one level up in [`../`](../):
`QGIS_PLUGIN_FEASIBILITY.md`, `REAL_DATA_TESTING.md`, `JOHNDEERE_FORMAT.md`.
