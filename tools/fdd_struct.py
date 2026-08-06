"""Structural analysis of a John Deere .fdd log.

Two passes:

1. **Periodicity** — byte-level autocorrelation over the data region to reveal
   the record stride without assuming any framing.
2. **TLV walk** — parse the uint16 length-prefixed header stream to enumerate
   the declared columns and find where the schema ends and records begin.

    python tools/fdd_struct.py <file.fdd> [--start 0xBD] [--max-stride 512]
"""

from __future__ import annotations

import argparse
import re
import struct
from collections import Counter
from pathlib import Path


def autocorr(data: bytes, lo: int, hi: int, sample: int = 400_000) -> list[tuple[int, float]]:
    """Fraction of positions where d[i] == d[i+stride], per candidate stride."""
    n = min(len(data), sample)
    view = data[:n]
    scores = []
    for s in range(lo, hi + 1):
        if s >= n:
            break
        same = 0
        # step to keep this cheap on large files
        step = max(1, (n - s) // 20000)
        cnt = 0
        for i in range(0, n - s, step):
            if view[i] == view[i + s]:
                same += 1
            cnt += 1
        if cnt:
            scores.append((s, same / cnt))
    return scores


def walk_tlv(data: bytes, start: int = 0, limit: int = 400):
    """Heuristic walk: a uint16 length followed by that many printable bytes is
    treated as a string; anything else is reported as a scalar pair."""
    pos = start
    out = []
    while pos + 2 <= len(data) and len(out) < limit:
        ln = struct.unpack_from("<H", data, pos)[0]
        body = data[pos + 2: pos + 2 + ln]
        is_str = (
            4 <= ln <= 128
            and len(body) == ln
            and all(32 <= b < 127 for b in body)
        )
        if is_str:
            out.append((pos, "str", ln, body.decode("ascii")))
            pos += 2 + ln
        else:
            out.append((pos, "u16", ln, None))
            pos += 2
    return out, pos


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--max-stride", type=int, default=256)
    ap.add_argument("--min-stride", type=int, default=4)
    args = ap.parse_args()

    data = Path(args.path).read_bytes()
    print(f"{Path(args.path).name}: {len(data):,} bytes\n")

    # --- where does the readable schema region end? ---
    runs = [(m.start(), m.end()) for m in re.finditer(rb"[\x20-\x7E]{6,}", data)]
    if runs:
        # last dense cluster of identifiers near the front = end of schema block
        gaps = [(b[0] - a[1], a[1]) for a, b in zip(runs, runs[1:])]
        big = max(gaps, key=lambda g: g[0]) if gaps else (0, runs[0][1])
        print(f"identifier runs: {len(runs)}; largest gap {big[0]:,} bytes after 0x{big[1]:X}")
        print(f"first identifier at 0x{runs[0][0]:X}, last at 0x{runs[-1][0]:X}")

    print("\n=== TLV walk from 0 ===")
    items, end = walk_tlv(data, 0, limit=60)
    for pos, kind, ln, s in items[:40]:
        if kind == "str":
            print(f"  0x{pos:06X}  str[{ln:3}]  {s}")
        else:
            print(f"  0x{pos:06X}  u16        {ln}")
    print(f"  … walk reached 0x{end:X}")

    # --- periodicity over the region after the schema ---
    body_start = runs[-1][1] if runs else 0
    tail = data[body_start:]
    print(f"\n=== autocorrelation over data region (from 0x{body_start:X}, "
          f"{len(tail):,} bytes) ===")
    scores = autocorr(tail, args.min_stride, args.max_stride)
    if scores:
        base = sum(s for _, s in scores) / len(scores)
        top = sorted(scores, key=lambda x: -x[1])[:12]
        print(f"  mean match rate {base:.4f}; strongest strides:")
        for s, v in top:
            mark = " <<<" if v > base * 1.5 else ""
            print(f"    stride {s:4}  match {v:.4f}{mark}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
