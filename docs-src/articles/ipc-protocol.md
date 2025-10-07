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

