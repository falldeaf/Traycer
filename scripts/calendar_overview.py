#!/usr/bin/env python3
"""Traycer calendar overview script (single compact well).

Fetch a Google Calendar (ICS) feed and render a compact ASCII overview that can
be pushed into the Traycer HUD or printed to stdout.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.request
import time
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Optional

try:
    from zoneinfo import ZoneInfo  # Python 3.9+
except ImportError:  # pragma: no cover
    ZoneInfo = None  # type: ignore

PIPE_NAME = r"\\.\\pipe\\TraycerHud"
CALENDAR_ICON = "\U0001F4C5"
BLOCK_MINUTES = 30
DEFAULT_BLOCK_COUNT = 12
DEFAULT_TIMEOUT = 15
CONNECT_TIMEOUT = 5.0

_DURATION_RE = re.compile(
    r"P(?:(?P<days>\d+)D)?(?:T(?:(?P<hours>\d+)H)?(?:(?P<minutes>\d+)M)?(?:(?P<seconds>\d+)S)?)?",
    re.IGNORECASE,
)


class TraycerError(Exception):
    """Raised when the Traycer pipe cannot be written."""


class TraycerClient:
    """Helper for sending JSON messages to the Traycer named pipe."""

    def __init__(self, pipe: str = PIPE_NAME) -> None:
        self._pipe = pipe

    def send(self, payload: Dict[str, object]) -> None:
        data = json.dumps(payload, separators=(",", ":")) + "\n"

        deadline = time.time() + CONNECT_TIMEOUT
        last_error: Optional[TraycerError] = None
        while True:
            try:
                with open(self._pipe, "w", encoding="utf-8", newline="\n") as pipe:
                    pipe.write(data)
                return
            except FileNotFoundError as exc:
                err = TraycerError(f"Traycer pipe not found: {self._pipe}")
                err.__cause__ = exc
                last_error = err
            except OSError as exc:
                err = TraycerError(f"Failed writing to Traycer pipe: {exc}")
                err.__cause__ = exc
                last_error = err

            if time.time() >= deadline:
                raise last_error if last_error is not None else TraycerError("Failed opening Traycer pipe")
            time.sleep(0.1)

    def ensure_well(self, well_id: str, width: int, index: Optional[int] = None) -> None:
        payload: Dict[str, object] = {"op": "add", "well": well_id, "width": width}
        if index is not None:
            payload["index"] = index
        self.send(payload)

    def set_text(self, well_id: str, text: str) -> None:
        self.send({"op": "set", "well": well_id, "text": text})


def fetch_ical(url: str, timeout: int = DEFAULT_TIMEOUT) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "traycer-calendar/1.0"})
    with urllib.request.urlopen(request, timeout=timeout) as response:  # nosec: user-supplied URL
        charset = response.headers.get_content_charset() or "utf-8"
        body = response.read()
    return body.decode(charset, errors="replace")


def unfold_ics_lines(raw: str) -> List[str]:
    lines: List[str] = []
    for line in raw.splitlines():
        if line.startswith(" ") or line.startswith("\t"):
            if lines:
                lines[-1] += line[1:]
        else:
            lines.append(line)
    return lines


def unescape_ical_text(value: str) -> str:
    return (
        value.replace("\\n", " / ")
        .replace("\\N", " / ")
        .replace("\\,", ",")
        .replace("\\;", ";")
        .replace("\\\\", "\\")
    )


def parse_duration(raw: str) -> Optional[timedelta]:
    match = _DURATION_RE.fullmatch(raw.strip())
    if not match:
        return None
    parts = {key: int(val) if val is not None else 0 for key, val in match.groupdict().items()}
    return timedelta(
        days=parts.get("days", 0),
        hours=parts.get("hours", 0),
        minutes=parts.get("minutes", 0),
        seconds=parts.get("seconds", 0),
    )


def parse_ical_datetime(value: str, params: Dict[str, str], local_tz: timezone) -> datetime:
    value = value.strip()
    tzid = params.get("TZID")
    value_type = params.get("VALUE", "DATE-TIME").upper()

    if value_type == "DATE":
        dt = datetime.strptime(value, "%Y%m%d")
        return dt.replace(tzinfo=local_tz)

    is_utc = value.endswith("Z")
    if is_utc:
        value = value[:-1]

    dt_obj: Optional[datetime] = None
    for fmt in ("%Y%m%dT%H%M%S", "%Y%m%dT%H%M"):
        try:
            dt_obj = datetime.strptime(value, fmt)
            break
        except ValueError:
            continue
    if dt_obj is None:
        raise ValueError(f"Unsupported datetime format: {value}")

    if is_utc:
        dt_obj = dt_obj.replace(tzinfo=timezone.utc)
    elif tzid and ZoneInfo is not None:
        try:
            dt_obj = dt_obj.replace(tzinfo=ZoneInfo(tzid))
        except Exception:
            dt_obj = dt_obj.replace(tzinfo=local_tz)
    else:
        dt_obj = dt_obj.replace(tzinfo=local_tz)

    return dt_obj.astimezone(local_tz)


def parse_ics_events(raw: str, local_tz: timezone) -> List[Dict[str, object]]:
    events: List[Dict[str, object]] = []
    current: Dict[str, Tuple[str, Dict[str, str]]] = {}
    inside_event = False

    for line in unfold_ics_lines(raw):
        if line.startswith("BEGIN:VEVENT"):
            inside_event = True
            current = {}
            continue
        if line.startswith("END:VEVENT"):
            if inside_event and "DTSTART" in current:
                start_val, start_params = current["DTSTART"]
                start = parse_ical_datetime(start_val, start_params, local_tz)
                if "DTEND" in current:
                    end_val, end_params = current["DTEND"]
                    end = parse_ical_datetime(end_val, end_params, local_tz)
                elif "DURATION" in current:
                    duration = parse_duration(current["DURATION"][0]) or timedelta(minutes=BLOCK_MINUTES)
                    end = start + duration
                else:
                    end = start + timedelta(minutes=BLOCK_MINUTES)
                if end <= start:
                    end = start + timedelta(minutes=BLOCK_MINUTES)
                summary = unescape_ical_text(current.get("SUMMARY", ("", {}))[0]) if "SUMMARY" in current else ""
                events.append({
                    "start": start,
                    "end": end,
                    "summary": summary.strip(),
                })
            inside_event = False
            current = {}
            continue
        if not inside_event:
            continue
        if ":" not in line:
            continue
        key_part, value = line.split(":", 1)
        parts = key_part.split(";")
        key = parts[0].upper()
        params: Dict[str, str] = {}
        for part in parts[1:]:
            if "=" in part:
                p_key, p_val = part.split("=", 1)
                params[p_key.upper()] = p_val
        if key in {"DTSTART", "DTEND", "SUMMARY", "DURATION"}:
            current[key] = (value.strip(), params)

    events.sort(key=lambda evt: evt["start"])
    return events


def align_to_block(dt: datetime, block_minutes: int) -> datetime:
    minutes = dt.hour * 60 + dt.minute
    floored = (minutes // block_minutes) * block_minutes
    return dt.replace(hour=0, minute=0, second=0, microsecond=0) + timedelta(minutes=floored)


def block_overlaps(event: Dict[str, object], start: datetime, end: datetime) -> bool:
    return event["start"] < end and event["end"] > start  # type: ignore[index]


def build_timeline(
    now: datetime,
    events: List[Dict[str, object]],
    block_minutes: int,
    block_count: int,
) -> str:
    base = align_to_block(now, block_minutes)
    delta = timedelta(minutes=block_minutes)
    chars: List[str] = []

    for i in range(block_count):
        start = base + delta * i
        end = start + delta
        event = next((evt for evt in events if block_overlaps(evt, start, end)), None)
        chars.append("=" if event else "-")

    return "".join(chars)


def compose_calendar_line(
    now: datetime,
    events: List[Dict[str, object]],
    block_minutes: int,
    block_count: int,
) -> str:
    timeline = build_timeline(now, events, block_minutes, block_count)
    return f"{CALENDAR_ICON} {timeline}"


def compose_error_line(message: str) -> str:
    trimmed = message.strip() or "Calendar fetch failed"
    if len(trimmed) > 80:
        trimmed = trimmed[:77] + "..."
    return f"{CALENDAR_ICON} ERROR {trimmed}"


def local_timezone() -> timezone:
    local = datetime.now().astimezone().tzinfo
    if isinstance(local, timezone):
        return local
    tz = datetime.now(timezone.utc).astimezone().tzinfo
    return tz or timezone.utc


def parse_args(argv: Optional[List[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Traycer calendar overview driver")
    parser.add_argument("url", help="Google Calendar secret ICS URL")
    parser.add_argument(
        "--pipe",
        default=os.environ.get("TRAYCER_PIPE", PIPE_NAME),
        help="Traycer named pipe (default: %(default)s)",
    )
    parser.add_argument(
        "--blocks",
        type=int,
        default=DEFAULT_BLOCK_COUNT,
        help="Number of half-hour blocks to display (default: %(default)s)",
    )
    parser.add_argument(
        "--timeout",
        type=int,
        default=DEFAULT_TIMEOUT,
        help="HTTP timeout for ICS fetch (default: %(default)s)",
    )
    parser.add_argument(
        "--width",
        type=int,
        default=100,
        help="Traycer well width in pixels (default: %(default)s)",
    )
    parser.add_argument(
        "--well",
        default="calendar",
        help="Target Traycer well id (default: %(default)s)",
    )
    parser.add_argument(
        "--index",
        type=int,
        default=None,
        help="Optional Traycer column index when inserting the well",
    )
    parser.add_argument(
        "--stdout-only",
        action="store_true",
        help="Print the calendar line instead of sending it to Traycer",
    )
    return parser.parse_args(argv)


def main(argv: Optional[List[str]] = None) -> int:
    args = parse_args(argv)
    if args.blocks <= 0:
        print("--blocks must be positive", file=sys.stderr)
        return 1

    local_tz = local_timezone()
    now = datetime.now(local_tz)

    try:
        ical_text = fetch_ical(args.url, timeout=args.timeout)
        events = parse_ics_events(ical_text, local_tz)
        line = compose_calendar_line(now, events, BLOCK_MINUTES, args.blocks)
        success = True
    except Exception as exc:
        line = compose_error_line(str(exc))
        success = False

    if args.stdout_only:
        print(line)
        return 0 if success else 1

    try:
        client = TraycerClient(args.pipe)
        client.ensure_well(args.well, args.width, args.index)
        client.set_text(args.well, line)
    except TraycerError as exc:
        print(f"Failed to push update to Traycer: {exc}", file=sys.stderr)
        print(line)
        return 1

    return 0 if success else 1


if __name__ == "__main__":
    raise SystemExit(main())

