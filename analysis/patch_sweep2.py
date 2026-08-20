"""Re-test the rows that failed on the harness's own gpkg-name collision."""

import json
import sys
import pathlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "tools"))
import vault_test as vt  # noqa: E402

OUT = pathlib.Path(__file__).parent / "vault"
RESULTS = OUT / "results.json"


def main() -> int:
    rows = json.loads(RESULTS.read_text())

    # Every row that failed on the sharing violation the truncated gpkg name
    # caused. Identified from the stored error rather than a hand-kept list.
    targets = [r for r in rows
               if r["status"] == "error" and "WinError 32" in r["error"]]
    print(f"{len(targets)} collision-failed row(s) to re-test")

    for r in targets:
        path = r["path"]
        print(f"re-testing ...{path[-70:]}", flush=True)
        fresh = vt.test_candidate(vt.DEFAULT_EXE, path, OUT, timeout=7200,
                                  detected=r["detected"])
        r.clear()
        r.update(vt.asdict(fresh))
        print(f"  -> {fresh.status}: {fresh.layers} layers, "
              f"{fresh.features:,} features ({fresh.seconds:.0f}s)", flush=True)

    RESULTS.write_text(json.dumps(rows, indent=1))
    md = vt.build_report(rows, "G:/Shared drives/Olds College Smart Farm Vault")
    (OUT / "COVERAGE.md").write_text(md, encoding="utf-8")
    print("results.json and COVERAGE.md updated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
