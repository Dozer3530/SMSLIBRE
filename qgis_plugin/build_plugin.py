"""Build the QGIS plugin zip, bundling the .NET sidecar.

    python qgis_plugin/build_plugin.py [--runtime win-x64|linux-x64] [--install]

Publishes the sidecar as a self-contained single file so the plugin works on a
machine without the .NET runtime, copies it into the plugin folder, and zips the
result for QGIS's "Install from ZIP".

Note: only the open-source AgGateway assemblies are bundled by the sidecar
build. The proprietary vendor plugins (John Deere, Precision Planting, Trimble,
Climate, CNH) are read from the user's own SMS installation at run time and are
never redistributed.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PLUGIN_DIR = Path(__file__).resolve().parent / "smslibre_import"
SIDECAR_PROJ = ROOT / "app" / "src" / "SmsImport" / "SmsImport.csproj"


def publish_sidecar(runtime: str) -> Path:
    out = ROOT / "build" / f"sidecar-{runtime}"
    if out.exists():
        shutil.rmtree(out)
    # Deliberately NOT PublishSingleFile: the ADAPT assemblies must exist as real
    # files beside the executable. Vendor plugins loaded from the user's SMS
    # install resolve their own copy of AgGateway.ADAPT.*, and if ours were
    # embedded in a single-file bundle the two would be distinct assembly
    # identities — the IPlugin cast then fails and most plugins silently vanish
    # (observed: 10 plugins drop to 4).
    cmd = [
        "dotnet", "publish", str(SIDECAR_PROJ),
        "-c", "Release", "-r", runtime, "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-o", str(out),
    ]
    print("$", " ".join(cmd))
    subprocess.run(cmd, check=True)
    return out


def stage_sidecar(published: Path) -> None:
    """Copy the whole published folder — the runtime and the ADAPT assemblies
    must all sit beside the executable (see the note in publish_sidecar)."""
    bin_dir = PLUGIN_DIR / "bin"
    shutil.rmtree(bin_dir, ignore_errors=True)
    shutil.copytree(published, bin_dir)
    n = sum(1 for _ in bin_dir.rglob("*") if _.is_file())
    size = sum(f.stat().st_size for f in bin_dir.rglob("*") if f.is_file())
    print(f"  staged {n} files ({size / 1e6:.0f} MB) into {bin_dir}")


def make_zip() -> Path:
    dist = ROOT / "build"
    dist.mkdir(exist_ok=True)
    zip_path = dist / "smslibre_import.zip"
    skip = {"__pycache__", ".pytest_cache"}
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for path in PLUGIN_DIR.rglob("*"):
            if path.is_dir() or any(s in path.parts for s in skip):
                continue
            z.write(path, Path("smslibre_import") / path.relative_to(PLUGIN_DIR))
    print(f"\nWrote {zip_path}")
    return zip_path


def install_locally() -> None:
    if sys.platform.startswith("win"):
        base = Path(os.environ["APPDATA"]) / "QGIS/QGIS3/profiles/default/python/plugins"
    else:
        base = Path.home() / ".local/share/QGIS/QGIS3/profiles/default/python/plugins"
    target = base / "smslibre_import"
    if not base.exists():
        print(f"QGIS plugin folder not found: {base}")
        return
    shutil.rmtree(target, ignore_errors=True)
    shutil.copytree(PLUGIN_DIR, target,
                    ignore=shutil.ignore_patterns("__pycache__"))
    print(f"Installed to {target}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--runtime", default="win-x64",
                    help="dotnet RID: win-x64, linux-x64, osx-x64 (default win-x64)")
    ap.add_argument("--skip-sidecar", action="store_true",
                    help="package the Python only (sidecar already staged)")
    ap.add_argument("--install", action="store_true",
                    help="also copy into the local QGIS plugins folder")
    args = ap.parse_args()

    if not args.skip_sidecar:
        stage_sidecar(publish_sidecar(args.runtime))
    make_zip()
    if args.install:
        install_locally()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
