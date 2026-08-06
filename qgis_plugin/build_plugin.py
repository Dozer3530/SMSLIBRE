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
SIDECAR_PROJ = ROOT / "sidecar" / "src" / "SmsImport" / "SmsImport.csproj"


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


def stage_licensed(secrets: Path, vendor: Path) -> None:
    """Stage licensed vendor material for an INTERNAL build.

    Places John Deere's plugin release and our credentials where the sidecar
    auto-discovers them (an AdaptPlugins folder and johndeere.* files beside the
    executable), so an internal install needs no configuration at all.

    Never call this for a build that will be published — see build_zip().
    """
    bin_dir = PLUGIN_DIR / "bin"
    plugins_src = vendor / "jd-plugins" / "plugins"
    if plugins_src.is_dir():
        dst = bin_dir / "AdaptPlugins"
        shutil.rmtree(dst, ignore_errors=True)
        shutil.copytree(plugins_src, dst)
        n = sum(1 for _ in dst.rglob("*.dll"))
        print(f"  staged vendor plugins: {n} dlls")
    else:
        print(f"  WARNING: no vendor plugins at {plugins_src}")

    for name in ("johndeere.appid", "johndeere.adaptplugins.lic"):
        src = secrets / name
        if src.exists():
            shutil.copy2(src, bin_dir / name)
            print(f"  staged {name}")
        else:
            print(f"  WARNING: missing {src}")


def build_zip(internal: bool) -> Path:
    dist = ROOT / "build"
    dist.mkdir(exist_ok=True)
    name = "smslibre_import_INTERNAL.zip" if internal else "smslibre_import.zip"
    zip_path = dist / name
    skip = {"__pycache__", ".pytest_cache"}

    licensed_markers = ("adaptplugins", "johndeere", "adaptplugins.lic")
    included_licensed = []

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for path in PLUGIN_DIR.rglob("*"):
            if path.is_dir() or any(s in path.parts for s in skip):
                continue
            rel = path.relative_to(PLUGIN_DIR)
            low = str(rel).lower()
            if any(m in low for m in licensed_markers):
                if not internal:
                    # Safety net: a public zip must never carry vendor material.
                    continue
                included_licensed.append(str(rel))
            z.write(path, Path("smslibre_import") / rel)

    print(f"\nWrote {zip_path}")
    if internal:
        print(f"  contains {len(included_licensed)} licensed file(s) — "
              "INTERNAL DISTRIBUTION ONLY, do not publish this zip")
    else:
        print("  public build: licensed vendor material excluded")
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
    ap.add_argument("--internal", action="store_true",
                    help="INTERNAL build: bundle the licensed vendor plugins and "
                         "credentials so the plugin works with no configuration. "
                         "Never publish the resulting zip.")
    ap.add_argument("--secrets", type=Path, default=ROOT / "secrets",
                    help="folder holding johndeere.appid / johndeere.adaptplugins.lic")
    ap.add_argument("--vendor", type=Path, default=ROOT / "vendor",
                    help="folder holding jd-plugins/plugins")
    args = ap.parse_args()

    if not args.skip_sidecar:
        stage_sidecar(publish_sidecar(args.runtime))
    if args.internal:
        stage_licensed(args.secrets, args.vendor)
    build_zip(args.internal)
    if args.install:
        install_locally()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
