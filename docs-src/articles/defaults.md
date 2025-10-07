# Defaults Configuration

Traycer requires a defaults file before the HUD can render. This article explains where the file lives, how it is discovered, and the sections you can use to seed layout, text, actions, and background tasks.

## File location

Traycer searches for `traycer.defaults.json` in the following order:

1. `AppContext.BaseDirectory\traycer.defaults.json` – beside the executable.
2. `%LOCALAPPDATA%\Traycer\traycer.defaults.json` – the per-user copy.
3. Path referenced by the `TRAYCER_DEFAULTS` environment variable – highest priority.

The first existing file wins. If none are found, Traycer prompts to create a template under `%LOCALAPPDATA%\Traycer` using the bundled sample.

## Minimal template

```json
{
  "placement": { "height": 24, "bottomOffset": 2, "padding": 6, "cornerRadius": 8 },
  "wells": [
    { "id": "weather", "width": 240 },
    { "id": "build",   "width": 180 }
  ],
  "updates": [
    { "well": "weather", "text": "🌦️ 73°F", "bg": "#33223333" }
  ]
}
```

Each top-level section is optional, but the file itself must be valid JSON. Keys are case-insensitive.

### placement

Controls initial window geometry.

| Field | Description |
| --- | --- |
| `height` | Content height in pixels. |
| `bottomOffset` | Distance from the bottom edge of the primary display. |
| `padding` | Applied to the window chrome and well content. |
| `cornerRadius` | Radius for the outer frame. |

### wells

Defines the columns rendered on startup. Each entry supplies:

- `id` – unique identifier used for IPC updates and tray actions.
- `width` – initial width in pixels.
- `index` (optional) – absolute position. When omitted, file order is used.

Use runtime messages (`add`, `remove`, `resize`) for incremental layout tweaks once the HUD is running.

### updates

Seeds well text/color/action state the moment the HUD loads. You can include the same fields accepted by the `set` IPC operation:

- `text` – label content. Emoji are supported.
- `fg` / `bg` – hex colors, e.g. `#RRGGBB`, `#AARRGGBB`, `hex:33223333`.
- `blink` – boolean accent hint.
- `action` – command or URL invoked on click while click-through is disabled.

### tasks

Optional array for background work managed through the tray icon. See [Background Tasks](tasks.md) for full details.

### actions

Top-level dictionary mapping well IDs to action strings. This is a shorthand for seeding click handlers when you do not need to set text or colors.

## Editing tips

- Keep a copy of the template under source control and sync it to `%LOCALAPPDATA%` during deployment.
- Validate JSON with `pwsh` or any linter before running Traycer; the HUD exits on parse errors.
- When iterating on layout, start with the defaults file and then fine-tune at runtime with IPC messages until the layout feels right.
