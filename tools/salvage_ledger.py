"""Compute the native-Linux salvage ledger for the SMS assemblies.

Combines three inputs produced by tools/decompiler:
  - assembly-classification.csv : native / pure-IL / mixed-mode (C++/CLI) per file
  - deps-refs.csv               : assembly -> referenced-assembly edges (+ Win-only flag)
  - deps-pinvoke.csv            : assembly -> native DLL P/Invoke targets

Classification model
--------------------
Taint only propagates from things that genuinely cannot run on native Linux
.NET, and only through *SMS's own* assemblies:

  * CORE taint source  = a first-party assembly that is C++/CLI mixed-mode or a
    native PE (its logic is Windows machine code).
  * WPF taint source    = any assembly that references WPF/WinForms.

References to the .NET BCL (System.*, netstandard) and to normal managed NuGet
libraries (log4net, Newtonsoft, DotSpatial, the ADAPT ecosystem, ...) are
*portable* and never taint — that runtime and those packages exist on Linux.
A P/Invoke into a Windows-only native DLL (advapi32, crypt32, ...) is recorded
as a caveat to stub, not a disqualifier, because such calls are typically on
Windows-only codepaths guarded at runtime.

Buckets (priority CORE > WPF > REUSABLE):
  CORE-REIMPLEMENT   is / transitively needs the C++/CLI native core.
  WPF-UI             needs WPF/WinForms (rebuild in a native toolkit).
  NATIVE-REUSABLE    pure managed, runs on Linux .NET as-is (maybe with a small
                     Windows-P/Invoke stub).
"""

from __future__ import annotations

import csv
import sys
from collections import defaultdict
from pathlib import Path

INV = Path(__file__).resolve().parents[1] / "analysis" / "inventory"

WIN_PINVOKE_HINT = {  # Windows-only native DLLs commonly P/Invoked
    "advapi32", "crypt32", "kernel32", "user32", "gdi32", "gdiplus", "ole32",
    "oleaut32", "shell32", "shlwapi", "wininet", "winhttp", "ws2_32", "comctl32",
    "comdlg32", "coredll", "ntdll", "secur32", "dwmapi", "uxtheme", "psapi",
    "version", "iphlpapi", "setupapi", "userenv", "winspool.drv",
}


def _norm(dll: str) -> str:
    d = dll.lower()
    for suf in (".dll", ".so"):
        if d.endswith(suf):
            d = d[: -len(suf)]
    return d


def is_first_party(name: str) -> bool:
    return name.startswith(("AL", "AgLeader", "AgFiniti", "AgLV"))


def is_adapt(name: str) -> bool:
    return name.startswith((
        "AgGateway", "JohnDeere", "PrecisionPlanting", "Trimble", "CNH",
        "Climate", "Raven", "crop-list", "Adapt",
    ))


def load():
    cls = {}
    with open(INV / "assembly-classification.csv", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            cls[Path(r["Name"]).stem] = r
    edges = defaultdict(set)
    win_ref = defaultdict(bool)
    with open(INV / "deps-refs.csv", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            edges[r["Assembly"]].add(r["RefAssembly"])
            if r["RefIsWindowsOnly"].strip().lower() == "true":
                win_ref[r["Assembly"]] = True
    pinvoke = defaultdict(set)
    with open(INV / "deps-pinvoke.csv", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            pinvoke[r["Assembly"]].add(_norm(r["NativeDll"]))
    return cls, edges, win_ref, pinvoke


def _closure(seed: set[str], edges, universe: set[str]) -> set[str]:
    """All nodes in `universe` that can reach any node in `seed` via edges.

    (i.e. taint flows from a dependency up to whatever references it.)
    """
    tainted = set(seed)
    changed = True
    while changed:
        changed = False
        for a, deps in edges.items():
            if a in tainted or a not in universe:
                continue
            if deps & tainted:
                tainted.add(a)
                changed = True
    return tainted


def main() -> int:
    cls, edges, win_ref, pinvoke = load()

    # Universe for taint = first-party assemblies only (BCL / NuGet are portable
    # and must not carry taint between SMS components).
    fp = {n for n in set(cls) | set(edges)
          if is_first_party(n) and not n.endswith(".resources")}

    core_seed = {n for n in fp if cls.get(n, {}).get("Kind") in ("mixed-mode", "native")}
    wpf_seed = {n for n in fp if win_ref.get(n)}

    core = _closure(core_seed, edges, fp)
    wpf = _closure(wpf_seed, edges, fp)

    rows = []
    for n in sorted(fp):
        kind = cls.get(n, {}).get("Kind", "?")
        size = cls.get(n, {}).get("SizeKB", "")
        win_pi = sorted(d for d in pinvoke.get(n, set()) if d in WIN_PINVOKE_HINT)
        caveat = ("win P/Invoke: " + ",".join(win_pi)) if win_pi else ""
        if n in core:
            bucket, why = "CORE-REIMPLEMENT", (
                "native core" if n in core_seed else "needs native core")
        elif n in wpf:
            bucket, why = "WPF-UI", (
                "references WPF/WinForms" if n in wpf_seed else "needs WPF assembly")
        else:
            bucket, why = "NATIVE-REUSABLE", (caveat or "pure managed")
        rows.append((bucket, n, kind, size, why))

    # Third-party managed libs bundled with SMS (ADAPT etc.) — the "free engine".
    tp = sorted({n for n in set(cls)
                 if is_adapt(n) and cls.get(n, {}).get("Kind") == "pure-IL"
                 and not n.endswith(".resources")})

    order = ["NATIVE-REUSABLE", "WPF-UI", "CORE-REIMPLEMENT"]
    buckets = defaultdict(list)
    for b, n, *_ in rows:
        buckets[b].append(n)

    print("=== NATIVE SALVAGE LEDGER — first-party SMS assemblies ===\n")
    for b in order:
        print(f"  {b:18} {len(buckets[b]):3d}")
    print(f"  {'TOTAL':18} {len(rows):3d}")
    print(f"\n  + {len(tp)} third-party ADAPT/vendor managed libs — all pure-IL, "
          f"native-reusable (the import engine)\n")

    with open(INV / "salvage-ledger.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Bucket", "Assembly", "Kind", "SizeKB", "Reason"])
        for r in sorted(rows):
            w.writerow(r)
        for n in tp:
            w.writerow(["THIRD-PARTY-REUSABLE", n,
                        cls.get(n, {}).get("Kind", ""),
                        cls.get(n, {}).get("SizeKB", ""), "ADAPT/vendor import lib"])

    for b in order:
        print(f"--- {b} ({len(buckets[b])}) ---")
        for r in sorted(x for x in rows if x[0] == b):
            _, n, kind, size, why = r
            print(f"  {n:34} {kind:11} {why}")
        print()
    print(f"--- THIRD-PARTY-REUSABLE ({len(tp)}) ---")
    print("  " + ", ".join(tp))
    return 0


if __name__ == "__main__":
    sys.exit(main())
