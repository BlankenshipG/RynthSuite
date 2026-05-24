# RynthAi 0.6.3 — Missile Combat section in advanced settings

## Summary

RynthAi **Advanced Settings** now includes a **Missile Combat** tab. It groups missile attack options, the **“inventory rules only”** control (the usual reason the bot “cannot find” loose ammo), ammo-rule counts, instructions for the Items panel, and missile crafting status. The **Crafting** tab is reduced to a shortcut so missile automation is configured in one place.

## What was wrong (common case)

- **“Inventory rules only (no auto-scan for loose ammo)”** was only visible under the **Items** panel. When enabled with **no** ammo rows in the list, combat will not equip any stacks — the scan that finds arrows/quarrels/darts in the main pack is skipped. The new tab surfaces this and shows a **warning** when the list is empty.

## Changes

- `LegacyUiSettings.AdvancedTabs`: added **Missile Combat** (after Melee Combat). Saved `SelectedAdvancedTab` indices for tabs after Melee may shift by one; pick the tab again once if the wrong panel opens.
- `LegacyAdvancedSettingsUi`: new **Missile Combat** case; Melee no longer shows missile power/height; **Crafting** case links to the Missile tab.

## Versioning

- Plugin `Version` / file version **0.6.3**; `RynthAiPlugin` string **0.6.3-legacy-ui**

## Previous release

- **0.6.2** — `2026-04-22-loot-editor-rynthai-active-profile.md` and related notes.
