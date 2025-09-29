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

## Installation & Updates

- Download the latest `TraycerSetup_<version>.exe` from [GitHub Releases](https://github.com/thomas-mardis/Traycer/releases).
- The installer creates a Start Menu shortcut and offers optional desktop and "Start with Windows" choices (unchecked by default).
- Traycer checks GitHub Releases daily for updates. You can force a check or trigger a winget/installer upgrade from the tray icon menu.
- Silent install switches follow Inno Setup conventions: `/VERYSILENT /NORESTART` for fully silent, `/SILENT /NORESTART` to show minimal UI.

## Release Automation

- Pushes and pull requests against `main` run a Windows build, optional tests, and publish a self-contained artifact (`.github/workflows/ci.yml`).
- [release-please](https://github.com/googleapis/release-please) manages semantic versioning, CHANGELOG updates, and release PRs (`.github/workflows/release-please.yml`).
- Tagging `vX.Y.Z` runs `.github/workflows/release-tag.yml` to publish the app, build/sign the installer, attach assets to the GitHub Release, and optionally open a winget PR when secrets are provided.

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


