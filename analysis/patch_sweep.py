"""Re-test a handful of cards and merge them into an existing sweep result.

For fixes that affect a known set of cards, re-running a five-hour sweep to
update four rows is waste. This runs the same per-card pipeline the sweep runs
(import -> open the GeoPackage -> validate) on named paths and splices the fresh
rows into results.json, replacing any row for the same path.
"""

import json
import sys
import pathlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools"))
import vault_test as vt  # noqa: E402

OUT = pathlib.Path(__file__).parent / "vault"
RESULTS = OUT / "results.json"

# The three copies of the Brandt Seeding card (never imported by the sweep:
# the first-claimant collapse folded them into their empty TASKDATA child)
# and the PreSeed copy that failed on a transient file lock.
V = r"G:\Shared drives\Olds College Smart Farm Vault"
PATHS = [
    (V + r"\1. Smart Farm\Raw Equipment Data\2024\Equipment Data\Field 19"
         r"\Brandt Seeding", "ISO v4 Plugin"),
    (V + r"\1. Smart Farm\Field Specific\Steckler\2024"
         r"\3. Seeding (Seed and Fertilizer - Broadcast or in-row)"
         r"\Steckler_Seed_20240511_Equipment\Brandt Seeding", "ISO v4 Plugin"),
    (V + r"\1. Smart Farm\Field Specific\19\2024"
         r"\3. Seeding (Seed and Fertilizer - Broadcast or in-row)"
         r"\Field19_2024_Seeding_EquipmentData\Brandt Seeding", "ISO v4 Plugin"),
    (V + r"\2. Saskler\Field Specific Information\SE14_GMS_SE-14-24-29 W2\2023"
         r"\5. Chemical Applications (pre-seed, Herbicide, Fungicide, Desiccant,"
         r" post seed applications)\PreSeed", "RCDPlugins"),
]


def main() -> int:
    rows = json.loads(RESULTS.read_text())
    by_path = {r["path"]: i for i, r in enumerate(rows)}

    for path, reader in PATHS:
        if not pathlib.Path(path).exists():
            print(f"missing, skipped: {path}")
            continue
        print(f"re-testing ...{path[-70:]}", flush=True)
        r = vt.test_candidate(vt.DEFAULT_EXE, path, OUT, timeout=7200,
                              detected=reader)
        row = vt.asdict(r)
        # The sweep records paths with the root's forward slashes; match both.
        for key in (path, path.replace("\\", "/", 1)):
            alt = key.replace(V, V.replace("\\", "/"))
            for k in (key, alt):
                if k in by_path:
                    rows[by_path[k]] = row
                    break
            else:
                continue
            break
        else:
            rows.append(row)
        print(f"  -> {r.status}: {r.layers} layers, {r.features:,} features "
              f"({r.seconds:.0f}s)", flush=True)

    RESULTS.write_text(json.dumps(rows, indent=1))
    md = vt.build_report(rows, "G:/Shared drives/Olds College Smart Farm Vault")
    (OUT / "COVERAGE.md").write_text(md, encoding="utf-8")
    print("results.json and COVERAGE.md updated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
