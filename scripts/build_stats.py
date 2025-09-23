# -*- coding: utf-8 -*-
#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

# ---- Config ----
PIPE_NAME = r"\\.\pipe\TraycerHud"
PIPE_TIMEOUT_SECONDS = 5.0
BUILD_WELL_ID = "build"
BUILD_WELL_WIDTH = 240
DEPLOYMENTS_ACTION = 'https://github.com/SimX-Inc/unity-client/deployments'
DEFAULT_FG = "#80F8F8F2"
DEFAULT_BG = "#8044475A"

PALETTE = {
    "success":   {"label": "✔️", "background": "#8050FA7B"},
    "failure":   {"label": "❌", "background": "#80FF5555"},
    "cancelled": {"label": "✖️", "background": "#80F1FA8C"},
    "timed_out": {"label": "⏲️", "background": "#80FFB86C"},
}

# Windows-only flag; harmless elsewhere
CREATE_NO_WINDOW = 0x08000000

# ---- Minimal helpers ----
def run_hidden(cmd: list[str], *, timeout: float = 15.0) -> subprocess.CompletedProcess:
    """Run a console program without creating a console window."""
    return subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        check=False,
        timeout=timeout,
        creationflags=CREATE_NO_WINDOW,
    )

def gh_json(args: list[str], *, timeout: float = 15.0) -> Optional[Any]:
    proc = run_hidden(["gh", *args], timeout=timeout)
    if proc.returncode != 0:
        return None
    data = proc.stdout.strip()
    if not data:
        return None
    try:
        return json.loads(data)
    except json.JSONDecodeError:
        return None

def git_out(repo_dir: Optional[Path], git_args: list[str], *, timeout: float = 15.0) -> Optional[str]:
    base = ["git"] + (["-C", str(repo_dir)] if repo_dir else [])
    proc = run_hidden(base + git_args, timeout=timeout)
    if proc.returncode != 0:
        return None
    return proc.stdout.strip()

def get_repo_from_dir(path: Path) -> Optional[str]:
    url = git_out(path, ["remote", "get-url", "origin"], timeout=10)
    if not url:
        return None
    m = re.search(r"github\.com[:/](?P<nwo>[^/]+/[^/]+?)(?:\.git)?$", url)
    return m.group("nwo") if m else None

def fmt_local(iso_ts: Optional[str]) -> str:
    if not iso_ts:
        return "-"
    s = iso_ts.strip()
    if not s:
        return "-"
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    try:
        dt = datetime.fromisoformat(s)
    except ValueError:
        return "-"
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    local = dt.astimezone()
    return f"{local.month}/{local.day} {local.strftime('%I:%M %p').lstrip('0')}"

def resolve_version(repo_dir: Optional[Path], sha: Optional[str]) -> Optional[str]:
    if not sha:
        return None
    if repo_dir:
        # Quietly fetch tags
        run_hidden(["git", "-C", str(repo_dir), "fetch", "--tags", "--quiet"], timeout=20)
        exact = git_out(repo_dir, ["tag", "--points-at", sha], timeout=10)
        if exact:
            # first non-empty line
            for line in exact.splitlines():
                if line.strip():
                    return line.strip()
        described = git_out(repo_dir, ["describe", "--tags", "--abbrev=0", sha], timeout=10)
        if described:
            return described.strip()
    return sha[:7] if sha else None

def send_traycer(payload: dict[str, Any]) -> bool:
    data = json.dumps(payload, ensure_ascii=False) + "\n"
    deadline = time.time() + PIPE_TIMEOUT_SECONDS
    last_err: Optional[Exception] = None
    while time.time() < deadline:
        try:
            with open(PIPE_NAME, "w", encoding="utf-8", newline="\n") as pipe:
                pipe.write(data)
            return True
        except OSError as e:
            last_err = e
            time.sleep(0.1)
    # Optional: log to file if you want; stdout/stderr may be invisible under pythonw
    return False

def ensure_build_well(width: int = BUILD_WELL_WIDTH) -> None:
    send_traycer({"op": "add", "well": BUILD_WELL_ID, "width": width})

def set_build_text(text: str, fg: str, bg: str, action: Optional[str] = None) -> None:
    payload = {"op": "set", "well": BUILD_WELL_ID, "text": text, "fg": fg, "bg": bg}
    if action:
        payload["action"] = action
    send_traycer(payload)

# ---- Main ----
def main(argv: Optional[list[str]] = None) -> int:
    p = argparse.ArgumentParser(description="Update Traycer build well from latest GitHub Actions runs.")
    p.add_argument("--repo-dir", help="Path to local clone (for tag/describe).")
    p.add_argument("--repo", help="GitHub owner/repo, e.g. org/project. Auto-detected from repo-dir if omitted.")
    p.add_argument("--branch", default="dev", help="Which branch counts as 'dev' (default: dev).")
    args = p.parse_args(argv)

    repo_dir: Optional[Path] = Path(args.repo_dir).resolve() if args.repo_dir else None
    repo = args.repo or (get_repo_from_dir(repo_dir) if repo_dir else None)
    repo_args = ["-R", repo] if repo else []

    # Last run (any status)
    last_runs = gh_json(["run", "list", *repo_args, "-L", "1", "--json", "status,conclusion,updatedAt"]) or []
    last = last_runs[0] if isinstance(last_runs, list) and last_runs else None

    # Latest successful run (prefer given branch)
    successes = gh_json(["run", "list", *repo_args, "--status", "success", "-L", "30",
                         "--json", "headSha,updatedAt,headBranch"]) or []
    dev_lower = args.branch.lower()
    succ = None
    if isinstance(successes, list):
        for run in successes:
            if (run.get("headBranch") or "").lower() == dev_lower:
                succ = run
                break
        if succ is None and successes:
            succ = successes[0]

    # Left side: version + time for successful dev (or first success)
    left_label = "<no dev success>"
    left_time = "-"
    if succ:
        version = resolve_version(repo_dir, succ.get("headSha"))
        if version:
            left_label = version
        left_time = fmt_local(succ.get("updatedAt"))
    left = f"{left_label}  {left_time}"

    # Right side: last run status + time
    fg = DEFAULT_FG
    bg = DEFAULT_BG
    state_label = "?"
    if last and (last.get("status") or "").lower() == "completed":
        key = (last.get("conclusion") or "").lower()
        pal = PALETTE.get(key)
        if pal:
            state_label = pal["label"]
            bg = pal["background"]
    right = "-" if not last else f"{state_label} {fmt_local(last.get('updatedAt'))}"

    summary = f"{left}  |  {right}"

    # Send to Traycer
    ensure_build_well()
    set_build_text(summary, fg, bg, DEPLOYMENTS_ACTION)
    return 0

if __name__ == "__main__":
    sys.exit(main())


