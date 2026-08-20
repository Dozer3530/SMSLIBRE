"""Build the diagrams and data figures used by the user guide.

The map figures are rendered from a GeoPackage this project actually produced,
not drawn by hand — a guide that shows invented output teaches the wrong thing.

    python docs/make_figures.py

docs/sample.gpkg is the figure source and is too large for git. Recreate it by
importing any card with rate data, e.g.

    SmsImport.exe import "<a Raven Jobs folder>" docs/sample.gpkg

The coverage chart reads analysis/vault/results.json, which is committed.
"""

import json
import math
import pathlib
import sqlite3
import struct
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt                     # noqa: E402
from matplotlib.patches import FancyArrowPatch, FancyBboxPatch  # noqa: E402

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent
OUT = HERE / "images"
OUT.mkdir(parents=True, exist_ok=True)

INK = "#1a1a1a"
ACCENT = "#2f6f4e"
MUTED = "#8a8a8a"
BAD = "#a4392f"


def save(fig, name):
    path = OUT / f"{name}.png"
    fig.savefig(path, dpi=170, bbox_inches="tight", facecolor="white")
    plt.close(fig)
    print(f"  wrote {path.name}  ({path.stat().st_size // 1024} KB)")


# --------------------------------------------------------------------------
# Reading points back out of a GeoPackage we wrote.
# --------------------------------------------------------------------------

def read_points(gpkg, table, value_field):
    """(lon, lat, value) for every feature; geometry is GP header + WKB point."""
    db = sqlite3.connect(gpkg)
    try:
        rows = db.execute(
            f'SELECT geom, "{value_field}" FROM "{table}"').fetchall()
    finally:
        db.close()
    out = []
    for blob, val in rows:
        if not blob or len(blob) < 21 or blob[:2] != b"GP":
            continue
        if struct.unpack_from("<I", blob, 9)[0] != 1:      # point only
            continue
        lon, lat = struct.unpack_from("<dd", blob, 13)
        out.append((lon, lat, val))
    return out


# --------------------------------------------------------------------------
# 1. Architecture
# --------------------------------------------------------------------------

def architecture():
    fig, ax = plt.subplots(figsize=(10, 3.4))
    ax.set_xlim(0, 10); ax.set_ylim(0, 3.4); ax.axis("off")

    boxes = [
        (0.15, "QGIS", "Import Machine Data\ndialog", "#eef3f7"),
        (2.65, "Sidecar", "smsimport.exe\n(.NET 10)", "#e8f1ec"),
        (5.15, "Readers", "9 format readers +\nvendor ADAPT plugins", "#f5f0e6"),
        (7.65, "Output", "GeoPackage\n(.gpkg)", "#eef3f7"),
    ]
    for x, title, body, colour in boxes:
        ax.add_patch(FancyBboxPatch((x, 1.15), 2.2, 1.15,
                                    boxstyle="round,pad=0.06,rounding_size=0.08",
                                    facecolor=colour, edgecolor=INK, linewidth=1.2))
        ax.text(x + 1.1, 2.02, title, ha="center", va="center",
                fontsize=11.5, fontweight="bold", color=INK)
        ax.text(x + 1.1, 1.55, body, ha="center", va="center",
                fontsize=9, color=INK)

    for x in (2.35, 4.85, 7.35):
        ax.add_patch(FancyArrowPatch((x, 1.72), (x + 0.3, 1.72),
                                     arrowstyle="-|>", mutation_scale=15,
                                     linewidth=1.4, color=INK))

    ax.text(3.75, 0.92, "one JSON object on stdout", ha="center",
            fontsize=8.5, style="italic", color=MUTED)
    ax.text(8.75, 0.92, "opened natively by QGIS", ha="center",
            fontsize=8.5, style="italic", color=MUTED)
    ax.text(5, 0.35,
            "The vendor plugins are .NET assemblies, so they run in a separate "
            "process:\na crash cannot take QGIS down with it.",
            ha="center", fontsize=9, color=INK)
    save(fig, "architecture")


# --------------------------------------------------------------------------
# 2. Where to point the plugin
# --------------------------------------------------------------------------

def card_structures():
    fig, axes = plt.subplots(1, 3, figsize=(11.5, 3.9))

    trees = [
        ("Point HERE", ACCENT,
         ["MyCard/            <- select this",
          "  GS3_2630/",
          "    Client Name/",
          "      RCD/",
          "        ContextData/",
          "        EIC/"]),
        ("Not here", BAD,
         ["MyCard/",
          "  GS3_2630/",
          "    Client Name/",
          "      RCD/         <- too deep",
          "        ContextData/",
          "        EIC/"]),
        ("Recovered automatically", ACCENT,
         ["Tyson lentils/",
          "  lentils 2026/    <- select this",
          "    RCD/           (GS3_2630 layer",
          "      ContextData/  missing - the",
          "      EIC/          plugin rebuilds",
          "                    it for you)"]),
    ]
    for ax, (title, colour, lines) in zip(axes, trees):
        ax.axis("off")
        ax.set_title(title, fontsize=11, fontweight="bold", color=colour, pad=8)
        for i, line in enumerate(lines):
            ax.text(0.02, 0.88 - i * 0.145, line, fontsize=8.6,
                    family="monospace", color=INK, va="top", transform=ax.transAxes)
        ax.add_patch(plt.Rectangle((0, 0), 1, 1, transform=ax.transAxes,
                                   fill=False, edgecolor=colour, linewidth=1.6))
    fig.suptitle("Select the folder that CONTAINS the display folder",
                 fontsize=12, fontweight="bold", y=1.03)
    save(fig, "card-structures")


# --------------------------------------------------------------------------
# 3. A real imported track, coloured by application rate
# --------------------------------------------------------------------------

def track_map(gpkg):
    db = sqlite3.connect(gpkg)
    tables = [r[0] for r in db.execute("SELECT table_name FROM gpkg_contents")]
    # Pick by what the data shows, not by layer name: the most useful figure is
    # the pass with real rate variation in it, where the sprayer switched on and
    # off. Naming a layer by hand picked one that was zero throughout.
    best, best_spread = None, -1.0
    for t in tables:
        try:
            lo, hi, n = db.execute(
                f'SELECT MIN(rate_applied), MAX(rate_applied), COUNT(*) '
                f'FROM "{t}" WHERE rate_applied IS NOT NULL').fetchone()
        except sqlite3.OperationalError:
            continue
        if lo is None or n < 500:
            continue
        if hi - lo > best_spread:
            best, best_spread = t, hi - lo
    db.close()
    table = best or tables[0]

    pts = [p for p in read_points(gpkg, table, "rate_applied") if p[2] is not None]
    if not pts or best_spread <= 0:
        print("  (no rate variation found; skipping track_map)")
        return

    fig, ax = plt.subplots(figsize=(7.6, 6))
    lon = [p[0] for p in pts]; lat = [p[1] for p in pts]; val = [p[2] for p in pts]
    sc = ax.scatter(lon, lat, c=val, s=3, cmap="RdYlGn", linewidths=0)
    cb = fig.colorbar(sc, ax=ax, shrink=0.82)
    cb.set_label("Applied rate (L/ha)", fontsize=9)

    ax.set_aspect(1 / math.cos(math.radians(sum(lat) / len(lat))))
    ax.set_xlabel("Longitude", fontsize=9)
    ax.set_ylabel("Latitude", fontsize=9)
    ax.set_title(f"Real import: {table}\n{len(pts):,} points, coloured by applied rate",
                 fontsize=11)
    ax.tick_params(labelsize=8)
    save(fig, "map-track-rate")


# --------------------------------------------------------------------------
# 4. Coverage by reader, from the committed sweep results
# --------------------------------------------------------------------------

def coverage_chart():
    results = REPO / "analysis" / "vault" / "results.json"
    if not results.is_file():
        print("  (no sweep results; skipping coverage_chart)")
        return
    rows = json.loads(results.read_text())

    totals = {}
    for r in rows:
        if r.get("status") == "ok" and r.get("detected"):
            totals[r["detected"]] = totals.get(r["detected"], 0) + r["features"]
    if not totals:
        return
    items = sorted(totals.items(), key=lambda kv: kv[1])

    fig, ax = plt.subplots(figsize=(9, 4.4))
    names = [k for k, _ in items]
    vals = [v / 1e6 for _, v in items]
    bars = ax.barh(names, vals, color=ACCENT, height=0.62)
    for bar, v in zip(bars, vals):
        ax.text(bar.get_width() + max(vals) * 0.012, bar.get_y() + bar.get_height() / 2,
                f"{v:,.1f}M", va="center", fontsize=8.5, color=INK)
    ax.set_xlabel("Features imported (millions)", fontsize=9)
    ax.set_title("Smart Farm Vault — features imported per reader", fontsize=11)
    ax.tick_params(labelsize=8.5)
    ax.spines[["top", "right"]].set_visible(False)
    ax.set_xlim(0, max(vals) * 1.13)
    save(fig, "coverage-by-reader")


def main():
    gpkg = HERE / "sample.gpkg"
    print("building figures")
    architecture()
    card_structures()
    if gpkg.is_file():
        track_map(str(gpkg))
    coverage_chart()
    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
