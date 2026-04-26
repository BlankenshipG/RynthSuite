# RynthAi 0.6.1 — external editor paths from RynthCore install

## Fix

- **`SuiteToolPaths`:** `LootEditor` / `MonsterEditor` are now resolved by walking **up** from the **loaded plugin assembly path** and looking for `Tools\MonsterEditor\RynthCore.MonsterEditor.exe` and `Tools\LootEditor\RynthCore.LootEditor.exe` (same layout as the **RynthBundle** installer: `{app}\Tools\...` next to `RynthCore.exe`).
- The previous RynthAi-settings-based search (sibling `Games\RynthCore\Tools\` from the profile data tree) is still used as a **fallback** for older or dev-only layouts.
- The plugin’s reported version string is **0.6.1-legacy-ui** (`RynthAiPlugin.cs`).

## Rationale

Profile and settings roots can point at different drives or repo checkouts, so they are unreliable for finding the **installed** editor exes. The in-process plugin DLL is always loaded from `Runtime\Plugins\` (or a shadow path under the same install), so its directory chain reaches the RynthCore application root that contains `Tools\`.
