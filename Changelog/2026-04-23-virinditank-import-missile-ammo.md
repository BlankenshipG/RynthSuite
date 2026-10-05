# Changelog — VirindiTank profile import and missile ammo rules (2026-04-23)

## Summary

- **VirindiTank import (dashboard):** New **“Import VirindiTank profiles”** button next to the loot profile area. Scans common install paths (for example `C:\Games\VirindiPlugins\VirindiTank` and `Documents\VirindiPlugins\VirindiTank`), copies **`*.utl`** loot files into `LootProfiles`, and **merges** any **`monsters.json`** files that deserialize as Rynth `MonsterRule` lists (skips duplicate rule names and skips overwriting `Default`).
- **Missile ammunition (Items window):** Optional **ammo stacks** with launcher category (**Auto / Bow / Crossbow / Atlatl**), plus **“Inventory rules only”** — when enabled, auto-equip only uses these stacks (and per-monster overrides), not a full loose-ammo scan.
- **Per-monster ammo (Monsters window):** New **Ammo** column: pick from the ammo list, **`<AUTO>`**, or **Sel** with loose ammo selected in inventory (`PreferredAmmoItemId` on `MonsterRule`).
- **Combat:** Wielded ammo must **match** the equipped launcher (arrows vs quarrels/bolts vs darts). `CombatManager` equips preferred / listed / best loose ammo before missile stance when the missile weapon is already wielded. `MissileCraftingManager` uses the same classification helper.
- **Monster Editor:** **Ammo Id** column for `PreferredAmmoItemId`.
- **Plugin version string:** `0.6.0-legacy-ui`.

## Files of note

- `Plugins/RynthCore.Plugin.RynthAi/ProfileImport/VirindiTankProfileImporter.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Combat/MissileAmmoHelper.cs`
- `Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyWeaponsUi.cs`, `LegacyMonstersUi.cs`, `LegacyDashboardRenderer.cs`, `LegacyUiSettings.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Combat/CombatManager.cs`, `MissileCraftingManager.cs`

## Limitations

- VirindiTank **monster lists** are not converted from VT’s internal profile format; only **Rynth JSON** `monsters.json` discovered under VT folders is merged. Loot **`.utl`** is native to RynthAi’s existing parser.
- Ammo selection uses **item names** and object ids (same style as consumables / weapons).
