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

- Download the latest `TraycerSetup_<version>.exe` from [GitHub Releases](https://github.com/falldeaf/Traycer/releases).
- The installer creates a Start Menu shortcut and offers optional desktop and "Start with Windows" choices (unchecked by default).
- Traycer checks GitHub Releases daily for updates. You can force a check or trigger a winget/installer upgrade from the tray icon menu.
- Silent install switches follow Inno Setup conventions: `/VERYSILENT /NORESTART` for fully silent, `/SILENT /NORESTART` to show minimal UI.

## Release Automation

- Pushes and pull requests against `main` run a Windows build, optional tests, and publish a self-contained artifact (`.github/workflows/ci.yml`).
- Creating a git tag in the form `v<major>.<minor>` (for example `git tag v1.2 && git push origin v1.2`) triggers the release workflow (`.github/workflows/release.yml`). That job:
  - Builds the self-contained publish, Inno installer, and portable single-file executable.
  - Uploads installer + portable binaries (and checksums) to the matching GitHub Release.
  - Optionally updates winget when the `WINGET_TOKEN` secret is configured.
- Versioning is purely manual: choose the next `vX.Y` tag you want to ship.

## Documentation

- Hosted docs: <https://falldeaf.github.io/Traycer/>
- Source articles live under `docs-src/`; the generated static site (served by GitHub Pages) is committed in `docs/`.
- Key topics:
  - [Getting Started](https://falldeaf.github.io/Traycer/articles/getting-started.html)
  - [IPC JSON Protocol](https://falldeaf.github.io/Traycer/articles/ipc-protocol.html)
  - [Defaults Configuration](https://falldeaf.github.io/Traycer/articles/defaults.html)
  - [Background Tasks](https://falldeaf.github.io/Traycer/articles/tasks.html)
  - [Custom Script Tutorial](https://falldeaf.github.io/Traycer/articles/custom-scripts.html)
  - [API Reference](https://falldeaf.github.io/Traycer/api/Traycer.html)

Generate (and update) the documentation site locally:

```powershell
docfx metadata docs-src/docfx.json
docfx build docs-src/docfx.json
```

The commands emit YAML metadata under `docs-src/api/` and write the final static site to `docs/`. Commit both `docs/` and `docs-src/api/` so GitHub Pages stays in sync.

Issues and pull requests are welcome. Please run `dotnet format` and the build/test suite before submitting changes.


