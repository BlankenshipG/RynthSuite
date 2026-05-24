# RynthAi **0.6.9** — VirindiTank import: active loot + MyMonsters .usd for current character (2026-04-23)

## Summary

Dashboard **Import VirindiTank profiles** now aligns with the external **Monster Editor** import story for the **logged-in character**:

1. **Loot (items):** After copying `.utl` files into `LootProfiles`, the **last copied file** becomes **`CurrentLootPath`** so VT loot rules apply immediately. **`RynthAiPlugin`** clears in-memory VT / native loot caches via **`AfterVirindiTankImport`** so the next loot evaluation reloads from disk.

2. **Monsters:** In addition to **`monsters.json`** under VirindiTank trees, the importer scans **text** **`.usd`** profiles for a **`MyMonsters`** SCF block (same layout as **`VirindiTankUsdTextScfParser`** in **MonsterEditor**) and merges into **`LegacyUiSettings.MonsterRules`** using the same name / match-expression dedupe and **Default** row combat copy as the external editor flow. **SQLite** `.usd` files are **skipped** in-plugin (log line); use **Monster Editor → Import Virindi .usd** for those.

## Files

- **`ProfileImport/VirindiTankUsdMyMonstersScfParser.cs`** — SCF parser targeting **`LegacyUi.MonsterRule`**.
- **`ProfileImport/VirindiTankProfileImporter.cs`** — `.usd` pass, **`CopiedLootDestPaths`**, merge helpers.
- **`LegacyUi/LegacyDashboardRenderer.cs`** — activate copied loot, **`AfterVirindiTankImport`** hook.
- **`RynthAiPlugin.cs`** — cache invalidation + version string.

## Version

- **`RynthCore.Plugin.RynthAi`** **0.6.8 → 0.6.9**
