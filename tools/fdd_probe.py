"""Locate the GPS channel inside a John Deere .fdd log.

We know roughly where the field is, so rather than guessing at the record
framing we scan for the field's coordinates in every encoding John Deere
plausibly uses, and let the hit pattern reveal the record stride.

    python tools/fdd_probe.py <file.fdd> --lat 51.77 --lon -114.09
"""

from __future__ import annotations

import argparse
import struct
from collections import Counter
from pathlib import Path

# Encodings to try: (name, struct code, degrees -> raw)
SEMI = 2 ** 31 / 180.0            # "semicircles" — common on JD/Garmin hardware
ENCODINGS = [
    ("float64", "<d", lambda d: d),
    ("float32", "<f", lambda d: d),
    ("int32_1e7", "<i", lambda d: d * 1e7),
    ("int32_1e6", "<i", lambda d: d * 1e6),
    ("int32_semicircle", "<i", lambda d: d * SEMI),
]


def scan(data: bytes, lat: float, lon: float, tol_deg: float = 0.05):
    """Return {encoding: [(offset, kind, value_in_degrees)]}."""
    hits: dict[str, list] = {name: [] for name, _, _ in ENCODINGS}
    for name, code, to_raw in ENCODINGS:
        size = struct.calcsize(code)
        lat_raw, lon_raw = to_raw(lat), to_raw(lon)
        # tolerance expressed in the same raw units
        tol = abs(to_raw(lat + tol_deg) - lat_raw) or 1e-6
        for off in range(0, len(data) - size, 1):
            try:
                v = struct.unpack_from(code, data, off)[0]
            except struct.error:
                continue
            if abs(v - lat_raw) <= tol:
                hits[name].append((off, "lat", v / (to_raw(1.0) or 1.0)))
            elif abs(v - lon_raw) <= tol:
                hits[name].append((off, "lon", v / (to_raw(1.0) or 1.0)))
    return hits


def stride_report(offsets: list[int], label: str, top: int = 6):
    """Most common gaps between consecutive hits ≈ the record size."""
    if len(offsets) < 3:
        return
    diffs = Counter(b - a for a, b in zip(offsets, offsets[1:]) if 0 < b - a < 4096)
    common = diffs.most_common(top)
    print(f"    {label}: {len(offsets)} hits; common strides {common}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--lat", type=float, required=True)
    ap.add_argument("--lon", type=float, required=True)
    ap.add_argument("--tol", type=float, default=0.05)
    args = ap.parse_args()

    data = Path(args.path).read_bytes()
    print(f"{Path(args.path).name}: {len(data):,} bytes")
    print(f"searching for lat~{args.lat} lon~{args.lon} (+/-{args.tol} deg)\n")

    hits = scan(data, args.lat, args.lon, args.tol)
    for name, hs in hits.items():
        if not hs:
            print(f"  {name:18} —")
            continue
        lats = [o for o, k, _ in hs if k == "lat"]
        lons = [o for o, k, _ in hs if k == "lon"]
        print(f"  {name:18} {len(hs):>7} hits  (lat {len(lats)}, lon {len(lons)})")
        stride_report(sorted(lats), "lat")
        stride_report(sorted(lons), "lon")
        # If lat/lon are adjacent in the record, the gap is the pair spacing.
        if lats and lons:
            pair = Counter()
            ls = set(lons)
            for o in lats:
                for delta in range(-16, 17):
                    if o + delta in ls:
                        pair[delta] += 1
            if pair:
                print(f"    lat→lon offset deltas: {pair.most_common(4)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
