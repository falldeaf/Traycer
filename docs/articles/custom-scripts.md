# Custom Script Tutorial

This walkthrough covers two common ways to drive Traycer wells with your own automation scripts: a timer-style job that runs, updates, and exits, and a long-lived daemon that stays connected to the named pipe.

## Prerequisites

- Traycer HUD running locally.
- A language/runtime capable of writing UTF-8 lines to the named pipe `\\.\pipe\TraycerHud` (examples below use PowerShell and Python).

## Pattern 1: Timer job

Use a script that performs a single update and exit. Pair it with either Windows Task Scheduler or a Traycer `once` task to run it on a cadence.

Example PowerShell script (`scripts/update-weather.ps1`):

```powershell
param(
    [string]$Well = "weather"
)

$payload = @{
    op   = "set"
    well = $Well
    text = "🌦️  $(Get-Date -Format 't')"
    bg   = "#33223333"
} | ConvertTo-Json -Compress

$pipe = new-object System.IO.Pipes.NamedPipeClientStream('.', 'TraycerHud', [System.IO.Pipes.PipeDirection]::Out)
$pipe.Connect(2000)
$writer = new-object System.IO.StreamWriter($pipe, New-Object System.Text.UTF8Encoding($false))
$writer.AutoFlush = $true
$writer.WriteLine($payload)
$writer.Dispose(); $pipe.Dispose()
```

Schedule it every five minutes (Task Scheduler) or add to the defaults:

```json
{
  "id": "weather-poll",
  "command": "pwsh",
  "args": "-File \"scripts/update-weather.ps1\"",
  "mode": "schedule",
  "autoStart": true,
  "schedule": { "frequency": "minute", "interval": 5 }
}
```

## Pattern 2: Background daemon

For continuous feeds, keep the script connected to the pipe, emit updates as needed, and clean up on exit.

Example Python daemon (`scripts/stock_daemon.py`):

```python
import json
import os
import sys
import time
import ctypes
from ctypes import wintypes

PIPE_NAME = r"\\.\pipe\TraycerHud"
WELL_ID = "stocks"

class Pipe:
    def __enter__(self):
        while True:
            try:
                self.handle = open(PIPE_NAME, "w", encoding="utf-8", newline="\n")
                break
            except OSError:
                time.sleep(0.5)
        return self

    def send(self, payload):
        self.handle.write(json.dumps(payload, ensure_ascii=False) + "\n")
        self.handle.flush()

    def __exit__(self, exc_type, exc, tb):
        self.handle.close()

if __name__ == "__main__":
    try:
        with Pipe() as pipe:
            pipe.send({"op": "add", "well": WELL_ID, "width": 220})
            pipe.send({"op": "set", "well": WELL_ID, "text": "📈 Initializing"})
            while True:
                price = fetch_price_somehow()
                pipe.send({"op": "set", "well": WELL_ID, "text": f"📈 {price:$}"})
                time.sleep(10)
    except KeyboardInterrupt:
        with Pipe() as pipe:
            pipe.send({"op": "remove", "well": WELL_ID})
```

Key ideas:

- Ensure the script retries connecting to the pipe until Traycer is ready.
- Send an `add` message once to create the well dynamically.
- On shutdown, remove the well so the HUD stays tidy.

## Testing tips

- Use `pwsh -Command "Get-Content -Wait -Path \"\\.\pipe\TraycerHud\""` in a second console to inspect outgoing messages.
- Wrap long-running scripts in Traycer `once` tasks with `autoStart` to manage their lifecycle through the tray menu.
- Remember that all messages must be UTF-8 and newline-terminated.
