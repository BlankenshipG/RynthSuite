# RynthAi 0.6.2 — external loot editor opens/saves the active profile

## Summary

RynthAi now passes the **active native loot .json** path (when set) to **RynthCore.LootEditor**, matching the two-argument style used for the Monster editor. The loot editor loads or binds that file so **Save** / **Ctrl+S** writes the same file the plugin uses for import/reload, instead of an unrelated “Untitled” document.

## Changes

- `LegacyDashboardRenderer.LaunchLootEditor()`: `Arguments` are `"<LootProfiles folder>"` and, when `CurrentLootPath` ends in `.json`, the **full path** to that profile.
- `RynthCore.LootEditor` `MainViewModel.TryApplyStartupCommandLine()`: if **argv[2]** is a `.json` path, open it; if the file is missing, create on first **Save** at that path (and ensure the parent directory exists).
- **Loot** and **Monster** standalone windows: **Ctrl+S** triggers save; loot toolbar label/tooltip clarifies that save targets the RynthAi profile file when launched from the plugin.

## Versioning

- `RynthCore.Plugin.RynthAi.csproj`: `Version` **0.6.2**, `FileVersion` / `AssemblyVersion` **0.6.2.0**, `InformationalVersion` **0.6.2-legacy-ui-loot-editor-launch**
- `RynthAiPlugin.cs` reported version: **0.6.2-legacy-ui**
- `RynthCore.LootEditor.csproj` / `RynthCore.MonsterEditor.csproj`: **0.1.1** (file **0.1.1.0**)

## Previous release

- Plugin **0.6.1-legacy-ui** and prior bundle notes remain described in `2026-04-23-external-editors-install-path.md` and related Changelog files.
