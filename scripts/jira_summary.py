#!/usr/bin/env python3
"""Send a Jira status snapshot to the Traycer HUD using ACLI."""

import json
import re
import shutil
import subprocess
from subprocess import STARTF_USESHOWWINDOW, STARTUPINFO
import sys
import time
from typing import Optional
from urllib.parse import quote

PIPE_NAME = r"\\.\\pipe\\TraycerHud"
WELL_ID = "jira"
WELL_WIDTH = 80
CONNECT_TIMEOUT = 5.0

BASE_JQL = 'assignee in (currentUser()) AND status in ("In Progress","To Do")'
ACTION_JQL = f"{BASE_JQL} order by status ASC"
ACTION_URL = f"https://simx.atlassian.net/issues/?jql={quote(ACTION_JQL, safe='')}"
CREATE_NO_WINDOW = 0x08000000

STATUS_ICONS = [
    ("To Do", "📋"),
    ("In Progress", "🛠️"),
]
ISSUE_KEY_RE = re.compile(r"\b[A-Z][A-Z0-9]+-\d+\b")


def send_to_pipe(payload: dict) -> bool:
    data = json.dumps(payload, ensure_ascii=False) + "\n"
    deadline = time.time() + CONNECT_TIMEOUT
    last_error: Optional[Exception] = None
    while time.time() < deadline:
        try:
            with open(PIPE_NAME, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(data)
            return True
        except (FileNotFoundError, OSError) as exc:
            last_error = exc
            time.sleep(0.1)
    if last_error is not None:
        print(f"Traycer send failed: {last_error}", file=sys.stderr)
    return False


def update_well(text: str, action: Optional[str]) -> None:
    send_to_pipe({"op": "add", "well": WELL_ID, "width": WELL_WIDTH})
    message = {"op": "set", "well": WELL_ID, "text": text}
    if action:
        message["action"] = action
    send_to_pipe(message)


def parse_acli_count(output: str) -> int:
    stripped = output.strip()
    if not stripped:
        raise RuntimeError("ACLI returned no output")

    try:
        data = json.loads(stripped)
    except json.JSONDecodeError:
        data = None
    else:
        if isinstance(data, dict):
            for key in ("total", "issueCount", "count", "records"):
                value = data.get(key)
                if isinstance(value, int):
                    return value
            for value in data.values():
                if isinstance(value, int):
                    return value
                if isinstance(value, list):
                    return len(value)
        elif isinstance(data, list):
            return len(data)

    match = re.search(r"total[^0-9]*([0-9]+)", stripped, re.IGNORECASE)
    if match:
        return int(match.group(1))
    match = re.search(r"issues[^0-9]*([0-9]+)", stripped, re.IGNORECASE)
    if match:
        return int(match.group(1))

    keys = set()
    for line in stripped.splitlines():
        key_match = ISSUE_KEY_RE.search(line)
        if key_match:
            keys.add(key_match.group(0))
    if keys:
        return len(keys)

    raise RuntimeError("Unable to parse issue count from ACLI output")


def run_acli_count(jql: str) -> int:
    base_cmd = [
        "acli",
        "jira",
        "workitem",
        "search",
        "--jql",
        jql,
        "--limit",
        "0",
    ]
    attempts = [base_cmd + ["--outputFormat", "json"], base_cmd]
    last_error = ""
    for cmd in attempts:
        startup = STARTUPINFO()
        startup.dwFlags |= STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            startupinfo=startup,
            creationflags=CREATE_NO_WINDOW,
        )
        if proc.returncode == 0:
            combined = proc.stdout if proc.stdout else proc.stderr
            return parse_acli_count(combined)
        last_error = proc.stderr.strip() or proc.stdout.strip()
    raise RuntimeError(f"ACLI failed: {last_error}")


def main() -> int:
    if shutil.which("acli") is None:
        print("acli executable not found in PATH.", file=sys.stderr)
        return 1

    try:
        counts = {
            status: run_acli_count(f"{BASE_JQL} AND status = \"{status}\"")
            for status, _ in STATUS_ICONS
        }
    except Exception as exc:
        print(f"Jira query failed: {exc}", file=sys.stderr)
        update_well("Jira fetch failed", None)
        return 1

    text = "".join(icon + str(counts.get(status, 0)) for status, icon in STATUS_ICONS)
    print(text)
    update_well(text, ACTION_URL)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
