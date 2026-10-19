# RynthOracle: Gear tab, with Overlap and Ratings (2026-10-20)

RynthOracle 0.7.0, ProductVersion 2026.10.20.1 (a new feature, so a minor bump and a new day series).
Previous release: RynthOracle 0.6.4 (2026.10.19.5).

## New: the Gear tab

UB-ILT's Character tab had a "Gear overlap" view and a "Paragon gear" view
(`LeaftideView.SpellOverlapTab.cs`, `LeaftideView.ParagonGearTab.cs`). Both are now the **Gear** tab
in RynthOracle, after Progress, with two views: **Overlap** and **Ratings**. Nothing is read until
**Check** is pressed (or Refresh, or `/ro gear` to open the tab).

### It works on every server

UB-ILT's version read its spell facts from a catalog exported from ACECustom's database. The Gear
tab reads them from the client's own `portal.dat` spell table (file 0x0E00000E): each spell's
family, power, type and target. Every server's client carries the spells it can show, custom ones
included. The equipment-set bonuses come from the same file (its SpellSet table), resolved the way
ACE's `GetSpellSet` does. Spell family and set names come from ACE's `SpellCategory` and
`EquipmentSet` enums. When `portal.dat` can't be read, the tab falls back to RynthOracle's
embedded retail table (`SpellData.txt`) and says so.

The items, their spells and the character's enchantments all come from the client:
- the engine's equipment record (API 87; older engines: every object whose wielder is you);
- each item's appraisal. Items the client hasn't appraised are appraised one at a time, 150 ms
  apart, and each item at most once in 5 minutes;
- the player's enchantment registry.

### Overlap

- **Summary.** When it was checked, items worn, spells, and the overlap / duplicate / stack counts.
  How many of the 7 life wards and 7 banes are Legendary, which elements are missing, and the set,
  paragon and buff counts.
- **Life / Crit protection.** For each element, the ward (on you) and the bane (on your armour) that
  wins, its tier (Minor, Major, Epic, Legendary; missing in red), where it comes from, and what it
  beats. Element names use the damage-type colours.
- **Equipped gear spells.** Set bonuses (with the pieces), paragon spells, then every worn piece with
  its spells: inscribed, and active on the item (a bane cast on it, Blood Drinker). Each row shows
  family and power. The character's buffs are in a folded section.
- **Overlap / stacking.** The families where spells meet, with **Hide overlap**:
  - *Overlap*: different spells of one enchantment family. Only the highest power works; the rest
    are marked "(beat)". Spells cast on items are checked per item.
  - *Duplicate* (new, not in UB-ILT): the same spell on more than one item. One copy works; the
    others are marked "(extra)".
  - *Stacks*: spells that aren't enchantments (procs, for example). These combine.

### Ratings

UB-ILT's Paragon gear view, for any server: every item with an equipment set, an item level or gear
ratings (Damage, Damage resist, Crit, Crit damage and the rest, PropertyInt 370-389). Each row shows
where it is (Worn or Pack), its type with the weapon's element, its set, its level as
current / maximum, and its ratings. Weapon rows are tinted by damage type. Paragon sets and paragon
weapons are marked where the server has them. **Worn totals** sums each rating over what is worn.
**Include pack items** adds the packs' gear (at most 200 items appraised per Check).

## Code

- `Data/PortalSpellBook.cs`: the `portal.dat` spell table and SpellSet reader (background load).
- `Data/GearNames.cs`, `Resources/spellcategories.csv`, `Resources/equipmentsets.csv`: names, from
  `Tools/RynthOracle.QuestPackGen/Make-GearNames.ps1` (ACE's enums).
- `Data/GearAnalysis.cs`: the overlap, ward / bane, set and ratings analysis (pure).
- `Data/GearScanner.cs`: the Check (items, appraisals, buffs).
- `Views/GearTab.cs`: the tab. `/ro gear` (or `/ro overlap`) opens it.
- `SpellTable` (SpellData.txt) also keeps each spell's family and power, for the fallback.
- The plugin references `RynthCore.TerrainData` (the dat reader RynthAi already uses).

## Tests

- **RynthOracle.Tests** 539/539, 69 of them new:
  - a hand-built spell table and a cut-short one, and the real `client_portal.dat` when present
    (6,266 spells, 139 sets);
  - set tiers between and past the top tier, and item-level formulas;
  - family and set names;
  - Overlap, Duplicate, Stacks and per-item rows; wards and banes by tier and missing elements;
    count-based and leveled set bonuses;
  - the Ratings rows and worn totals;
  - both views through the engine's display-list parser.
