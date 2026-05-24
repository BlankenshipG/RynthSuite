# RynthAi 0.6.7 — missile auto-combat launcher vs ammo detection

## Previous release

- **0.6.6** — ImGui monsters table column flags (`LegacyMonstersUi`).

## Summary

Auto-combat could refuse to fire with a **crossbow** (and sometimes other missile setups) even when ammo was equipped. Two causes were addressed:

1. **False “ammo” match on launchers** — normalized names like `heavycrossbow` contain the substring `bolt`, so crossbow weapons were misclassified as crossbow **ammunition**. That broke `HasWieldedAmmoMatchingKind` / stance gating.
2. **Launcher vs ammo ordering** — loose stacks often share `AcObjectClass.MissileWeapon` with the launcher. The first matching wielded item in enumeration could be **arrows/quarrels**, so the derived “weapon kind” was wrong. `TryGetWieldedMissileKind` now skips items that look like loose ammo for any launcher kind.
3. **Wield slot fallback** — `IsPlayerWielded` now falls back to `LongValueKey.CurrentWieldedLocation` when cache wield location is zero but the client still reports a wield slot.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Combat/MissileAmmoHelper.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Combat/MissileCraftingManager.cs` (same launcher resolution rules)
- `Plugins/RynthCore.Plugin.RynthAi/RynthCore.Plugin.RynthAi.csproj` — version **0.6.7**
