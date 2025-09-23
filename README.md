
# Traycer HUD — IPC JSON Protocol (concise)

**Purpose:** Drive a Windows HUD that renders fixed-width “wells” (columns) in a slim, always-on-top strip.
**Transport:** UTF-8 NDJSON over Windows **Named Pipe** `\\.\pipe\TraycerHud`. One JSON object per line. No replies (fire-and-forget). Unknown fields are ignored.

## Colors

Accepts `#RRGGBB`, `#AARRGGBB`, `0xAARRGGBB`, or `hex:AARRGGBB`.

## Click actions

Per-well `action` runs on left-click **only when click-through is OFF** (toggle with **Win+Alt+H**).

* If `action` contains `://` → opened via shell.
* Else → executed as `cmd.exe /c <action>`.

## Ops (messages)

### `placement`

Adjust strip geometry (applies immediately; persists for session).

```json
{"op":"placement","height":24,"bottomOffset":2,"padding":6,"cornerRadius":8}
```

* **height**: content height px
* **bottomOffset**: px up from screen bottom
* **padding**: px outer & inner padding
* **cornerRadius**: px

### `config`

Replace **all** wells in the given order.

```json
{"op":"config","wells":[{"id":"weather","width":240},{"id":"build","width":180}]}
```

* **wells\[i].id** (string), **wells\[i].width** (px)

### `add`

Insert a well **without resetting others**.

```json
{"op":"add","well":"alerts","width":160,"index":2}
```

* If **index** omitted → append.
* If **well** already exists → **resize** that well to `width`.

### `remove`

```json
{"op":"remove","well":"ram"}
```

### `resize`

```json
{"op":"resize","well":"weather","width":300}
```

### `set`

Set content/styling/action for a single well.

```json
{"op":"set","well":"build","text":"✅ Passing","bg":"#33305533","fg":"#FFFFFF","blink":false,"action":"start https://ci.example.com"}
```

* **text** (string, emoji ok), **fg/bg** (color), **blink** (bool style hint), **action** (see “Click actions”)

### `bulk`

Batch multiple `set` updates.

```json
{"op":"bulk","updates":[
  {"op":"set","well":"net","text":"📶  142 Mbps"},
  {"op":"set","well":"cpu","text":"🧠  34%"},
  {"op":"set","well":"ram","text":"🧵  61%"}
]}
```

### `bind`

Bind/update an action without changing text/colors.

```json
{"op":"bind","well":"meeting","action":"start https://meet.example.com/room"}
```

## Defaults file (optional, auto-loaded at startup)

Defaults file required. Traycer exits if it cannot load a valid defaults file.

Search order:

1. `AppContext.BaseDirectory\traycer.defaults.json`
2. `%LOCALAPPDATA%\Traycer\defaults.json`
3. If env var **`TRAYCER_DEFAULTS`** points to a file, it wins.

**Case-insensitive keys.** Schema:

```json
{
  "placement": { "height":24, "bottomOffset":2, "padding":6, "cornerRadius":8 },
  "wells": [
    { "id":"weather", "width":240, "index":0 },
    { "id":"build",   "width":180 }
  ],
  "updates": [
    { "well":"weather","text":"⛅  73°F","bg":"#33223333" }
  ],
  "tasks": [
    {
      "id": "weather-refresh",
      "command": "python.exe",
      "args": ""weather.py" 47.61 -122.33",
      "mode": "once",
      "autoStart": false
    },
    {
      "id": "calendar-loop",
      "command": "python.exe",
      "args": ""calendar_overview.py" https://example.com/private.ics",
      "mode": "schedule",
      "autoStart": true,
      "schedule": { "frequency": "minute", "interval": 5, "start": "06:00" }
    }
  ],
  "actions": {
    "build": "start https://ci.example.com"
  }
}
```

* In **defaults**, `wells[].index` is honored for ordering (otherwise file order).
* At runtime, prefer `add/remove/resize` for incremental layout changes; `config` replaces all.
* `tasks` entries launch background commands. Use `mode` = `"once"` (optional `autoStart`) to spawn a process immediately, or `mode` = `"schedule"` to register the command with Windows Task Scheduler under the `Traycer` folder.
* Scheduled tasks accept `schedule.frequency` (`minute`, `hourly`, `daily`, `logon`), optional `interval`, and optional `start` (`HH:mm`). Tray menu entries let you start/stop scheduled jobs or run/kill on-demand commands.

## Notes / gotchas

* Messages are applied in arrival order; there’s no ACK. Senders should not assume atomic multi-line transactions—use `bulk` when needed.
* PowerShell treats `#` as comment; quote color literals, e.g. `--bg "#33223333"` (or use `hex:33223333`).
* The HUD reasserts topmost z-order periodically and on deactivation; it stays above the taskbar.
* The tray icon exposes wells (run action/remove) and task controls (start/stop scheduled jobs, run/kill on-demand commands). Traycer removes any scheduled entries from the `Traycer` folder when it exits.

## Minimal example session

```
{"op":"placement","height":24,"bottomOffset":2,"padding":6}
{"op":"config","wells":[{"id":"weather","width":240},{"id":"build","width":180},{"id":"cpu","width":110}]}
{"op":"set","well":"weather","text":"🌦️  71°F Light rain"}
{"op":"bind","well":"build","action":"start https://ci.example.com"}
{"op":"bulk","updates":[
  {"op":"set","well":"build","text":"🟡 Running…","bg":"#33333322"},
  {"op":"set","well":"cpu","text":"🧠  34%"}
]}
{"op":"add","well":"alerts","width":160,"index":2}
{"op":"set","well":"alerts","text":"🔔  3 alerts","bg":"hex:33222222"}
```
