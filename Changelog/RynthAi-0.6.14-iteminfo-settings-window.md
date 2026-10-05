# RynthAi 0.6.14 — Item Info settings window

**Date:** 2026-10-04
**Branch:** `SK-local`
**Previous release:** RynthAi 0.6.13 (Mag-style item info)

## Added
- **"RynthAi Item Info" window** (`LegacyUi/LegacyItemInfoUi.cs`). Open it with
  Advanced Settings → Display → **Item Info settings...**, or with `/ra iteminfo settings`.
  - **Preview:** the selected item's line, refreshed every 0.5 s and immediately after any option
    change. **Print to chat** (same as `/ra iteminfo`, IDs the item first) and **Refresh** buttons.
  - **When to print:** describe-on-select, with on-select filters for Weapons, Armor & clothing,
    Jewelry and Other items. ID wait slider (0.5–15 s, default 3).
  - **Fields:** one checkbox per section of the line, each with an example: material,
    damage type/mastery, set, AL, imbues, armor cleaving, crit mult/freq, splits, range,
    cleaving/multi-strike, slayer, tinks, applied materials, damage, attack, defenses,
    mana conversion, spells, wield reqs, activation/use reqs, Diff, craft/work, protections,
    ratings, keyring, plus value & burden. "All on" / "All off" buttons.
  - **Spells & ratings:** Mag filter or All spells (with a max-listed slider, 1–64). One checkbox
    per rating (D, DR, C, CD, CR, CDR, HB, V, NR, LR, PKD, PKDR, OP, OPR), with All/None.
  - **Chat output:** chat type/colour picker with a **Test** button, and an editable line prefix
    (default `[RynthAi] `, may be empty).
  - **Reset all to defaults.**
- **New commands** (same settings as the window):
  - `/ra iteminfo settings` (or `gui`/`window`) opens or closes the window.
  - `/ra iteminfo fields` lists every field and rating with its on/off state.
  - `/ra iteminfo field <name> on|off`, e.g. `field ratings off`, `field slayer on`.
  - `/ra iteminfo rating <tag> on|off`, e.g. `rating CDR off`.
  - `/ra iteminfo reset`.
  - Existing: `on|off`, `value on|off`, `verbose on|off`.

## Changed
- Item info options now live in one per-character object, `ItemInfoSettings`
  (`ItemInfo/MagItemInfoSettings.cs`). It replaces the three 0.6.13 flags (`ItemInfoOnSelect`,
  `ItemInfoShowValueBurden`, `ItemInfoVerboseSpells`). 0.6.13 was never released, so there is no
  migration.
- Field visibility is stored as a *hidden* bitmask (`HiddenFields`, `HiddenRatings`). Fields added
  in later versions show by default on existing profiles.
- Item info lines use the configured chat type and prefix (previously fixed type 1 and `[RynthAi] `).
- Advanced Settings → Display keeps the "Describe items when selected" checkbox. The other options
  moved to the new window.

## Fixed
- The item info options were missing from the settings-load copy (`CopySettings`), so after a
  reload or a character switch they went back to off. `ItemInfoSettings` is now copied and
  sanitized (clamps the timeout, spell cap, chat type and prefix length).

## Build
- RynthAi `0.6.14` (`VersionPointer` `0.6.14-legacy-ui`): `dotnet build` and NativeAOT
  `dotnet publish -c Release` succeed with no errors and no IL trim warnings.
- Not yet verified in game.
