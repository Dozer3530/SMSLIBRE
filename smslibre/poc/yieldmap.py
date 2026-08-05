"""Core yield-map logic: load a boundary + yield layer and render an SMS-style map.

Kept UI-agnostic on purpose. ``load_field`` returns a plain dataclass and
``render`` draws onto any Matplotlib ``Axes``, so the same code backs both the
headless PNG exporter and the Qt viewer.
"""

from __future__ import annotations

from dataclasses import dataclass, field as _field
from pathlib import Path

import geopandas as gpd
import numpy as np
from matplotlib.axes import Axes
from matplotlib.figure import Figure

# Attribute names (case-insensitive) SMS/Trimble exports commonly use for the
# dry-yield-volume measure, most specific first. Auto-detection falls back to
# "first numeric column that isn't an id" if none of these are present.
YIELD_COLUMN_CANDIDATES = (
    "Yld_Vol_Dr", "YldVolDry", "Yield_Vol_Dry", "DryYield",
    "Yld_Mass_D", "Yield", "Yld", "VRYIELDVOL",
)

# Columns that look numeric but are identifiers, not measurements.
_ID_LIKE = ("obj", "id", "fid", "index", "ptid", "point")


@dataclass
class FieldData:
    """One field's boundary and yield points, reprojected to a metric CRS."""

    name: str
    boundary: gpd.GeoDataFrame          # polygon(s), metric CRS
    yield_pts: gpd.GeoDataFrame         # points, metric CRS
    yield_col: str                      # attribute holding the yield value
    metric_crs: str                     # e.g. "EPSG:32611"
    attrs: dict = _field(default_factory=dict)   # boundary metadata for titling

    @property
    def area_ha(self) -> float:
        return float(self.boundary.geometry.area.sum()) / 10_000.0

    @property
    def area_ac(self) -> float:
        return self.area_ha * 2.471053815

    @property
    def values(self) -> np.ndarray:
        return self.yield_pts[self.yield_col].to_numpy(dtype=float)


def _detect_yield_column(gdf: gpd.GeoDataFrame) -> str:
    lower = {c.lower(): c for c in gdf.columns}
    for cand in YIELD_COLUMN_CANDIDATES:
        if cand.lower() in lower:
            return lower[cand.lower()]
    # Fallback: first numeric, non-id-looking column with real variation.
    for c in gdf.columns:
        if c == "geometry":
            continue
        if any(tok in c.lower() for tok in _ID_LIKE):
            continue
        s = gpd.pd.to_numeric(gdf[c], errors="coerce")
        if s.notna().sum() and s.nunique() > 5:
            return c
    raise ValueError(
        "Could not detect a yield column; pass yield_col explicitly. "
        f"Columns present: {list(gdf.columns)}"
    )


def load_field(
    boundary_path: str | Path | None,
    yield_path: str | Path,
    *,
    yield_col: str | None = None,
    name: str | None = None,
    clean: bool = False,
    clip_pct: tuple[float, float] = (1.0, 99.0),
) -> FieldData:
    """Read a yield layer (+ optional boundary) and reproject to a metric CRS.

    Accepts any format GDAL/pyogrio reads (Shapefile, GeoJSON, ...). Working in
    metres (not lat/lon degrees) is what makes area, density, and aspect correct.

    ``boundary_path`` may be None (e.g. ISOXML yield with no boundary polygon) —
    a convex hull of the points stands in for the field outline.

    ``clean`` applies the basic filtering raw combine data needs before it is
    legible: drop non-positive values and clip to the ``clip_pct`` percentiles.
    (SMS's own yield cleaner does much more; this is a first approximation.)
    """
    ypts = gpd.read_file(yield_path)
    if ypts.crs is None:
        ypts.set_crs("EPSG:4326", inplace=True)

    # Drop empty/null-island geometries; raw combine logs carry stray GPS fixes.
    ypts = ypts[~ypts.geometry.is_empty & ypts.geometry.notna()]
    if clean:
        x, y = ypts.geometry.x, ypts.geometry.y
        ypts = ypts[(x.abs() > 1e-6) | (y.abs() > 1e-6)]           # not (0, 0)
        # Robust spatial clip: keep the bulk of points, discard far outliers that
        # would otherwise stretch the extent and confuse UTM-zone selection.
        qx = ypts.geometry.x.quantile([0.005, 0.995])
        qy = ypts.geometry.y.quantile([0.005, 0.995])
        ypts = ypts[ypts.geometry.x.between(qx.iloc[0], qx.iloc[1]) &
                    ypts.geometry.y.between(qy.iloc[0], qy.iloc[1])]

    metric_crs = ypts.estimate_utm_crs()
    ypts = ypts.to_crs(metric_crs)

    ycol = yield_col or _detect_yield_column(ypts)
    ypts = ypts.copy()
    ypts[ycol] = gpd.pd.to_numeric(ypts[ycol], errors="coerce")
    ypts = ypts[ypts[ycol].notna()]

    if clean:
        v = ypts[ycol]
        ypts = ypts[v > 0]
        lo, hi = ypts[ycol].quantile([clip_pct[0] / 100, clip_pct[1] / 100])
        ypts = ypts[(ypts[ycol] >= lo) & (ypts[ycol] <= hi)]

    if boundary_path is not None:
        boundary = gpd.read_file(boundary_path)
        if boundary.crs is None:
            boundary.set_crs("EPSG:4326", inplace=True)
        boundary = boundary.to_crs(metric_crs)
    else:
        # No boundary polygon supplied — approximate the field outline with the
        # convex hull of the (cleaned) points so area and outline still render.
        hull = ypts.geometry.union_all().convex_hull
        boundary = gpd.GeoDataFrame(geometry=[hull], crs=metric_crs)

    attrs = {}
    if len(boundary):
        row = boundary.iloc[0]
        for key in ("GROWER", "FARM", "FIELDNAME", "ADSYEAR", "COACH"):
            if key in boundary.columns and row.get(key) not in (None, ""):
                attrs[key] = row[key]

    if name is None:
        stem = Path(boundary_path).stem if boundary_path is not None else Path(yield_path).stem
        name = attrs.get("FIELDNAME") or stem

    return FieldData(
        name=str(name),
        boundary=boundary,
        yield_pts=ypts,
        yield_col=ycol,
        metric_crs=str(metric_crs),
        attrs=attrs,
    )


def _quantile_bins(values: np.ndarray, n_classes: int) -> np.ndarray:
    """Quantile (equal-count) class breaks — the standard for yield maps.

    Deduplicated so heavily repeated yield values can't create empty classes.
    """
    qs = np.linspace(0, 1, n_classes + 1)
    edges = np.unique(np.quantile(values, qs))
    if edges.size < 3:  # pathological: near-constant field
        lo, hi = float(values.min()), float(values.max()) or 1.0
        edges = np.linspace(lo, hi + 1e-9, 4)
    return edges


def render(
    fd: FieldData,
    ax: Axes,
    *,
    n_classes: int = 7,
    cmap_name: str = "RdYlGn",
    point_size: float = 2.0,
    units: str = "",
) -> Axes:
    """Draw the yield map (points classed by yield + boundary outline) onto *ax*."""
    import matplotlib.pyplot as plt
    from matplotlib.colors import BoundaryNorm

    vals = fd.values
    edges = _quantile_bins(vals, n_classes)
    n_used = len(edges) - 1
    cmap = plt.get_cmap(cmap_name, n_used)
    norm = BoundaryNorm(edges, ncolors=n_used)

    xs = fd.yield_pts.geometry.x.to_numpy()
    ys = fd.yield_pts.geometry.y.to_numpy()
    ax.scatter(xs, ys, c=vals, cmap=cmap, norm=norm, s=point_size,
               linewidths=0, marker="o")

    fd.boundary.boundary.plot(ax=ax, color="black", linewidth=1.4, zorder=5)

    ax.set_aspect("equal")
    ax.set_axis_off()
    ax.margins(0.02)

    # Title from boundary metadata when available.
    a = fd.attrs
    line1 = "  ·  ".join(x for x in (
        a.get("GROWER"), a.get("FARM"), f"Field {a.get('FIELDNAME', fd.name)}"
    ) if x)
    year = a.get("ADSYEAR")
    line2 = f"Yield map ({fd.yield_col})" + (f" — {year}" if year else "")
    ax.set_title(f"{line1}\n{line2}", fontsize=12, fontweight="bold")

    _add_legend(ax, edges, cmap, vals, units)
    _add_stats_box(ax, fd, units)
    _add_scalebar(ax)
    return ax


def _add_legend(ax, edges, cmap, vals, units):
    from matplotlib.patches import Patch

    counts, _ = np.histogram(vals, bins=edges)
    total = counts.sum() or 1
    handles = []
    for i in range(len(edges) - 1):
        lo, hi = edges[i], edges[i + 1]
        pct = 100.0 * counts[i] / total
        label = f"{lo:.2f} – {hi:.2f}{(' ' + units) if units else ''}  ({pct:.0f}%)"
        handles.append(Patch(facecolor=cmap(i), edgecolor="none", label=label))
    ax.legend(
        handles=handles[::-1],           # high yield at top
        title=f"Yield ({units})" if units else "Yield class",
        loc="center left", bbox_to_anchor=(1.01, 0.5),
        frameon=True, fontsize=8, title_fontsize=9,
    )


def _add_stats_box(ax, fd: FieldData, units):
    v = fd.values
    u = f" {units}" if units else ""
    lines = [
        f"Points:  {len(v):,}",
        f"Area:    {fd.area_ha:.1f} ha  ({fd.area_ac:.1f} ac)",
        f"Mean:    {np.mean(v):.2f}{u}",
        f"Median:  {np.median(v):.2f}{u}",
        f"Min–Max: {np.min(v):.2f} – {np.max(v):.2f}{u}",
        f"Std dev: {np.std(v):.2f}{u}",
        f"CRS:     {fd.metric_crs}",
    ]
    ax.text(
        0.01, 0.01, "\n".join(lines), transform=ax.transAxes,
        fontsize=8, family="monospace", va="bottom", ha="left",
        bbox=dict(boxstyle="round,pad=0.4", facecolor="white", alpha=0.8,
                  edgecolor="0.6"),
        zorder=6,
    )


def _add_scalebar(ax):
    """A metric scale bar (~1/5 of map width, rounded) in the lower-right corner.

    Lower-right keeps it clear of the stats box in the lower-left.
    """
    x0, x1 = ax.get_xlim()
    y0, y1 = ax.get_ylim()
    span = x1 - x0
    raw = span / 5.0
    mag = 10 ** np.floor(np.log10(raw))
    nice = min([1, 2, 5, 10], key=lambda m: abs(m * mag - raw)) * mag
    bx1 = x1 - span * 0.04
    bx0 = bx1 - nice
    by = y0 + (y1 - y0) * 0.04
    ax.plot([bx0, bx1], [by, by], color="black", linewidth=3, zorder=7)
    label = f"{nice/1000:.0f} km" if nice >= 1000 else f"{nice:.0f} m"
    ax.text((bx0 + bx1) / 2, by + (y1 - y0) * 0.012, label, ha="center",
            va="bottom", fontsize=8, zorder=7,
            bbox=dict(boxstyle="square,pad=0.1", facecolor="white",
                      alpha=0.7, edgecolor="none"))


def make_figure(fd: FieldData, **kwargs) -> Figure:
    """Build a standalone Figure (used by the PNG exporter)."""
    fig = Figure(figsize=(11, 8.5), dpi=120)
    ax = fig.add_subplot(111)
    render(fd, ax, **kwargs)
    fig.tight_layout()
    return fig
