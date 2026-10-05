# RynthAi 0.6.13 — Mag-style item info (`/ra iteminfo`)

**Date:** 2026-10-04
**Branch:** `SK-local`
**Previous release:** RynthAi 0.6.12 (off-hand shields)

## Added
- **Mag-style item info line**, ported from UtilityBelt ub-IT (`Tools/ItemInfo.cs`,
  `Lib/ItemDescribeMagShorthand.cs`, `Lib/ItemInfoHelper/*`) and Mag-Tools `ItemInfo`. One chat line
  per item, e.g.

  `Ornate Long Sword (Slash Sword), Noble Relic Set, CS, Undead Slayer, Tinks 4, Applied: Steel x2,
  152.48-236, 0.35v, +18%a, 12%md, Legendary Blood Thirst, Wield Lvl 180, Heavy Weapons 375,
  Diff 270, Craft 8, [D 3, CD 2]`

  Fields, in ub-IT order, gated per item class (melee / missile / caster / armor / jewelry):
  - `[Material] Name (DamageType Mastery)`, equipment set, `AL`.
  - **Imbues:** CS, CB, AR, Slash/Pierce/Bludge/Acid/Frost/Light/Fire/Nether Rend,
    Melee/Missile/Magic Imbue, Hematited, MagicAbsorb (ImbuedEffect 179 plus 303–306).
  - Armor cleaving (`AC`), CritMult / CritFreq, split arrows, missile range (yards),
    Cleaving / Multi-Strike.
  - **Slayer** (full ACE creature type list), tinks, and the applied tinker materials (ACECustom TinkerLog).
  - **Damage:** `min-max`, variance `v`, `+elemental`, `+damage mod %`, caster `+N% vs. Monsters`.
  - `%a`, `%md`, `%mgc.d`, `%msl.d`, `%mc`.
  - **Spells**, using Mag's filter: cantrips, Augmented spells, level 7+ weapon auras, and
    Impen/banes only on unenchantable gear. I–VI, level-7 lore buffs and Incantations are hidden.
  - Wield requirements (both sets), use level, "to Activate" skill, summoning skill/spec, `Diff`,
    `Craft` (or salvage `Work` average), base protections for unenchantable armor.
  - Optional `Value` / `BU`.
  - **Ratings cluster** `[D, DR, C, CD, CR, CDR, HB, V, NR, LR, PKD, PKDR, OP, OPR]`
    (ACE gear ratings 370–389).
  - Keyring keys/uses.
- **Commands**
  - `/ra iteminfo` (alias `/ra ii`) describes the selected item. If it has no appraisal yet, an ID is
    requested and the line prints when the ID arrives. After 3 s it prints whatever is known,
    tagged "(not identified — stats may be incomplete)".
  - `/ra iteminfo on|off` describes every item you select (items only, never monsters/NPCs/doors).
  - `/ra iteminfo value on|off` appends value and burden.
  - `/ra iteminfo verbose on|off` lists every spell (up to 26) instead of the Mag filter, including
    ids missing from SpellData.txt.
- **Advanced Settings → Display → "Item Info (Mag-style)"** checkboxes for the three options
  (`ItemInfoOnSelect`, `ItemInfoShowValueBurden`, `ItemInfoVerboseSpells`, all off by default,
  saved with the character settings).
- `SpellDatabase` now keeps each spell's family and difficulty (`TryGetSpellMeta`, `HasSpell`),
  which the spell filter needs.

## Files
- New: `ItemInfo/MagItemDescriber.cs`, `ItemInfo/MagItemInfoTables.cs`, `RynthAiItemInfo.cs`.
- Changed: `Combat/SpellDatabase.cs`, `RynthAiPlugin.cs` (command, tick poll, on-select hook),
  `RynthAiCommands.cs` (help line), `LegacyUi/LegacyUiSettings.cs`, `LegacyUi/LegacyAdvancedSettingsUi.cs`.

## Notes
- Properties are read as raw ACE ids (PropertyInt/Float/String) through the host, not Decal's
  remapped keys. Split arrows (9031) and TinkerLog (9007) are ACECustom/ILT properties. On other
  servers those fields just don't show.
- Not yet verified in game.

## Build
- RynthAi `0.6.13` (`VersionPointer` `0.6.13-legacy-ui`): `dotnet build` and NativeAOT
  `dotnet publish -c Release` succeed with no errors.
