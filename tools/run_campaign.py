"""Run the full test campaign across every shared drive, unattended.

A single sweep answers "does this import?". A campaign answers the harder
questions: does it still import when everything is rebuilt from scratch, does it
import the SAME way twice, and does the answer hold across every drive we have
rather than the one that happened to get tested.

Phases run in order. Each writes a marker when it finishes, so a campaign that
is interrupted — reboot, network drop, someone closing the laptop — resumes
where it stopped instead of starting over.

    python tools/run_campaign.py                 # run everything
    python tools/run_campaign.py --list          # show the plan and stop
    python tools/run_campaign.py --from 4        # resume at a phase
    python tools/run_campaign.py --budget-hours 48

Progress goes to analysis/campaign/campaign.log and to the console.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
CAMPAIGN = ROOT / "analysis" / "campaign"
PY = sys.executable

DRIVES = [
    # (key, human name, root path)
    ("vault", "Olds College Smart Farm Vault",
     "G:/Shared drives/Olds College Smart Farm Vault"),
    ("staar", "210600 STAAR",
     "G:/Shared drives/210600 STAAR"),
    ("sfdata", "M: sfdata (olsmartfsrv)",
     "M:/"),
]


def log(msg: str) -> None:
    stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    line = f"[{stamp}] {msg}"
    print(line, flush=True)
    CAMPAIGN.mkdir(parents=True, exist_ok=True)
    with open(CAMPAIGN / "campaign.log", "a", encoding="utf-8") as f:
        f.write(line + "\n")


def free_gb() -> float:
    try:
        return shutil.disk_usage(ROOT.anchor).free / 1e9
    except OSError:
        return float("nan")


def build_env() -> dict:
    """Environment for a phase.

    The project targets .NET 10, whose SDK lives in the user profile, while
    Windows has an older SDK on PATH under Program Files. Inheriting the plain
    environment finds the wrong one and every dotnet phase dies instantly with
    NETSDK1045 — which reads like a test failure and is not.
    """
    env = dict(os.environ)
    local = pathlib.Path.home() / ".dotnet"
    if local.is_dir():
        env["DOTNET_ROOT"] = str(local)
        env["PATH"] = str(local) + os.pathsep + env.get("PATH", "")
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    return env


def run(cmd: list[str], phase: str, timeout: int) -> tuple[bool, str]:
    """Run one phase. A non-zero exit is reported, never fatal to the campaign."""
    log(f"  $ {' '.join(str(c) for c in cmd)}")
    out_file = CAMPAIGN / f"{phase}.log"
    try:
        with open(out_file, "w", encoding="utf-8") as f:
            proc = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT,
                                  timeout=timeout, cwd=ROOT, env=build_env())
        ok = proc.returncode == 0
        return ok, f"exit {proc.returncode}"
    except subprocess.TimeoutExpired:
        return False, f"timed out after {timeout}s"
    except Exception as exc:                                   # noqa: BLE001
        return False, f"{type(exc).__name__}: {exc}"


def phases(args) -> list[dict]:
    """The plan. Sweeps first, then the checks that depend on their output."""
    plan: list[dict] = []

    plan.append({
        "id": 1, "name": "unit-tests",
        "what": "Full test suite — the baseline everything else assumes",
        "cmd": ["dotnet", "test", "sidecar/tests/SmsLibre.Import.Tests",
                "-c", "Release", "--nologo"],
        "timeout": 3600,
    })

    for i, (key, name, root) in enumerate(DRIVES, start=2):
        plan.append({
            "id": i, "name": f"sweep-{key}",
            "what": f"Full sweep: {name}",
            "cmd": [PY, "tools/vault_test.py", "--root", root,
                    "--out", f"analysis/campaign/{key}",
                    # Three workers, not five: a wide card writes several GB and the reserve
                    # has to cover every worker holding one at once.
                    "--depth", "14", "--cap", "200000", "--workers", "3",
                    "--min-depth", "2", "--timeout", "7200",
                    "--scan-timeout", "43200"],
            "timeout": 20 * 3600,
        })

    n = len(DRIVES) + 2
    for key, name, _ in DRIVES:
        plan.append({
            "id": n, "name": f"determinism-{key}",
            "what": f"Re-import and compare: {name}",
            "cmd": [PY, "tools/verify_determinism.py",
                    "--results", f"analysis/campaign/{key}/results.json",
                    "--out", f"analysis/campaign/{key}",
                    "--sample", str(args.sample), "--repeat", "1",
                    "--max-seconds", str(args.determinism_seconds)],
            "timeout": args.determinism_seconds + 3600,
            "optional": True,       # needs the sweep to have produced results
        })
        n += 1

    plan.append({
        "id": n, "name": "summary",
        "what": "Combine every result into one report",
        "cmd": [PY, "tools/campaign_summary.py"],
        "timeout": 1800,
    })
    return plan


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", action="store_true", help="print the plan and exit")
    ap.add_argument("--from", dest="start", type=int, default=1,
                    help="first phase to run")
    ap.add_argument("--budget-hours", type=float, default=48.0)
    ap.add_argument("--sample", type=int, default=60,
                    help="cards per drive in the determinism check (0 = all)")
    ap.add_argument("--determinism-seconds", type=int, default=5 * 3600)
    ap.add_argument("--force", action="store_true",
                    help="re-run phases that already have a completion marker")
    args = ap.parse_args()

    plan = phases(args)
    if args.list:
        print(f"{'#':>3}  {'phase':22} what")
        for p in plan:
            print(f"{p['id']:>3}  {p['name']:22} {p['what']}")
        return 0

    CAMPAIGN.mkdir(parents=True, exist_ok=True)
    started = time.time()
    deadline = started + args.budget_hours * 3600

    log("=" * 70)
    log(f"CAMPAIGN START — budget {args.budget_hours:.0f} h, "
        f"{free_gb():.0f} GB free on {ROOT.anchor}")
    log("=" * 70)

    outcomes = []
    for p in plan:
        if p["id"] < args.start:
            continue
        marker = CAMPAIGN / f".done-{p['name']}"
        if marker.exists() and not args.force:
            log(f"phase {p['id']} {p['name']}: already done, skipping")
            continue

        remaining = deadline - time.time()
        if remaining <= 0:
            log(f"BUDGET EXHAUSTED before phase {p['id']} ({p['name']})")
            break

        log("")
        log(f"PHASE {p['id']}: {p['what']}")
        log(f"  {remaining / 3600:.1f} h of budget left, {free_gb():.0f} GB free")

        t0 = time.time()
        ok, detail = run(p["cmd"], p["name"], min(p["timeout"], int(remaining)))
        mins = (time.time() - t0) / 60

        if ok:
            marker.write_text(datetime.datetime.now().isoformat())
            log(f"  DONE in {mins:.0f} min ({detail})")
        else:
            # An optional phase that could not run is not a campaign failure —
            # a determinism check has nothing to do if its sweep found nothing.
            level = "SKIPPED" if p.get("optional") else "FAILED"
            log(f"  {level} after {mins:.0f} min ({detail}) — see "
                f"analysis/campaign/{p['name']}.log")
        outcomes.append({"phase": p["id"], "name": p["name"], "ok": ok,
                         "minutes": round(mins, 1), "detail": detail})

    total = (time.time() - started) / 3600
    log("")
    log("=" * 70)
    log(f"CAMPAIGN END — {total:.1f} h elapsed, {free_gb():.0f} GB free")
    for o in outcomes:
        log(f"  phase {o['phase']:>2} {o['name']:22} "
            f"{'ok' if o['ok'] else 'FAILED':7} {o['minutes']:>6.0f} min")
    log("=" * 70)

    (CAMPAIGN / "outcomes.json").write_text(json.dumps(outcomes, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
