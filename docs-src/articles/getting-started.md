# Getting Started

This guide walks through installing Traycer HUD, seeding its configuration, and understanding day-to-day usage.

## Prerequisites

- Windows 10 or later with .NET 8 desktop runtime.
- Clone of the Traycer repository.
- Optional: Python or other tooling referenced by your background tasks.

## Build and run

1. Restore and build from the repository root:
   ```powershell
   dotnet build src/Traycer.csproj
   ```
2. Launch the HUD:
   ```powershell
   dotnet run --project src/Traycer.csproj
   ```
3. The tray icon appears in the notification area. Right-click it to open commands for wells and background tasks.

## Defaults file

Traycer will look for `traycer.defaults.json` under `%LOCALAPPDATA%\Traycer`. If the file is missing, the app offers to scaffold a template based on the copy bundled beside the executable.

Key sections to configure:

- `placement` – tune height, padding, and corner radius of the HUD.
- `wells` – declare the columns shown on screen, including width and optional index order.
- `updates` – seed initial text, colors, and click actions for wells.
- `tasks` – define background processes or scheduled jobs managed through the tray menu.

## Interaction basics

- Toggle click-through mode with **Win+Alt+H**. While click-through is off, left-clicking a well triggers its bound action.
- The HUD reasserts topmost order automatically and when the window deactivates.
- Use the tray menu to remove wells, start or stop scheduled tasks, and exit the app.

For the JSON commands that drive the HUD dynamically, see the [IPC Protocol](ipc-protocol.md) article.
