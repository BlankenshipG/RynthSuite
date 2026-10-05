# RynthAi 0.6.12 — Shields in Items for the secondary (off) hand

**Date:** 2026-10-04
**Branch:** `SK-local`
**Previous release:** RynthAi 0.6.11 (UB mob seed)

## Added
- **Items window → "Shields (off-hand)" section** (`LegacyWeaponsUi`).
  - "Add Selected Shield" adds the inventory-selected shield as an `ItemRule` with
    `Action = "Shield"`. A shield already listed as a weapon (e.g. from an older VT import) is
    re-tagged instead of duplicated.
  - Shields table with "(Gone)" marking and Delete. Deleting a shield also clears any Monsters
    rule `Offhand` pointing at it and saves `monsters.json`.
  - **"Auto-equip with one-handed melee weapons"** checkbox (`AutoEquipShield`, default on).
- **`Combat\ShieldHelper`** — shield detection from the item's valid-equip-locations mask
  (STypeInt 9, `EquipMask.Shield = 0x00200000`), with a name fallback (Shield, Buckler, Aegis, …)
  for armor/unknown-class items whose mask hasn't been read yet. `AllowsShield(weapon)` = melee
  weapon without the two-handed bit (`0x02000000`).
- **Combat off-hand equip** (`CombatManager.TryEquipOffhandShield`). Once the main weapon is in
  hand and Melee stance is reached, combat wields the shield for the target:
  1. Damage panel per-monster **Offhand** override (this was stored but never used before);
  2. Monsters tab rule **Offhand**;
  3. with auto-equip on, the first shield in Items → Shields that is carried.
  Picks that aren't shields, or are no longer listed in Items, are ignored.
  - Only with one-handed melee weapons — never with two-handers, bows/crossbows/atlatls or casters.
  - Best effort, never blocks an attack. It shares the weapon swap gate (3 s between equips), has
    one attempt in flight, a 3 s resolve timeout and a 15 s cooldown after a miss. It gives up on
    a shield after 3 misses, with a single chat warning, so an unwieldable shield can't jam the item
    queue. Logged as `[ShieldDiag]`.

## Changed
- Shields are never chosen as the **main-hand weapon**: element/first-weapon selection, the Damage
  panel weapon override, the `/ra combat` snapshot and both wand finders skip them.
- **Monsters tab:** the Weapon picker hides shields; the Offhand picker lists only shields (tooltip added).
- **Dashboard / Damage panel pickers** show shields as `Name [Shield]`.
- **VT/ILT USD import:** rows from the `shields` table, or items that wield into the shield slot,
  are imported as `Shield` entries instead of `Weapon` (previously combat could try to fight with them).

## Fixed
- The engine-side (Avalonia) Items panel only round-trips id/name/element. Saving from it replaced
  the whole list and reset every entry's `Action` and `KeepBuffed`. `ApplyItemsJson` now keeps both
  from the existing entries, so shield tags and "keep buffed" survive edits made there.

## Notes
- Existing profiles keep working: shields previously stored as weapons are still recognised by
  their equip mask and kept off the main hand. Re-add them with "Add Selected Shield" (or delete and
  re-import) to make them available to the off-hand pickers.

## Build
- RynthAi `0.6.12` (`VersionPointer` `0.6.12-legacy-ui`): `dotnet build` and NativeAOT
  `dotnet publish -c Release` succeed; no new warnings or IL trim warnings.
