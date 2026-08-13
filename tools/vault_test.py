"""Exercise SMSLIBRE against every candidate card in a data vault.

Detection alone proves little — a reader can claim a folder and then produce
nothing useful. So the sweep runs in two phases:

    1. discovery   one `smsimport scan` process walks the whole tree
    2. per card    import -> open the GeoPackage -> validate geometry and attributes

Discovery is one process on purpose. Detection itself is cheap, but starting the
sidecar loads every ADAPT plugin from the SMS install and costs seconds, so
spawning it per directory put a full-vault sweep out of reach: 1,669 directories
took two hours that way. In one process the same tree walks at about 17
directories a second — 7,311 in 433 s — which leaves the time budget for the
imports, the part that can actually fail.

Every outcome is recorded, including the failures and the directories nothing
claimed. The result is a coverage report (what imports, what does not, and why)
plus a machine-readable corpus the regression suite consumes.

    python tools/vault_test.py --root "<vault path>" --out analysis/vault
    python tools/vault_test.py --resume           # skip candidates already done
    python tools/vault_test.py --only-detected    # re-import after a sidecar change
"""

from __future__ import annotations

import argparse
import concurrent.futures as futures
import json
import math
import os
import sqlite3
import struct
import subprocess
import sys
import time
from dataclasses import dataclass, asdict, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_EXE = (Path(os.environ.get("APPDATA", "")) /
               "QGIS/QGIS3/profiles/default/python/plugins/smslibre_import/bin/SmsImport.exe")


@dataclass
class Result:
    path: str
    detected: str = ""            # reader name, or "" when nothing claimed it
    status: str = "not-detected"  # not-detected | ok | empty | error
    layers: int = 0
    features: int = 0
    max_channels: int = 0
    operations: list[str] = field(default_factory=list)
    invalid_geom: int = 0
    out_of_range: int = 0
    static_layers: int = 0        # layers whose points barely move
    seconds: float = 0.0
    error: str = ""
    retried: bool = False         # a transient I/O failure forced a second attempt
    exts: list[str] = field(default_factory=list)   # file types present, for triage


# Failures that say nothing about the data — a flaky network mount, a file the
# sync client had not materialised yet, a lock held by another reader.
TRANSIENT = (
    "incorrect function", "the specified network name is no longer available",
    "device is not ready", "being used by another process", "semaphore timeout",
    "network path was not found", "i/o error", "unspecified error",
)


def is_transient(message: str) -> bool:
    m = message.lower()
    return any(t in m for t in TRANSIENT)


def run_sidecar(exe: Path, args: list[str], timeout: int) -> dict:
    """Invoke the sidecar and parse its single JSON object."""
    creation = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
    proc = subprocess.run([str(exe)] + args, capture_output=True, text=True,
                          timeout=timeout, creationflags=creation)
    out = (proc.stdout or "").strip()
    if not out:
        raise RuntimeError((proc.stderr or "no output").strip()[:200])
    return json.loads(out)


def _wkb_coords(blob: bytes, off: int) -> list[tuple[float, float]]:
    """Coordinates of one WKB geometry, following into rings and parts.

    The writer emits little-endian WKB with no envelope, so the geometry starts
    at byte 8 of the GeoPackage blob. Points were the only type checked before,
    which meant field boundaries — the whole output of a setup card — went
    through the validator untouched.
    """
    typ = struct.unpack_from("<I", blob, off + 1)[0]
    off += 5
    pts: list[tuple[float, float]] = []

    if typ == 1:                                   # Point
        pts.append(struct.unpack_from("<dd", blob, off))
    elif typ == 2:                                 # LineString
        (n,) = struct.unpack_from("<I", blob, off)
        off += 4
        for _ in range(n):
            pts.append(struct.unpack_from("<dd", blob, off)); off += 16
    elif typ == 3:                                 # Polygon
        (rings,) = struct.unpack_from("<I", blob, off)
        off += 4
        for _ in range(rings):
            (n,) = struct.unpack_from("<I", blob, off)
            off += 4
            for _ in range(n):
                pts.append(struct.unpack_from("<dd", blob, off)); off += 16
    elif typ in (4, 5, 6, 7):                      # Multi* / GeometryCollection
        (parts,) = struct.unpack_from("<I", blob, off)
        off += 4
        for _ in range(parts):
            sub = _wkb_coords(blob, off)
            pts.extend(sub)
            off = _wkb_end(blob, off)
    return pts


def _wkb_end(blob: bytes, off: int) -> int:
    """Byte just past the geometry starting at off — needed to walk Multi* parts."""
    typ = struct.unpack_from("<I", blob, off + 1)[0]
    off += 5
    if typ == 1:
        return off + 16
    if typ == 2:
        (n,) = struct.unpack_from("<I", blob, off)
        return off + 4 + n * 16
    if typ == 3:
        (rings,) = struct.unpack_from("<I", blob, off)
        off += 4
        for _ in range(rings):
            (n,) = struct.unpack_from("<I", blob, off)
            off += 4 + n * 16
        return off
    (parts,) = struct.unpack_from("<I", blob, off)
    off += 4
    for _ in range(parts):
        off = _wkb_end(blob, off)
    return off


def validate_gpkg(path: Path) -> tuple[int, int, int, int, int, int]:
    """Open the GeoPackage and check what actually landed in it."""
    layers = features = invalid = out_of_range = static = 0
    max_ch = 0
    db = sqlite3.connect(path)
    try:
        c = db.cursor()
        if c.execute("PRAGMA application_id").fetchone()[0] != 1196444487:
            raise RuntimeError("not a GeoPackage")
        tables = [r[0] for r in c.execute("SELECT table_name FROM gpkg_contents")]
        layers = len(tables)
        for t in tables:
            cols = [d[1] for d in c.execute(f'PRAGMA table_info("{t}")')]
            # fid, geom and timestamp are structural; the rest are channels.
            max_ch = max(max_ch, max(0, len(cols) - 3))
            n = c.execute(f'SELECT COUNT(*) FROM "{t}"').fetchone()[0]
            features += n
            lons, lats = [], []
            for (blob,) in c.execute(f'SELECT geom FROM "{t}"'):
                if not blob or len(blob) < 21 or blob[:2] != b"GP":
                    invalid += 1
                    continue
                if blob[3] & 0b0000_1110:
                    # An envelope would shift the WKB; the writer emits none, so
                    # this means the blob did not come from us.
                    invalid += 1
                    continue
                try:
                    pts = _wkb_coords(blob, 8)
                except (struct.error, IndexError):
                    invalid += 1
                    continue
                if not pts:
                    invalid += 1
                    continue
                for lon, lat in pts:
                    if not (math.isfinite(lon) and math.isfinite(lat)) \
                       or abs(lat) > 90 or abs(lon) > 180:
                        out_of_range += 1
                    else:
                        lons.append(lon); lats.append(lat)
            if lons and max(max(lons) - min(lons), max(lats) - min(lats)) < 5e-4:
                static += 1
    finally:
        db.close()
    return layers, features, max_ch, invalid, out_of_range, static


# Extensions that identify machine data even when no reader claims the folder.
# Used to separate "we cannot read this" from "there is nothing here to read".
DATA_EXTS = {
    ".xml": "ISOXML/TASKDATA", ".bin": "ISOXML binary log", ".jdp": "Raven Slingshot",
    ".zip": "archive", ".db": "SQLite (JD GS3/GS4)", ".jdl": "John Deere Gen4 log",
    ".cn1": "CNH Voyager", ".fmd": "CNH Voyager", ".fld": "CNH Voyager",
    ".agdata": "Trimble AgData", ".dat": "raw log", ".ilf": "Ag Leader",
    ".yld": "Ag Leader yield", ".pf": "Ag Leader", ".shp": "shapefile",
    ".csv": "text export", ".pdf": "document", ".jpg": "image", ".png": "image",
    ".docx": "document", ".xlsx": "spreadsheet",
}


def fingerprint(path: str, cap: int = 400) -> list[str]:
    """Extensions directly inside a folder — cheap triage for what it holds."""
    seen: dict[str, int] = {}
    try:
        with os.scandir(path) as it:
            for i, e in enumerate(it):
                if i >= cap:
                    break
                if e.is_file():
                    ext = os.path.splitext(e.name)[1].lower()
                    if ext:
                        seen[ext] = seen.get(ext, 0) + 1
    except OSError:
        return []
    return [e for e, _ in sorted(seen.items(), key=lambda kv: -kv[1])[:8]]


def discover(exe: Path, root: str, depth: int, cap: int, timeout: int,
             out_dir: Path, reuse: bool = False) -> tuple[dict[str, str], list[dict]]:
    """Every directory a reader claims, from a single sidecar process.

    Detection is cheap but starting the sidecar is not: it loads every ADAPT
    plugin from the SMS install, which costs seconds. Spawning it once per
    directory put a full-vault sweep out of reach — 6,000+ directories at ~4 s
    each. `smsimport scan` walks the tree inside one process, so the plugin load
    is paid once: measured at 7,311 directories in 433 s, about 17 a second.

    Output goes to files rather than pipes. A vault-wide walk runs for the better
    part of an hour, and when one timed out with everything buffered in memory
    there was nothing left to show for it — no partial list, no idea how far it
    had reached. On disk, the log is readable while it runs and survives a kill.
    """
    scan_json = out_dir / "scan.json"
    scan_log = out_dir / "scan.log"
    if reuse and scan_json.is_file() and scan_json.stat().st_size:
        # The walk is the expensive half and its answer does not change when the
        # import code does, so a re-run after a sidecar fix can skip it.
        print(f"  reusing {scan_json}", flush=True)
    else:
        creation = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
        with open(scan_json, "w", encoding="utf-8") as so, \
             open(scan_log, "w", encoding="utf-8") as se:
            subprocess.run([str(exe), "scan", root, "--depth", str(depth),
                            "--max", str(cap)],
                           stdout=so, stderr=se, timeout=timeout,
                           creationflags=creation)

    text = scan_json.read_text(encoding="utf-8").strip()
    if not text:
        raise RuntimeError(f"scan produced no result; see {scan_log}")
    res = json.loads(text)

    hits = {}
    for f in res.get("found") or []:
        plugins = f.get("plugins") or []
        if plugins:
            hits[f["path"]] = plugins[0].get("Name", "?")
    # The same walk reports what it rejected and what those folders hold, so the
    # "what does not import" half of the report costs no extra traversal.
    return hits, res.get("unclaimed") or []


def test_candidate(exe: Path, path: str, out_dir: Path, timeout: int,
                   detected: str = "") -> Result:
    r = Result(path=path)
    t0 = time.time()
    r.exts = fingerprint(path)
    try:
        if not detected:
            r.seconds = time.time() - t0
            return r
        r.detected = detected

        safe = "".join(ch if ch.isalnum() else "_" for ch in path)[-90:]
        gpkg = out_dir / f"{safe}.gpkg"
        gpkg.unlink(missing_ok=True)

        imp = run_sidecar(exe, ["import", path, str(gpkg)], timeout=timeout)
        if not imp.get("ok") and is_transient(str(imp.get("error", ""))):
            # The vault lives on a Google Drive shared drive, where a read can
            # fail with "Incorrect function." while the file is perfectly fine.
            # Retrying once separates a flaky mount from a format we cannot read;
            # without it the report blames the importer for the network.
            r.retried = True
            gpkg.unlink(missing_ok=True)
            time.sleep(5)
            imp = run_sidecar(exe, ["import", path, str(gpkg)], timeout=timeout)
        if not imp.get("ok"):
            r.status, r.error = "error", str(imp.get("error", ""))[:200]
            r.seconds = time.time() - t0
            return r

        lyrs = imp.get("layers") or []
        r.operations = sorted({l.get("operationType", "") for l in lyrs if l.get("operationType")})
        if not lyrs or not gpkg.exists():
            r.status = "empty"
            r.seconds = time.time() - t0
            return r

        (r.layers, r.features, r.max_channels,
         r.invalid_geom, r.out_of_range, r.static_layers) = validate_gpkg(gpkg)
        r.status = "ok" if r.features else "empty"
        # keep the corpus small: the report holds the numbers, not the data
        gpkg.unlink(missing_ok=True)
    except subprocess.TimeoutExpired:
        r.status, r.error = "error", f"timed out after {timeout}s"
    except Exception as exc:                              # noqa: BLE001
        r.status, r.error = "error", f"{type(exc).__name__}: {exc}"[:200]
    r.seconds = time.time() - t0
    return r


def deepest_cards(paths: dict[str, str]) -> dict[str, str]:
    """Drop a claimed directory when a descendant is claimed by the same reader.

    Readers match recursively: ISOv4 claims any folder with a TASKDATA anywhere
    beneath it, which means it claims the crop folder, the year folder, the card
    — and the vault root. Taking the outermost hit would make the whole vault one
    "card" and one enormous import; taking the innermost gives the folder that
    actually holds the data, and per-card numbers in the report.

    Only same-reader nesting collapses. A different reader claiming a subfolder
    is a different card the parent's import did not cover, and dropping it hides
    real failures: the Trimble licence error sits inside a folder the ISOXML
    reader imports happily.
    """
    keep = {}
    for path, reader in paths.items():
        prefix = path + os.sep
        if any(other.startswith(prefix) and r == reader
               for other, r in paths.items()):
            continue          # a descendant covers this one
        keep[path] = reader
    return keep


def _distinct_cards(rows: list[dict]) -> list[dict]:
    """The report's view of `deepest_cards`, applied to result rows."""
    readers = {r["path"]: r["detected"] for r in rows}
    keep = deepest_cards(readers)
    return [r for r in rows if r["path"] in keep]


def build_report(results: list[dict], root: str) -> str:
    hits = [r for r in results if r.get("detected")]
    cards = _distinct_cards(hits)
    ok = [r for r in cards if r["status"] == "ok"]
    empty = [r for r in cards if r["status"] == "empty"]
    err = [r for r in cards if r["status"] == "error"]
    miss = [r for r in results if not r.get("detected")]

    L = [
        "# Vault import coverage",
        "",
        f"Root: `{root}`",
        "",
        f"Walked **{len(results):,} directories**. A reader claimed **{len(hits)}** of "
        f"them, which collapse to **{len(cards)} distinct cards** once nested "
        "parent/child hits are merged.",
        "",
        "| Outcome | Cards |",
        "|---|--:|",
        f"| Imported with data | {len(ok)} |",
        f"| Detected but empty | {len(empty)} |",
        f"| Detected but failed | {len(err)} |",
        f"| No reader | {len(miss):,} directories |",
        "",
        f"Total features imported: **{sum(r['features'] for r in ok):,}** "
        f"across **{sum(r['layers'] for r in ok):,}** layers.",
        "",
        "## By reader",
        "",
        "| Reader | Cards | Layers | Features | Max channels | Empty | Failed |",
        "|---|--:|--:|--:|--:|--:|--:|",
    ]
    for name in sorted({r["detected"] for r in cards}):
        g = [r for r in cards if r["detected"] == name]
        g_ok = [r for r in g if r["status"] == "ok"]
        L.append(
            f"| {name} | {len(g_ok)} | {sum(r['layers'] for r in g_ok):,} | "
            f"{sum(r['features'] for r in g_ok):,} | "
            f"{max((r['max_channels'] for r in g_ok), default=0)} | "
            f"{sum(1 for r in g if r['status'] == 'empty')} | "
            f"{sum(1 for r in g if r['status'] == 'error')} |")

    L += ["", "## Cards that imported", "",
          "| Card | Reader | Layers | Features | Operations | s |",
          "|---|---|--:|--:|---|--:|"]
    for r in sorted(ok, key=lambda r: -r["features"]):
        ops = ", ".join(r.get("operations") or []) or "—"
        L.append(f"| `{Path(r['path']).name}` | {r['detected']} | {r['layers']:,} | "
                 f"{r['features']:,} | {ops} | {r['seconds']:.0f} |")

    if err:
        L += ["", "## Detected but failed", "",
              "| Card | Reader | Cause | Error |", "|---|---|---|---|"]
        for r in sorted(err, key=lambda r: r["path"]):
            e = r["error"]
            cause = ("timeout" if "timed out" in e
                     else "environment" if is_transient(e)
                     else "licence" if "licen" in e.lower() or "not initialized" in e.lower()
                     else "format")
            L.append(f"| `{Path(r['path']).name}` | {r['detected']} | {cause} | "
                     f"{e.replace('|', '/')[:150]} |")
        L += ["", "`licence` means a vendor plugin loaded but refused our "
              "application id — no code change fixes it. `environment` and "
              "`timeout` are the network share, not the data: re-run those. "
              "`format` is the only category that indicates a real gap."]
        retried = sum(1 for r in cards if r.get("retried"))
        if retried:
            L += ["", f"{retried} card(s) needed a retry after a transient read "
                      "failure on the shared drive."]

    if empty:
        L += ["", "## Detected but empty", "",
              "A reader claimed the folder and returned no spatial data. Usually a "
              "setup or prescription card with no logged work, or a parent folder "
              "whose data sits in a subfolder that is listed above.", "",
              "| Card | Reader |", "|---|---|"]
        for r in sorted(empty, key=lambda r: r["path"]):
            L.append(f"| `{Path(r['path']).name}` | {r['detected']} |")

    # Quality flags — these are the reasons to distrust a number above.
    flagged = [r for r in ok if r["invalid_geom"] or r["out_of_range"]]
    static = [r for r in ok if r["static_layers"]]
    L += ["", "## Data quality", ""]
    if flagged:
        L += [f"**{len(flagged)} card(s) still carry invalid or out-of-range geometry** "
              "— the coordinate guard is not catching them:", ""]
        L += [f"- `{Path(r['path']).name}`: {r['invalid_geom']} invalid, "
              f"{r['out_of_range']} out of range" for r in flagged]
    else:
        L.append("No invalid or out-of-range geometry survived import. Corrupt GPS "
                 "fixes present in the source are rejected by `Coordinates.IsPlausible`.")
    if static:
        L += ["", f"{len(static)} card(s) contain layers whose points span under ~50 m "
              "(`static_layers`). That is normal for stationary logging and summary "
              "records, not necessarily a defect:", ""]
        L += [f"- `{Path(r['path']).name}`: {r['static_layers']} of {r['layers']} layers"
              for r in sorted(static, key=lambda r: -r["static_layers"])[:10]]

    # What we could not read, characterised by the file types actually present.
    interesting: dict[str, int] = {}
    for r in miss:
        for e in r.get("exts") or []:
            if e in DATA_EXTS:
                interesting[e] = interesting.get(e, 0) + 1
    if interesting:
        L += ["", "## Directories no reader claimed", "",
              "File types found in the unclaimed directories. Documents and images "
              "are expected; a data extension appearing here is a gap worth chasing.",
              "", "| Extension | Meaning | Directories |", "|---|---|--:|"]
        for e, n in sorted(interesting.items(), key=lambda kv: -kv[1]):
            L.append(f"| `{e}` | {DATA_EXTS[e]} | {n:,} |")

    L.append("")
    return "\n".join(L)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--out", default="analysis/vault")
    ap.add_argument("--exe", default=str(DEFAULT_EXE))
    ap.add_argument("--depth", type=int, default=6)
    ap.add_argument("--cap", type=int, default=4000)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--timeout", type=int, default=1800,
                    help="seconds allowed for importing one card")
    ap.add_argument("--scan-timeout", type=int, default=14400,
                    help="seconds allowed for the whole discovery walk; a vault "
                         "on a network share can take the better part of an hour")
    ap.add_argument("--resume", action="store_true")
    ap.add_argument("--only-detected", action="store_true",
                    help="re-test just the directories a reader already claimed, "
                         "refreshing their numbers. Use after changing the sidecar: "
                         "the discovery walk is the slow part and its answer does "
                         "not change, but the import numbers do.")
    ap.add_argument("--reuse-scan", action="store_true",
                    help="reuse the scan.json from a previous discovery walk "
                         "instead of walking the tree again")
    ap.add_argument("--report", default="COVERAGE.md",
                    help="markdown coverage report to write beside results.json")
    args = ap.parse_args()

    exe = Path(args.exe)
    if not exe.is_file():
        print(f"sidecar not found: {exe}", file=sys.stderr)
        return 2

    out_dir = ROOT / args.out
    out_dir.mkdir(parents=True, exist_ok=True)
    results_path = out_dir / "results.json"

    done: dict[str, dict] = {}
    if args.resume and results_path.exists():
        done = {d["path"]: d for d in json.loads(results_path.read_text())}
        print(f"resuming: {len(done)} already tested")

    t0 = time.time()
    if args.only_detected:
        if not results_path.exists():
            print(f"no prior run to re-test: {results_path}", file=sys.stderr)
            return 2
        prior = json.loads(results_path.read_text())
        readers = {d["path"]: d["detected"] for d in prior if d.get("detected")}
        # keep the untouched rows; the re-tested ones are replaced below
        done = {d["path"]: d for d in prior if not d.get("detected")}
        unclaimed = []          # carried over untouched in `done`
        print(f"re-testing {len(readers)} detected directories "
              f"({len(done)} not-detected rows carried over)")
    else:
        print(f"discovering readers under {args.root} …", flush=True)
        readers, unclaimed = discover(exe, args.root, args.depth, args.cap,
                                      args.scan_timeout, out_dir,
                                      args.reuse_scan)
        nested = len(readers)
        readers = deepest_cards(readers)
        print(f"  {len(readers)} cards claimed ({nested - len(readers)} outer "
              f"folders dropped as duplicates), {len(unclaimed):,} not "
              f"({time.time()-t0:.0f}s)", flush=True)

    cands = [c for c in readers if c not in done]
    print(f"{len(cands)} directories to import (workers={args.workers})", flush=True)

    results = list(done.values())
    # Directories nothing claimed still belong in the report: they are the
    # "what doesn't import" half of the question, and their file types say
    # whether that is a real gap or just a folder of PDFs.
    for u in unclaimed:
        if u["path"] not in done:
            results.append(asdict(Result(path=u["path"], exts=u.get("exts") or [])))

    with futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futs = {pool.submit(test_candidate, exe, c, out_dir, args.timeout,
                            readers[c]): c for c in cands}
        for i, f in enumerate(futures.as_completed(futs), 1):
            r = f.result()
            results.append(asdict(r))
            print(f"  [{i}/{len(cands)}] {r.status:11} {r.detected:26} "
                  f"{r.layers:4}L {r.features:>9,}F  {Path(r.path).name[:44]}",
                  flush=True)
            if i % 10 == 0:
                results_path.write_text(json.dumps(results, indent=1))

    results_path.write_text(json.dumps(results, indent=1))
    hits = [r for r in results if r["detected"]]
    print(f"\ntested {len(results)} dirs in {time.time()-t0:.0f}s; "
          f"{len(hits)} detected")
    print(f"wrote {results_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
