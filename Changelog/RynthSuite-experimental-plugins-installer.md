# RynthSuite experimental plugins: installable and folder-independent

**Release date:** 2026-10-04
**Branch:** `feat/ilt-hub` (local test build, not on `main`)
**Ships in:** RynthCore installer `RynthCore-Setup-2026.10.4.3.exe`, as optional "Experimental plugins" components

## Versions
| Plugin | Previous | Now |
|---|---|---|
| RynthChat | 0.1.0 | 0.1.1 |
| RynthJuice | 0.1.0 | 0.1.1 |
| RynthNav | 0.5.4 | 0.5.5 |
| RynthTracker | 0.1.0 | 0.1.1 |
| RynthVision | 0.1.0 | 0.1.1 |

Each `.csproj` now carries `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` (`<ver>-experimental`)
matching the plugin's `VersionPointer`. Before, the DLL file version defaulted to 1.0.0.0.

## No more hard-coded `C:\Games\RynthCore`
Both plugins now link `Shared/RynthInstallPaths.cs`, so they follow the RynthCore folder chosen in the installer:
- **RynthNav:** tiles and `portals.tsv` are read from `<RynthCore>\NavData` (was `C:\Games\RynthCore\NavData`).
- **RynthJuice:** the `cell.dat` search for indoor damage numbers checks `<RynthCore>\AcClient` (was `C:\Games\RynthCore\AcClient`).
  The running client's own folder is still checked first, and the well-known AC install folders after.

RynthChat, RynthTracker and RynthVision had no install-folder paths; their settings stay in `%APPDATA%\RynthCore`.

## Install layout
Each plugin installs to `<RynthSuite>\<Name>\RynthCore.Plugin.<Name>.dll`, the same layout as a dev deploy, and is
registered with the launcher automatically. The `<PublishDir>` in RynthJuice, RynthNav and RynthVision still points at
`C:\Games\RynthSuite\<Name>\` for a bare `dotnet publish`; the installer build overrides it.

## Not changed
`Tools/RynthNav.Baker` still defaults `--out` to `C:\Games\RynthCore\NavData`. Pass `--out <RynthCore>\NavData` when
RynthCore is installed elsewhere.
