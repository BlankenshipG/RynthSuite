# Add to loot profile: an attribute checklist for every item

RynthAi 0.5.87-legacy-ui (build 2026.10.18.10), with engine 2026.10.18.10

"Add to loot profile" (the Inventory panel's right-click, the Loot Editor's "Add selected item")
used to give three match modes plus one "Include T11 attributes" box:
- "Items like this" always added the same handful of properties.
- Spells were required by exact name, at most four.
- Pet essences got nothing beyond their class.

The popup now lists every attribute the item has, grouped, and you tick what the rule requires.
This works for all gear, T11 or not, and for essences.

## The checklist

| Group | Rows (when the item has them) |
|---|---|
| Item | material, workmanship, value, burden, spellcraft, max mana, Arcane Lore to use, uses left / max uses, armor set, imbue, slayer, damage type |
| Weapon / Armor | wield skill, weapon skill, max damage, damage variance, damage / attack / melee, missile, magic defense / mana conversion modifiers, elemental damage vs monsters, equip slots, armor level |
| Ratings | the rating total, then each rating (damage, damage resist, crit, crit resist, crit damage, crit damage resist, heal boost, nether resist, vitality) |
| Spells | each spell, "Legendary cantrips", "Epic-or-better cantrips" and so on, and the spell count |
| Pet essence | bond level, level, potency stored, summoning mastery, mutations (total and each kind), captured damage type, the pet ratings, breeding charges |
| T11 | Is T11, tier, weapon grade, damage %, gear grade, each modifier, slot special, Cast on Strike count, then modifier count, modifier rating total and best roll % (off by default) |
| Requirements | wield requirement, wield difficulty |

- **What a row shows:** its tick, its comparison, and the value. You can pick `>=`, `=` or `<=` (`=` or `not` for named values such as material or mastery). The value shows by name where the property has names ("Gold", "Primalist", "Armor Rending").
- **Choosing a comparison or typing a value** ticks the row. Each group header has "all" and "none".
- **Spells** take "or better" (Major Strength also matches Epic and Legendary Strength; Blood Drinker Self VI also matches VII and VIII), "exact", or "any tier". Cantrips default to "or better", and leveled spells to exact, as before.
- **"At least N cantrips of a tier or better"** rows use VTank's Spell Match condition.
- **There is no limit of four spells** once you choose.
- **Pet essences** are recognised the way the Pets tab recognises them: by WCID, the summoning-gem icon, a Summoning use requirement, or ACECustom bond and potency.
- **Defaults:** "Items like this" pre-ticks what it always required (material, workmanship, the weapon or armor stats, the rating total, the first four spells), plus bond, level, potency, mastery and mutations on an essence. Name + class and Name only start with nothing ticked.
- **Unchanged:** with no choices (an older engine, `/ra loot add`), the rules are exactly what they were. That includes `/ra loot add t11`.
- **Native (JSON) profiles** still can't hold spell conditions; the popup lists the ones it left out.

## Loot Editor names

The condition pickers name the new keys:
- the pet essence keys (9053-9075, the pet ratings 307-316);
- summoning mastery (362) and the use requirements (364-369);
- spellcraft (106), max mana (108), Arcane Lore (109), the armor set (265);
- Decal's imbue key (179).

Imbue (353), summoning mastery and captured damage type pick their value by name.

Four labels were wrong and are fixed:
- 87 and 88 are MaxLevelRestrict and LockpickSkillBonus, not MaxStructure and Structure.
- The uses are 91 (UsesTotal) and 92 (UsesRemaining).
- 107 is the current mana (ItemCurMana); max mana is 108.

## Tests

- LootSdkTests: the new checklist tests: rows and groups, choices, typed values, spell tiers, cantrip counts, pet essences, the native format, the wire round-trip and the vocabulary.
- The existing "like this" and T11 tests pass unchanged: 8240 assertions, 0 failed.
- RynthAiHostTests, loot: 128 passed.
