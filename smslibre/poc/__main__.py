"""CLI for the yield-map PoC.

Examples
--------
Headless PNG (works anywhere, no display needed)::

    python -m smslibre.poc --boundary BND.shp --yield YLD.shp --out map.png

Interactive Qt window::

    python -m smslibre.poc --boundary BND.shp --yield YLD.shp --gui

With no --boundary/--yield, it falls back to the bundled sample field.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .yieldmap import load_field, make_figure

# Bundled sample (Smart Farm field "15-16", 2023 harvest) so the PoC runs with
# zero arguments straight after a git clone.
_SAMPLES = Path(__file__).resolve().parents[2] / "samples" / "poc"
_DEFAULT_BOUNDARY = _SAMPLES / "15-16_Smart_Farm_1581629_boundary.shp"
_DEFAULT_YIELD = _SAMPLES / "Field1516_Harvest2023_YieldCleaned.shp"


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="smslibre.poc", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--boundary", type=Path, default=_DEFAULT_BOUNDARY,
                   help="field boundary polygon shapefile")
    p.add_argument("--yield", dest="yield_path", type=Path, default=_DEFAULT_YIELD,
                   help="yield point shapefile")
    p.add_argument("--yield-col", default=None,
                   help="yield attribute name (auto-detected if omitted)")
    p.add_argument("--units", default="",
                   help='label for the yield unit, e.g. "bu/ac" or "t/ha"')
    p.add_argument("--classes", type=int, default=7, help="number of yield classes")
    p.add_argument("--out", type=Path, default=None, help="write a PNG here")
    p.add_argument("--gui", action="store_true", help="open the Qt viewer")
    args = p.parse_args(argv)

    for label, path in (("boundary", args.boundary), ("yield", args.yield_path)):
        if not path.exists():
            p.error(f"{label} shapefile not found: {path}")

    print(f"Loading boundary: {args.boundary.name}")
    print(f"Loading yield   : {args.yield_path.name}")
    fd = load_field(args.boundary, args.yield_path, yield_col=args.yield_col)
    print(f"  field '{fd.name}'  ·  {len(fd.yield_pts):,} points  ·  "
          f"yield col '{fd.yield_col}'  ·  {fd.area_ha:.1f} ha  ·  {fd.metric_crs}")

    if not args.gui and args.out is None:
        args.out = Path("yieldmap.png")   # sensible default so something is produced

    if args.out is not None:
        fig = make_figure(fd, n_classes=args.classes, units=args.units)
        fig.savefig(args.out, dpi=150, bbox_inches="tight")
        print(f"Wrote {args.out.resolve()}")

    if args.gui:
        from .viewer import launch
        return launch(fd, n_classes=args.classes, units=args.units)

    return 0


if __name__ == "__main__":
    sys.exit(main())
