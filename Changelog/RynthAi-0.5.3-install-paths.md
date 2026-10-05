# RynthAi 0.5.3: RynthSuite can live in any folder

**Release date:** 2026-10-04
**Plugin version:** `0.5.3-legacy-ui` (file version `0.5.3.0`)
**Previous release:** `0.5.2` (backed up to `C:\Games\RynthSuite\RynthAi\PreviousReleases\0.5.2\`)
**Branch:** `feat/ilt-hub` (local test build, not on `main`)
**Ships in:** the full RynthCore + RynthSuite installer `RynthCore-Setup-2026.10.4.2.exe`

## What changed

RynthAi, the Loot Editor and the Monster Editor no longer assume `C:\Games\RynthSuite`. They ask the new
`Shared/RynthInstallPaths.cs` resolver, which checks these places in order:

1. the `RYNTHSUITE_DIR` environment variable (handy for dev and test setups);
2. `HKCU\Software\Rynth\SuiteDir`, which a per-user install writes;
3. `HKLM\Software\Rynth\SuiteDir` (32-bit view), which an all-users install writes;
4. `C:\Games\RynthSuite`, the old default, so existing installs keep working with no change.

The resolver is a plain `RegGetValueW` call, so it is safe under NativeAOT. RynthCore keeps an identical copy
(`src/RynthCore.App/RynthInstallPaths.cs`) for the launcher, engine and loader.

### Paths now relative to the RynthSuite folder
Every former `C:\Games\RynthSuite\RynthAi\...` path is now `<SuiteDir>\RynthAi\...`:
- RynthAi settings, profiles and character data;
- `Logs\Diagnostics` (RynthLog);
- ILT Hub store, quests XML and gear suits;
- AutoVendor, loot profiles, metas, Lua UI, dungeon maps and hazard data, creature profiles, radar walls;
- the dashboard's Loot Editor and Monster Editor launch paths.

## Build / deploy
- Built against RynthCore `feat/full-installer` (based on `fix/tmds-dbus-advisory`, i.e. `main` plus the Tmds pin).
- NativeAOT publish: 0 errors, no IL or AOT warnings.
- Installed by the full installer to `<SuiteDir>\RynthAi\RynthCore.Plugin.RynthAi.dll`. The installer registers
  the DLL with the launcher automatically, so there is no manual "Add plugin" step on a fresh install.

## Upgrade notes
- If RynthSuite stays in `C:\Games\RynthSuite`, nothing changes.
- To move RynthSuite, re-run the installer and pick the new folder on the **Select RynthSuite Location** page.
  Copy your existing `RynthAi` data folder across first if you want to keep your settings.
- Roll back by copying `PreviousReleases\0.5.2\RynthCore.Plugin.RynthAi.dll` back while the game is closed.
