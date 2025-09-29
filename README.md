# Traycer HUD

Traycer is a lightweight Windows overlay that keeps a strip of live "wells" pinned above the taskbar. Update it over a named pipe, automate background scripts, and control everything from a notify icon.

## Highlights

- Always-on-top heads-up display with column-based layout.
- JSON/NDJSON IPC protocol over a named pipe for remote updates.
- Tray menu helpers for click-through toggling, well management, and background tasks.
- Built-in scheduler integration to trigger commands through Windows Task Scheduler.

## Quick start

1. Restore and build:
   ```powershell
   dotnet build src/Traycer.csproj
   ```
2. Run the HUD:
   ```powershell
   dotnet run --project src/Traycer.csproj
   ```
3. Provide or accept the generated defaults file when prompted, then drive the HUD with your automation scripts.

## Documentation

- [Getting Started](docs/articles/getting-started.md)
- [IPC JSON Protocol](docs/articles/ipc-protocol.md)
- [Defaults Configuration](docs/articles/defaults.md)
- [Background Tasks](docs/articles/tasks.md)
- [Custom Script Tutorial](docs/articles/custom-scripts.md)
- API Reference *(generate locally with DocFX; instructions below)*

Generate the documentation site locally:

```powershell
dotnet tool restore
cd docs
# produce YAML metadata and the static site
dotnet docfx metadata
dotnet docfx build
```

The generated site is emitted to `docs/_site/` (ignored from source control). Open `_site/index.html` in a browser to browse the pages.

Issues and pull requests are welcome. Please run `dotnet format` and the build/test suite before submitting changes.
