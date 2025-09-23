# IPC JSON Protocol

Traycer listens on the named pipe `\\.\pipe\TraycerHud` for newline-delimited UTF-8 JSON payloads. Each message is applied immediately and the pipe remains one-way—no acknowledgements are sent.

## Colors

Color fields accept `#RRGGBB`, `#AARRGGBB`, `0xAARRGGBB`, or `hex:AARRGGBB` formats.

## Click actions

A well may define an `action` that runs when the user left-clicks while click-through mode is **off** (toggle with **Win+Alt+H**).

- If the action string contains `://`, it is launched via the shell.
- Otherwise Traycer executes `cmd.exe /c <action>`.

## Operations

### `placement`

Adjust the HUD geometry for the current session.

```json
{"op":"placement","height":24,"bottomOffset":2,"padding":6,"cornerRadius":8}
```

| Field | Description |
| --- | --- |
| `height` | Content height in pixels |
| `bottomOffset` | Offset from the screen bottom in pixels |
| `padding` | Outer and inner padding in pixels |
| `cornerRadius` | Corner radius in pixels |

### `config`

Replace **all** wells with the supplied collection.

```json
{"op":"config","wells":[{"id":"weather","width":240},{"id":"build","width":180}]}
```

Each entry supplies:

- `id` – unique well identifier
- `width` – width in pixels

### `add`

Insert a well without clearing existing wells.

```json
{"op":"add","well":"alerts","width":160,"index":2}
```

- If `index` is omitted, the well is appended.
- Supplying an existing `well` id resizes that column instead.

### `remove`

```json
{"op":"remove","well":"ram"}
```

### `resize`

```json
{"op":"resize","well":"weather","width":300}
```

### `set`

Set text, styling, and action for a single well.

```json
{"op":"set","well":"build","text":"✔ Passing","bg":"#33305533","fg":"#FFFFFF","blink":false,"action":"start https://ci.example.com"}
```

Fields:

- `text` – label content (emoji supported)
- `fg`, `bg` – colors (see formats above)
- `blink` – boolean hint used for emphasis styling
- `action` – command or URL to run on click

### `bulk`

Apply multiple `set` updates in a single message.

```json
{"op":"bulk","updates":[
  {"op":"set","well":"net","text":"📶  142 Mbps"},
  {"op":"set","well":"cpu","text":"🖥️  34%"},
  {"op":"set","well":"ram","text":"📦  61%"}
]}
```

### `bind`

Bind or update an action without touching text or colors.

```json
{"op":"bind","well":"meeting","action":"start https://meet.example.com/room"}
```

## Defaults file

Traycer requires a defaults file on startup. If none is found, the app offers to copy the template bundled next to the executable.

Lookup order:

1. `AppContext.BaseDirectory\traycer.defaults.json`
2. `%LOCALAPPDATA%\Traycer\traycer.defaults.json`
3. Path supplied by the `TRAYCER_DEFAULTS` environment variable (highest priority)

Keys are case-insensitive. Example:

```json
{
  "placement": { "height":24, "bottomOffset":2, "padding":6, "cornerRadius":8 },
  "wells": [
    { "id":"weather", "width":240, "index":0 },
    { "id":"build",   "width":180 }
  ],
  "updates": [
    { "well":"weather","text":"🌦️  73°F","bg":"#33223333" }
  ],
  "tasks": [
    {
      "id": "weather-refresh",
      "command": "python.exe",
      "args": "\"weather.py\" 47.61 -122.33",
      "mode": "once",
      "autoStart": false
    },
    {
      "id": "calendar-loop",
      "command": "python.exe",
      "args": "\"calendar_overview.py\" https://example.com/private.ics",
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

Notes:

- `wells[].index` controls layout order; otherwise file order is preserved.
- Use runtime messages (`add`, `remove`, `resize`) for incremental layout changes.
- `tasks` entries launch background commands. `mode: "once"` optionally auto-starts a process, while `mode: "schedule"` registers with Windows Task Scheduler under the *Traycer* folder.
- Scheduled tasks accept `frequency` (`minute`, `hourly`, `daily`, `logon`), an optional `interval`, and optional `start` (`HH:mm`). The tray menu can start/stop scheduled jobs or run/kill on-demand commands.

## Additional notes

- Messages are processed sequentially without batching; use `bulk` to apply grouped updates.
- Escape `#` in PowerShell when passing color literals, e.g. `--bg "#33223333"`.
- The HUD reasserts its topmost z-order periodically and whenever it deactivates.
- The tray icon exposes contextual commands for wells (run action, remove) and tasks (run/stop/kill). Scheduled entries created by Traycer are removed on exit.

## Example session

```
{"op":"placement","height":24,"bottomOffset":2,"padding":6}
{"op":"config","wells":[{"id":"weather","width":240},{"id":"build","width":180},{"id":"cpu","width":110}]}
{"op":"set","well":"weather","text":"🌦️  71°F Light rain"}
{"op":"bind","well":"build","action":"start https://ci.example.com"}
{"op":"bulk","updates":[
  {"op":"set","well":"build","text":"⚙️ Running.","bg":"#33333322"},
  {"op":"set","well":"cpu","text":"🖥️  34%"}
]}
{"op":"add","well":"alerts","width":160,"index":2}
{"op":"set","well":"alerts","text":"🔔  3 alerts","bg":"hex:33222222"}
```
