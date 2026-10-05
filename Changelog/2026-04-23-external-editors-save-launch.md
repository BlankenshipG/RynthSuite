# Changelog — External Monster/Loot editors: launch paths, Save UI (2026-04-23)

## Summary

- **Monster Editor launch:** RynthAi now searches **`RynthAi\Tools\MonsterEditor`**, **`RynthCore\Tools\MonsterEditor`** (sibling of `RynthSuite`), and legacy **`RynthAi\MonsterEditor`** for `RynthCore.MonsterEditor.exe`. Passes **two** arguments: the per-character settings folder and the **`MonsterProfiles\<char>.json`** path the plugin uses (creates the folder if missing). Fixes wrong file target (`monsters.json` under settings vs `MonsterProfiles\*.json`).
- **Loot Editor:** Dashboard **“Open Loot Editor”** uses the same discovery for **`RynthCore.LootEditor.exe`** and passes the configured **LootProfiles** directory so Open/Save dialogs start in the right place.
- **Monster Editor UI:** Top **“Save now”** toolbar button (grid edits still auto-save).
- **Loot Editor UI:** Toolbar **Save** button mirroring **File → Save**.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/SuiteToolPaths.cs` (new)
- `Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyDashboardRenderer.cs`
- `Tools/RynthCore.MonsterEditor/MainWindow.axaml`, `MainWindow.axaml.cs`
- `Tools/RynthCore.LootEditor/MainWindow.axaml`, `MainViewModel.cs`
