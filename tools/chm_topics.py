"""List every internal topic file in a CHM help file.

Parses the CHM directory (ITSF → ITSP → PMGL chunks), which is *not* compressed,
to enumerate all internal objects. The .htm/.html entries are the help topics —
one per SMS feature/dialog — giving an authoritative breadth inventory even
though the topic *bodies* are LZX-compressed.
"""

import struct
import sys
from pathlib import Path


def read_encint(buf, pos):
    val = 0
    while True:
        b = buf[pos]; pos += 1
        val = (val << 7) | (b & 0x7F)
        if not (b & 0x80):
            break
    return val, pos


def chm_entries(path):
    data = Path(path).read_bytes()
    if data[:4] != b"ITSF":
        raise ValueError("not a CHM (no ITSF header)")

    # ITSF: directory section offset is the second (offset,len) pair at 0x48.
    dir_off = struct.unpack_from("<q", data, 0x48)[0]
    if data[dir_off:dir_off + 4] != b"ITSP":
        raise ValueError("no ITSP at directory offset")

    chunk_size = struct.unpack_from("<i", data, dir_off + 0x10)[0]
    n_chunks = struct.unpack_from("<i", data, dir_off + 0x2C)[0]
    itsp_len = struct.unpack_from("<i", data, dir_off + 0x08)[0]
    first = dir_off + itsp_len

    entries = []
    for c in range(n_chunks):
        base = first + c * chunk_size
        sig = data[base:base + 4]
        if sig != b"PMGL":
            continue  # PMGI index chunk — skip
        free = struct.unpack_from("<i", data, base + 0x04)[0]
        end = base + chunk_size - free
        pos = base + 0x14
        while pos < end:
            name_len, pos = read_encint(data, pos)
            name = data[pos:pos + name_len].decode("utf-8", "replace"); pos += name_len
            _section, pos = read_encint(data, pos)
            _offset, pos = read_encint(data, pos)
            _length, pos = read_encint(data, pos)
            entries.append(name)
    return entries


if __name__ == "__main__":
    chm = sys.argv[1] if len(sys.argv) > 1 else \
        r"C:\Program Files\Ag Leader Technology\SMS\ALMapping.chm"
    names = chm_entries(chm)
    topics = sorted(n for n in names if n.lower().endswith((".htm", ".html")))
    print(f"total internal objects: {len(names)}")
    print(f"topic pages (.htm):     {len(topics)}")
    print("\nsample topic filenames:")
    for t in topics[:40]:
        print("  ", t)
    out = Path(__file__).resolve().parents[1] / "analysis" / "chm_topics.txt"
    out.write_text("\n".join(topics), encoding="utf-8")
    print(f"\nwrote {out}")
