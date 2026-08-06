"""Wrapper around the `smsimport` .NET sidecar.

The vendor import plugins are .NET assemblies, so rather than embedding a CLR in
QGIS's Python (fragile, and a crash would take QGIS with it) we run a small
console tool as a subprocess and parse its JSON.
"""

from __future__ import annotations

import json
import os
import platform
import subprocess
from dataclasses import dataclass
from pathlib import Path


class SidecarError(Exception):
    """Raised when the sidecar is missing, fails, or returns an error object."""


@dataclass
class PluginInfo:
    name: str
    version: str
    owner: str

    @property
    def label(self) -> str:
        return f"{self.name} ({self.owner})" if self.owner else self.name


def default_sidecar_path() -> str:
    """Best guess at the bundled sidecar executable."""
    exe = "SmsImport.exe" if platform.system() == "Windows" else "SmsImport"
    bundled = Path(__file__).parent / "bin" / exe
    return str(bundled)


def default_sms_dir() -> str:
    for p in (
        r"C:\Program Files\Ag Leader Technology\SMS",
        r"C:\Program Files (x86)\Ag Leader Technology\SMS",
    ):
        if os.path.isdir(p):
            return p
    return ""


class Sidecar:
    def __init__(self, exe_path: str, sms_dir: str = ""):
        self.exe_path = exe_path
        self.sms_dir = sms_dir

    # -- internals ---------------------------------------------------------

    def _run(self, args: list[str], timeout: int = 3600) -> dict:
        if not self.exe_path or not os.path.isfile(self.exe_path):
            raise SidecarError(
                f"Sidecar not found: {self.exe_path or '(unset)'}\n"
                "Set its location in the plugin's Settings."
            )
        cmd = [self.exe_path] + args
        if self.sms_dir:
            cmd += ["--sms", self.sms_dir]

        # Keep the console window hidden on Windows.
        creation = 0
        if platform.system() == "Windows":
            creation = getattr(subprocess, "CREATE_NO_WINDOW", 0)

        try:
            proc = subprocess.run(
                cmd, capture_output=True, text=True,
                timeout=timeout, creationflags=creation,
            )
        except subprocess.TimeoutExpired as exc:
            raise SidecarError(f"Import timed out after {timeout}s") from exc
        except OSError as exc:
            raise SidecarError(f"Could not start sidecar: {exc}") from exc

        out = (proc.stdout or "").strip()
        if not out:
            raise SidecarError(
                f"Sidecar produced no output (exit {proc.returncode}).\n"
                f"{(proc.stderr or '').strip()[:800]}"
            )
        try:
            data = json.loads(out)
        except json.JSONDecodeError as exc:
            raise SidecarError(f"Unreadable sidecar output:\n{out[:800]}") from exc

        if not data.get("ok", False):
            raise SidecarError(data.get("error", "Unknown sidecar error"))
        return data

    # -- API ---------------------------------------------------------------

    def plugins(self) -> list[PluginInfo]:
        data = self._run(["plugins"], timeout=120)
        return [
            PluginInfo(p.get("Name", ""), p.get("Version", ""), p.get("Owner", ""))
            for p in data.get("plugins", [])
        ]

    def detect(self, card_path: str) -> list[PluginInfo]:
        data = self._run(["detect", card_path], timeout=300)
        return [
            PluginInfo(p.get("Name", ""), p.get("Version", ""), p.get("Owner", ""))
            for p in data.get("plugins", [])
        ]

    def import_card(self, card_path: str, out_gpkg: str,
                    plugin_name: str | None = None, timeout: int = 3600) -> dict:
        """Convert a card to GeoPackage. Returns the sidecar's JSON summary."""
        args = ["import", card_path, out_gpkg]
        if plugin_name:
            args += ["--plugin", plugin_name]
        return self._run(args, timeout=timeout)
