# Background Tasks

Traycer can manage external processes through the defaults file and the tray menu. Tasks let you launch one-off commands on startup or register jobs with Windows Task Scheduler.

## Defining tasks

Add a `tasks` array to `traycer.defaults.json`. Each entry uses the following fields:

| Field | Description |
| --- | --- |
| `id` | Unique identifier displayed in the tray menu. |
| `command` | Executable to launch (absolute path or relative to the Traycer binary). |
| `args` | Optional arguments string (environment variables are expanded). |
| `mode` | Either `"once"` for direct process launches or `"schedule"` for Task Scheduler integration. |
| `autoStart` | When `true`, Traycer starts the task on launch (or immediately queues the scheduled task). |
| `workingDirectory` | Optional working directory; relative paths resolve against the app directory. |
| `schedule` | Required when `mode` is `"schedule"`; see below. |

Example:

```json
"tasks": [
  {
    "id": "weather-refresh",
    "command": "python.exe",
    "args": "\"scripts/weather.py\" 47.61 -122.33",
    "mode": "once",
    "autoStart": true,
    "workingDirectory": ".."
  },
  {
    "id": "calendar-loop",
    "command": "pythonw.exe",
    "args": "\"scripts/calendar_overview.py\" https://example.com/private.ics",
    "mode": "schedule",
    "autoStart": true,
    "schedule": {
      "frequency": "minute",
      "interval": 5,
      "start": "06:00"
    }
  }
]
```

## Scheduled triggers

When `mode` is `"schedule"`, provide a `schedule` object:

- `frequency` – `minute`, `hour`, `hours`, `daily`, `day`, `logon`, or `once`.
- `interval` – optional integer controlling repetition. For example, `frequency: "minute"` with `interval: 5` runs every five minutes.
- `start` – optional `HH:mm` time for the first run.

Traycer registers the task under the `Traycer` folder in Task Scheduler using the current interactive user. When the app exits it cleans up any scheduled entries it created.

## Tray controls

Open the Traycer notify icon to see a **Tasks** submenu. Options vary by mode:

- **Run now** – launches the process or immediately triggers the scheduled job.
- **Kill process** – available for `once` tasks currently running.
- **Stop** – cancels a scheduled task that is currently active.

Traycer keeps track of running processes and displays their PID next to the task name when available.

## Tips

- Use `pythonw.exe` (or similar) for scheduled jobs when you want a windowless experience.
- Set `autoStart` to `false` for manual utilities you only trigger from the tray menu.
- Combine tasks with IPC scripts to automate well updates on a cadence.
