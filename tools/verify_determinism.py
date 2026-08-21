"""Import the same cards twice and prove nothing drifts between runs.

Every sweep so far has asked "does this card import?" — never "does it import
the SAME way twice?" Those are different questions. A reader that walks a
directory in filesystem order, a dictionary iterated without sorting, a
timestamp folded into a layer name: each produces a result that is correct once
and different the next time, and a sweep run once can never see it.

So: re-import cards that already succeeded and compare against what was
recorded. Layer count, feature count, channel count and the operation list must
all match exactly. Anything else is a defect worth knowing about, whether it
shows as a changed number or a changed name.

    python tools/verify_determinism.py --results analysis/vault/results.json
    python tools/verify_determinism.py --results ... --sample 40 --repeat 2
"""

from __future__ import annotations

import argparse
import json
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import vault_test as vt  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[1]


def compare(recorded: dict, fresh) -> list[str]:
    """Field-by-field differences between a recorded row and a fresh import."""
    diffs = []
    for field in ("layers", "features", "max_channels"):
        was, now = recorded.get(field, 0), getattr(fresh, field)
        if was != now:
            diffs.append(f"{field}: {was:,} -> {now:,}")
    was_ops = sorted(recorded.get("operations") or [])
    now_ops = sorted(fresh.operations or [])
    if was_ops != now_ops:
        diffs.append(f"operations: {was_ops} -> {now_ops}")
    if recorded.get("status") != fresh.status:
        diffs.append(f"status: {recorded.get('status')} -> {fresh.status}")
    return diffs


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", required=True,
                    help="results.json from a completed sweep")
    ap.add_argument("--out", default="",
                    help="working directory for GeoPackages (default: beside results)")
    ap.add_argument("--exe", default=str(vt.DEFAULT_EXE))
    ap.add_argument("--sample", type=int, default=0,
                    help="check this many cards, chosen at random (0 = every card)")
    ap.add_argument("--repeat", type=int, default=1,
                    help="re-imports per card; 2 compares the fresh runs to each other too")
    ap.add_argument("--seed", type=int, default=1,
                    help="sampling seed, so a run can be reproduced exactly")
    ap.add_argument("--timeout", type=int, default=7200)
    ap.add_argument("--max-seconds", type=int, default=0,
                    help="stop starting new cards after this long (0 = no limit)")
    args = ap.parse_args()

    results_path = pathlib.Path(args.results)
    rows = json.loads(results_path.read_text())
    out_dir = pathlib.Path(args.out) if args.out else results_path.parent
    out_dir.mkdir(parents=True, exist_ok=True)

    cards = [r for r in rows
             if r.get("status") == "ok" and r.get("features", 0) > 0
             and pathlib.Path(r["path"]).exists()]
    # Largest first: a wide card exercises far more of the code than a small one,
    # so if the run is cut short the expensive cases are already covered.
    cards.sort(key=lambda r: -r.get("features", 0))
    if args.sample and args.sample < len(cards):
        rng = random.Random(args.seed)
        # Keep the ten biggest, sample the rest — the tail is where the odd
        # formats live, and the head is where the volume is.
        head, tail = cards[:10], cards[10:]
        cards = head + rng.sample(tail, min(args.sample - 10, len(tail)))

    print(f"determinism check: {len(cards)} card(s), {args.repeat} re-import(s) each")
    print(f"  results: {results_path}")
    print(f"  working: {out_dir}\n", flush=True)

    t0 = time.time()
    drifted, checked, errors = [], 0, []

    for i, row in enumerate(cards, 1):
        if args.max_seconds and time.time() - t0 > args.max_seconds:
            print(f"\nstopping: {args.max_seconds}s budget reached "
                  f"({i - 1}/{len(cards)} checked)")
            break

        path = row["path"]
        runs = []
        for rep in range(args.repeat):
            fresh = vt.test_candidate(pathlib.Path(args.exe), path, out_dir,
                                      args.timeout, row.get("detected", ""))
            runs.append(fresh)

        checked += 1
        name = pathlib.Path(path).name[:40]

        # A run that failed for an environmental reason says nothing about
        # determinism; record it and move on rather than calling it drift.
        bad = [r for r in runs if r.status in ("error", "skipped-disk")]
        if bad:
            errors.append((path, bad[0].status, bad[0].error))
            print(f"  [{i}/{len(cards)}] {'ENV':7} {name:42} {bad[0].error[:50]}",
                  flush=True)
            continue

        diffs = compare(row, runs[0])
        for extra in runs[1:]:
            for field in ("layers", "features", "max_channels"):
                if getattr(runs[0], field) != getattr(extra, field):
                    diffs.append(
                        f"{field} differs between fresh runs: "
                        f"{getattr(runs[0], field):,} vs {getattr(extra, field):,}")

        if diffs:
            drifted.append((path, diffs))
            print(f"  [{i}/{len(cards)}] {'DRIFT':7} {name:42} {'; '.join(diffs)[:70]}",
                  flush=True)
        else:
            print(f"  [{i}/{len(cards)}] {'ok':7} {name:42} "
                  f"{runs[0].layers:,}L {runs[0].features:,}F", flush=True)

    elapsed = time.time() - t0
    print(f"\n{'=' * 62}")
    print(f"checked {checked} card(s) in {elapsed / 60:.0f} min")
    print(f"  identical : {checked - len(drifted) - 0}")
    print(f"  drifted   : {len(drifted)}")
    print(f"  environment problems (not drift): {len(errors)}")

    if drifted:
        print("\nCARDS THAT DID NOT REPRODUCE:")
        for path, diffs in drifted:
            print(f"  {path}")
            for d in diffs:
                print(f"      {d}")

    report = {
        "results": str(results_path),
        "checked": checked,
        "repeat": args.repeat,
        "seconds": round(elapsed),
        "drifted": [{"path": p, "diffs": d} for p, d in drifted],
        "environment": [{"path": p, "status": s, "error": e} for p, s, e in errors],
    }
    dest = out_dir / "determinism.json"
    dest.write_text(json.dumps(report, indent=1))
    print(f"\nwrote {dest}")
    return 1 if drifted else 0


if __name__ == "__main__":
    raise SystemExit(main())
