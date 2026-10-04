# RynthAi 0.5.4 — In-game tool launch buttons

**Date:** 2026-10-04
**Branch:** `feat/ilt-hub` (testing only — not merged to `main`, not pushed to UB GitLab)

## Added
- **Tools row on the Items window** (dashboard → Items) and on **Advanced Settings → Looting**:
  - **Loot Editor** — opens `RynthCore.LootEditor.exe` from `<CoreDir>\Tools\LootEditor\`. If a loot
    profile is active (`CurrentLootPath`) and exists on disk, it is opened directly in the editor.
  - **Monster Editor** — opens `RynthCore.MonsterEditor.exe` from `<SuiteDir>\RynthAi\MonsterEditor\`
    pointed at the current character's settings folder.
- Clicking a button while that tool is already running brings its window to the front (restoring it
  if minimized) instead of starting a second copy.
- Missing executables or no character folder report a red chat message instead of failing silently.

## Changed
- New `LegacyUi/ExternalTool.cs` owns process launch/focus/close for external editors. It uses
  `ShellExecuteExW` / `WaitForSingleObject` / `EnumWindows` because `System.Diagnostics.Process`
  is not safe inside the NativeAOT host injected into acclient.exe.
- The existing Monsters-window toggle in the dashboard now uses `ExternalTool` (same behavior:
  toggles open/closed). Duplicate P/Invoke code removed from `LegacyDashboardRenderer`.
- Tool paths resolve through `RynthInstallPaths`, so they follow the user-chosen install folders.
- Plugin shutdown now releases both tool handles (`ReleaseExternalToolHandles`).

## Versions
- RynthAi `0.5.3` → `0.5.4` (`0.5.4-legacy-ui`). Previous DLL saved to
  `C:\Games\RynthSuite\RynthAi\PreviousReleases\0.5.3\`.
- Shipped in installer `RynthCore-Setup-2026.10.4.6.exe`.
