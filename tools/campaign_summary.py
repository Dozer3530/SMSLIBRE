"""Combine every campaign result into one report.

The per-drive COVERAGE.md files answer "what happened on this drive". This
answers the question the campaign was run to settle: across everything we have,
what imports, what does not, and did any of it change between runs.
"""

from __future__ import annotations

import json
import os
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import vault_test as vt  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[1]
CAMPAIGN = ROOT / "analysis" / "campaign"

DRIVES = [
    ("vault", "Olds College Smart Farm Vault"),
    ("staar", "210600 STAAR"),
    ("sfdata", "M: sfdata (olsmartfsrv)"),
]


def load(name):
    p = CAMPAIGN / name / "results.json"
    return json.loads(p.read_text()) if p.is_file() else None


def main() -> int:
    lines = ["# Campaign results", ""]
    totals = {"dirs": 0, "cards": 0, "ok": 0, "features": 0,
              "layers": 0, "failed": 0, "empty": 0, "disk": 0}
    per_drive = []

    for key, name in DRIVES:
        rows = load(key)
        if rows is None:
            per_drive.append((name, None))
            continue

        cards = vt._distinct_cards([r for r in rows if r.get("detected")])
        dupes = vt.nested_duplicates(cards)
        ok = [r for r in cards if r["status"] == "ok"]
        counted = [r for r in ok if r["path"] not in dupes]
        stats = {
            "dirs": len(rows),
            "cards": len(cards),
            "ok": len(ok),
            "features": sum(r["features"] for r in counted),
            "layers": sum(r["layers"] for r in counted),
            "failed": sum(1 for r in cards if r["status"] == "error"),
            "empty": sum(1 for r in cards if r["status"] == "empty"),
            "disk": sum(1 for r in cards if r["status"] == "skipped-disk"),
            "bad_geom": sum(r["invalid_geom"] + r["out_of_range"] for r in ok),
        }
        per_drive.append((name, stats))
        for k in totals:
            totals[k] += stats[k]

    lines += ["## Coverage", "",
              "| Drive | Dirs | Cards | Imported | Features | Layers | Failed |",
              "|---|--:|--:|--:|--:|--:|--:|"]
    for name, s in per_drive:
        if s is None:
            lines.append(f"| {name} | — | — | — | not swept | — | — |")
            continue
        lines.append(f"| {name} | {s['dirs']:,} | {s['cards']:,} | {s['ok']:,} | "
                     f"{s['features']:,} | {s['layers']:,} | {s['failed']} |")
    lines.append(f"| **All drives** | **{totals['dirs']:,}** | **{totals['cards']:,}** | "
                 f"**{totals['ok']:,}** | **{totals['features']:,}** | "
                 f"**{totals['layers']:,}** | **{totals['failed']}** |")
    lines.append("")

    bad = sum(s["bad_geom"] for _, s in per_drive if s)
    lines.append(f"Invalid or out-of-range geometry surviving import: **{bad}**."
                 if bad else
                 "**No invalid or out-of-range geometry survived import on any drive.**")
    if totals["disk"]:
        lines.append(f"\n{totals['disk']} card(s) were skipped because the disk "
                     "was low on space — re-run those; they are not failures.")
    lines.append("")

    # ---- determinism ------------------------------------------------------
    lines += ["## Reproducibility", ""]
    any_det = False
    for key, name in DRIVES:
        p = CAMPAIGN / key / "determinism.json"
        if not p.is_file():
            continue
        any_det = True
        d = json.loads(p.read_text())
        drifted = d.get("drifted") or []
        env = d.get("environment") or []
        verdict = ("every card reproduced exactly" if not drifted
                   else f"**{len(drifted)} card(s) did not reproduce**")
        lines.append(f"- **{name}** — {d['checked']} card(s) re-imported, "
                     f"{verdict}"
                     + (f"; {len(env)} environment problem(s)" if env else "") + ".")
        for entry in drifted:
            lines.append(f"    - `{pathlib.Path(entry['path']).name}`: "
                         + "; ".join(entry["diffs"]))
    if not any_det:
        lines.append("_No determinism results found._")
    lines.append("")

    # ---- failures ---------------------------------------------------------
    lines += ["## Every failure, across all drives", ""]
    rows_out = []
    for key, name in DRIVES:
        rows = load(key)
        if not rows:
            continue
        for r in rows:
            if r["status"] == "error":
                rows_out.append((name, pathlib.Path(r["path"]).name,
                                 r.get("detected", ""), r["error"]))
    if rows_out:
        lines += ["| Drive | Card | Reader | Error |", "|---|---|---|---|"]
        for drive, card, reader, err in rows_out:
            lines.append(f"| {drive} | `{card}` | {reader} | "
                         f"{err.replace('|', '/')[:110]} |")
    else:
        lines.append("_No failures._")
    lines.append("")

    # ---- gaps -------------------------------------------------------------
    lines += ["## Data no reader claimed", "",
              "Folders holding machine-data file types that no reader took, and "
              "that are not inside a card which was imported. This is the "
              "improvement backlog.", "",
              "| Drive | Extension | Folders |", "|---|---|--:|"]
    DATA = {".jdp", ".jdl", ".db", ".bin", ".cn1", ".dat", ".ilf",
            ".yld", ".agdata", ".fmd", ".fld"}
    for key, name in DRIVES:
        rows = load(key)
        if not rows:
            continue
        claimed = [r["path"] for r in rows if r.get("detected")]
        counts: dict[str, int] = {}
        for r in rows:
            if r.get("detected"):
                continue
            if any(r["path"].startswith(c + os.sep) for c in claimed):
                continue
            for e in r.get("exts") or []:
                if e in DATA:
                    counts[e] = counts.get(e, 0) + 1
        for e, n in sorted(counts.items(), key=lambda kv: -kv[1])[:6]:
            lines.append(f"| {name} | `{e}` | {n:,} |")
    lines.append("")

    out = CAMPAIGN / "CAMPAIGN.md"
    out.write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(lines[:40]))
    print(f"\nwrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
